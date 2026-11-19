// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Globalization;
using System.Text.RegularExpressions;
using Apodofos.Core.Model;
using static Apodofos.Core.Analysis.AnalysisContext;

namespace Apodofos.Core.Analysis.Rules;

/// <summary>Shared helpers for the ASH rules: table access, severity from share + absolute load, version-aware SQL.</summary>
internal static class AshHelpers
{
    private static readonly string[] TimeFormats =
    [
        "dd-MMM-yy HH:mm:ss", "dd-MMM-yyyy HH:mm:ss", "d-MMM-yy HH:mm:ss", "MM/dd/yy HH:mm:ss", "MM/dd/yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "dd-MMM-yy HH.mm.ss"
    ];

    /// <summary>Table by exact title, falling back to a title prefix.</summary>
    public static AwrTable? T(AnalysisContext ctx, params string[] titles) => ctx.Report.Table(titles) ?? ctx.Report.TableStartingWith(titles);

    public static bool IsCpu(string ev) =>
        ev.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) || ev.Equals("ON CPU", StringComparison.OrdinalIgnoreCase);

    /// <summary>ASH shares are "% of all sampled activity" (the 10g layout calls the event share "% Activity").</summary>
    public static double Pct(AwrRow r) => r.Val("% Event", "% Activity") ?? 0;

    /// <summary>Sessions that were active on average because of a share of the samples (share x average active sessions).</summary>
    public static double Sessions(AnalysisContext ctx, double pct) => (ctx.Aas ?? 0) * pct / 100;

    /// <summary>Severity from the share of sampled activity and the absolute load: a big share of an almost idle database is not urgent.</summary>
    public static Severity Sev(AnalysisContext ctx, double pct, Severity? atMost = null, bool latencyEvidence = false)
    {
        var s = SeverityMath.ForLoad(ctx, SeverityMath.FromDbTimePct(pct));
        if (ctx.Info.AshSampleCount is < 100) s = SeverityMath.AtMost(s, Severity.Low);
        if (ctx.Aas is > 0)
        {
            var sess = Sessions(ctx, pct);
            if (sess < 0.1) s = SeverityMath.AtMost(s, Severity.Low);
            else if (sess < 0.5) s = SeverityMath.AtMost(s, Severity.Medium);
        }
        // A big share of samples is only a share: with fewer active sessions than CPUs and no latency evidence (ASH has no average
        // wait times) nothing proves users were slowed down, so the finding cannot be Critical (and not High when the load is light).
        if (!latencyEvidence && ctx.Cpus is > 0 && ctx.Aas is { } aas)
        {
            var ratio = aas / ctx.Cpus.Value;
            if (ratio < 0.5) s = SeverityMath.AtMost(s, Severity.Medium);
            else if (ratio < 1) s = SeverityMath.AtMost(s, Severity.High);
        }
        // a window under 5 minutes is a snapshot of a moment, not a period
        if (ctx.Info.ElapsedMinutes is > 0 and < 5) s = SeverityMath.AtMost(s, Severity.Medium);
        return atMost is { } m ? SeverityMath.AtMost(s, m) : s;
    }

    /// <summary>Data source view for the SQL snippets: the history view for reports built from DBA_HIST, V$ASH otherwise.</summary>
    public static string View(AnalysisContext ctx) => ctx.Info.AshFromHistory ? "dba_hist_active_sess_history" : "v$active_session_history";

    /// <summary>Expression that turns a sample count into approximate seconds of database time.</summary>
    public static string Weight(AnalysisContext ctx) => ctx.Info.AshFromHistory ? "COUNT(*) * 10" : "COUNT(*)";

    public static string Quote(string s) => (s ?? "").Replace("'", "''");

    /// <summary>"sample_time BETWEEN ..." for the analysed window; SQL*Plus substitution variables when the report's times cannot be converted.</summary>
    public static string Window(AnalysisContext ctx)
    {
        if (TryTime(ctx.Info.BeginSnapTime, out var b) && TryTime(ctx.Info.EndSnapTime, out var e))
            return $"sample_time BETWEEN TIMESTAMP '{b:yyyy-MM-dd HH:mm:ss}' AND TIMESTAMP '{e:yyyy-MM-dd HH:mm:ss}'";
        return "sample_time BETWEEN TO_TIMESTAMP('&begin_time', 'DD-MON-RR HH24:MI:SS') AND TO_TIMESTAMP('&end_time', 'DD-MON-RR HH24:MI:SS')";
    }

    private static bool TryTime(string text, out DateTime t) => Apodofos.Core.Parsing.ReportIntegrity.TryParseTime(text, out t);

    /// <summary>Row limit that works on every release: FETCH FIRST from 12.1, a ROWNUM wrapper on 11g.</summary>
    public static string Limit(AnalysisContext ctx, string select, int n) =>
        RuleHelpers.AtLeast(ctx, 12, 1)
            ? $"{select}\nFETCH FIRST {n} ROWS ONLY;"
            : $"SELECT * FROM (\n{select}\n) WHERE ROWNUM <= {n};";

    public const string HistoryCaution = "Needs the Oracle Diagnostics Pack license (ASH views). The window is the one analysed by this report; "
                                         + "use V$ACTIVE_SESSION_HISTORY for the last hour or so, DBA_HIST_ACTIVE_SESS_HISTORY (one sample in ten) for older periods.";

    /// <summary>Generic "who suffered this event" snippet.</summary>
    public static SqlSnippet ByEvent(AnalysisContext ctx, string ev) => new($"Which SQL and sessions waited on '{ev}' (ASH)",
        Limit(ctx, $"SELECT sql_id, sql_plan_hash_value, session_state, {Weight(ctx)} AS approx_db_seconds\nFROM   {View(ctx)}\nWHERE  {Window(ctx)}\nAND    {(IsCpu(ev) ? "session_state = 'ON CPU'" : $"event = '{Quote(ev)}'")}\nGROUP  BY sql_id, sql_plan_hash_value, session_state\nORDER  BY approx_db_seconds DESC", 10), HistoryCaution);

    /// <summary>Plan lookup snippet for a SQL_ID (AWR history first, cursor cache second).</summary>
    public static SqlSnippet Plan(string sqlId, int? planHash = null) => new($"Execution plan of {sqlId}",
        $"-- From the AWR repository (needs the statement to have been captured in a snapshot):\nSELECT * FROM TABLE(DBMS_XPLAN.DISPLAY_AWR('{Quote(sqlId)}'{(planHash is { } p ? $", {p}" : "")}, NULL, NULL, 'TYPICAL'));\n\n-- Or, while it is still in the shared pool:\nSELECT * FROM TABLE(DBMS_XPLAN.DISPLAY_CURSOR('{Quote(sqlId)}', NULL, 'TYPICAL'));");

    public static string SessionKey(string sid)
    {
        var m = Regex.Match(sid ?? "", @"\d+");
        return m.Success ? m.Value : (sid ?? "").Trim();
    }

    public static string SqlText(AnalysisContext ctx, string sqlId, string? fallback = null)
    {
        if (ctx.SqlTexts.TryGetValue(sqlId, out var t) && t.Trim().Length > 0) return t.Trim();
        return fallback ?? "";
    }

    public static string Short(string s, int max = 160)
    {
        s = Regex.Replace(s ?? "", @"\s+", " ").Trim();
        return s.Length <= max ? s : s[..max] + "...";
    }

    /// <summary>First data row per distinct value of a key column (rows repeat their parent key for each sub-line).</summary>
    public static List<AwrRow> Distinct(AwrTable t, params string[] keyColumns)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<AwrRow>();
        foreach (var r in t.DataRows)
        {
            var k = r.Str(keyColumns);
            if (k.Length == 0 || !seen.Add(k)) continue;
            list.Add(r);
        }
        return list;
    }

    public static string Pc(double v) => P(v, v < 10 ? 1 : 0);
}
