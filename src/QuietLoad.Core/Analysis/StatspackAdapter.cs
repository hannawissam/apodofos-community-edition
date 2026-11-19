// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text.RegularExpressions;
using QuietLoad.Core.Model;

namespace QuietLoad.Core.Analysis;

/// <summary>
/// The shared rules were written for AWR and emit DBA_HIST_* queries, ASH snippets and AWR wording. For a Statspack report this
/// adapter (run once after the rules) swaps in the PERFSTAT.STATS$* equivalents, drops ASH-only snippets with an explanatory
/// note, maps the source-view names and replaces AWR wording, so a Statspack user is never told to query views they do not have.
/// </summary>
public static partial class StatspackAdapter
{
    private static readonly Dictionary<string, string?> ViewMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DBA_HIST_SNAPSHOT"] = "STATS$SNAPSHOT", ["DBA_HIST_SYSTEM_EVENT"] = "STATS$SYSTEM_EVENT", ["DBA_HIST_SYSSTAT"] = "STATS$SYSSTAT",
        ["DBA_HIST_SQLSTAT"] = "STATS$SQL_SUMMARY", ["DBA_HIST_SQLTEXT"] = "STATS$SQLTEXT", ["DBA_HIST_SQL_PLAN"] = "STATS$SQL_PLAN",
        ["DBA_HIST_OSSTAT"] = "STATS$OSSTAT", ["DBA_HIST_SYS_TIME_MODEL"] = "STATS$SYS_TIME_MODEL", ["DBA_HIST_SEG_STAT"] = "STATS$SEG_STAT",
        ["DBA_HIST_LATCH"] = "STATS$LATCH", ["DBA_HIST_UNDOSTAT"] = "STATS$UNDOSTAT", ["DBA_HIST_FILESTATXS"] = "STATS$FILESTATXS",
        ["DBA_HIST_TEMPSTATXS"] = "STATS$TEMPSTATXS", ["DBA_HIST_PARAMETER"] = "STATS$PARAMETER", ["DBA_HIST_PGASTAT"] = "STATS$PGASTAT",
        ["DBA_HIST_PGA_TARGET_ADVICE"] = "STATS$PGA_TARGET_ADVICE", ["DBA_HIST_DB_CACHE_ADVICE"] = "STATS$DB_CACHE_ADVICE",
        ["DBA_HIST_SGA_TARGET_ADVICE"] = "STATS$SGA_TARGET_ADVICE", ["DBA_HIST_SGASTAT"] = "STATS$SGASTAT", ["DBA_HIST_LIBRARYCACHE"] = "STATS$LIBRARYCACHE",
        ["DBA_HIST_WAITSTAT"] = "STATS$WAITSTAT", ["DBA_HIST_ENQUEUE_STAT"] = "STATS$ENQUEUE_STATISTICS", ["DBA_HIST_RESOURCE_LIMIT"] = "STATS$RESOURCE_LIMIT",
        ["DBA_HIST_BG_EVENT_SUMMARY"] = "STATS$BG_EVENT_SUMMARY", ["DBA_HIST_THREAD"] = "STATS$THREAD", ["DBA_HIST_INSTANCE_RECOVERY"] = "STATS$INSTANCE_RECOVERY",
        ["DBA_HIST_BUFFER_POOL_STATISTICS"] = "STATS$BUFFER_POOL_STATISTICS",
        ["DBA_HIST_ACTIVE_SESS_HISTORY"] = null, ["DBA_HIST_IOSTAT_FUNCTION"] = null, ["DBA_HIST_IOSTAT_FILETYPE"] = null, ["DBA_HIST_RSRC_PLAN"] = null,
        ["DBA_HIST_INTERCONNECT_PINGS"] = null, ["DBA_HIST_INST_CACHE_TRANSFER"] = null, ["CDB_HIST_SNAPSHOT"] = null
    };

    [GeneratedRegex(@"\b(?:DBA|CDB)_HIST_[A-Z_]+", RegexOptions.IgnoreCase)]
    private static partial Regex HistView();

    [GeneratedRegex(@"sql_id\s*(?:=>|=)\s*'(?<id>[^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex SqlIdLiteral();

    [GeneratedRegex(@"event_name\s*=\s*'(?<ev>(?:[^']|'')+)'", RegexOptions.IgnoreCase)]
    private static partial Regex EventLiteral();

    private static readonly (Regex Pattern, string Replacement)[] Wording =
    [
        (new Regex(@"AWR/ASH report", RegexOptions.IgnoreCase), "Statspack report"),
        (new Regex(@"DBMS_XPLAN\.DISPLAY_AWR", RegexOptions.IgnoreCase), "sprepsql.sql"),
        (new Regex(@"awrrpt\.sql", RegexOptions.IgnoreCase), "spreport.sql"),
        (new Regex(@"ashrpt\.sql", RegexOptions.IgnoreCase), "sprepsql.sql / shorter snapshot intervals"),
        (new Regex(@"\bAWR report\b", RegexOptions.IgnoreCase), "Statspack report"),
        (new Regex(@"\bAWR snapshots?\b"), "Statspack snapshot"),
        (new Regex(@"\bAWR repository\b"), "PERFSTAT repository"),
        (new Regex(@"\bAWR\b"), "Statspack"),
    ];

    private const string AshNote = "Statspack does not sample sessions (no ASH). To find the SQL and sessions behind this, take Statspack snapshots at level 7 every 10-15 minutes around the problem, then read the SQL and segment sections of spreport.sql, or run sprepsql.sql for one statement.";

    private const string AwrToolsNote = "AWR-based tools (SQL Tuning Advisor tasks over snapshot ranges, plan baselines loaded from AWR) are not available from Statspack data. If the statement is still in the shared pool, run them from the cursor cache (Enterprise Edition with the Tuning Pack); otherwise use sprepsql.sql to see its plan and statistics.";

    public static void Adapt(AnalysisContext ctx)
    {
        foreach (var f in ctx.Findings.Where(f => f.Category != "Statspack")) Adapt(ctx, f);
    }

    private static void Adapt(AnalysisContext ctx, Finding f)
    {
        var dropped = false;
        var droppedAwrTools = false;
        var kept = new List<SqlSnippet>();
        var extra = new List<SqlSnippet>();
        foreach (var s in f.Sql)
        {
            var code = s.Code;
            if (IsAwrTuningCode(code)) { droppedAwrTools = true; continue; }
            var hist = code.Contains("dba_hist_", StringComparison.OrdinalIgnoreCase) || code.Contains("DISPLAY_AWR", StringComparison.OrdinalIgnoreCase)
                       || code.Contains("LOAD_PLANS_FROM_AWR", StringComparison.OrdinalIgnoreCase) || code.Contains("DBMS_WORKLOAD_REPOSITORY", StringComparison.OrdinalIgnoreCase)
                       || code.Contains("cdb_hist_", StringComparison.OrdinalIgnoreCase);
            if (!hist)
            {
                var caution = s.Caution;
                if (code.Contains("DBMS_SQLTUNE", StringComparison.OrdinalIgnoreCase) || code.Contains("DBMS_SPM", StringComparison.OrdinalIgnoreCase))
                    caution = (caution is { Length: > 0 } ? caution + " " : "") + "Needs Enterprise Edition (SQL Tuning Advisor also needs the Tuning Pack); not available on Standard Edition.";
                kept.Add(s with { Code = code, Caution = caution });
                continue;
            }
            var replacement = Replace(ctx, s);
            if (replacement != null) extra.Add(replacement);
            else dropped = true;
        }
        // The same replacement can be produced by several snippets of a finding; keep distinct ones.
        foreach (var e in extra)
            if (!kept.Any(k => k.Code == e.Code)) kept.Add(e);
        f.Sql = kept;

        f.SourceViews = f.SourceViews
            .Select(v => HistView().Replace(v, m => ViewMap.TryGetValue(m.Value, out var n) ? n ?? "" : ""))
            .Select(v => v.Trim()).Where(v => v.Length > 0 && v != "()").Distinct().ToList();

        f.Plain = Reword(f.Plain);
        f.WhyItMatters = Reword(f.WhyItMatters);
        f.Technical = Reword(f.Technical);
        f.Title = Reword(f.Title);
        f.Recommendations = f.Recommendations
            .Select(r => Regex.IsMatch(r, @"\bASH\b|ashrpt") && !r.Contains("Statspack", StringComparison.OrdinalIgnoreCase) && r.Contains("ASH") ? AshNote : Reword(r))
            .Select(r => r.Contains("SQL Tuning Advisor", StringComparison.OrdinalIgnoreCase) && !r.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)
                ? r.TrimEnd('.') + " (needs Enterprise Edition with the Tuning Pack; on Standard Edition use sprepsql.sql and an execution plan review)." : r)
            .Distinct().ToList();
        f.Sql = f.Sql.Select(s => s with { Title = Reword(s.Title), Caution = s.Caution == null ? null : Reword(s.Caution) }).ToList();
        if (dropped && !f.Recommendations.Contains(AshNote)) f.Recommendations.Add(AshNote);
        if (droppedAwrTools) f.Recommendations.Add(AwrToolsNote);
    }

    private static string Reword(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = HistView().Replace(text, m => ViewMap.TryGetValue(m.Value, out var n) && n != null ? n : "the Statspack tables");
        foreach (var (p, r) in Wording) text = p.Replace(text, r);
        text = text.Replace("(the Statspack tables)", "(PERFSTAT tables)").Replace("the Statspack tables", "the PERFSTAT tables");
        text = text.Replace("Statspack Pack", "Diagnostics Pack");
        return text;
    }

    /// <summary>Statspack equivalent of an AWR snippet, or null when there is none (ASH, IOSTAT, plan baselines from AWR).</summary>
    private static SqlSnippet? Replace(AnalysisContext ctx, SqlSnippet s)
    {
        var code = s.Code;
        if (code.Contains("active_sess_history", StringComparison.OrdinalIgnoreCase)) return null;

        if (code.Contains("dba_hist_system_event", StringComparison.OrdinalIgnoreCase) && EventLiteral().Match(code) is { Success: true } ev)
            return new SqlSnippet("Trend of this wait across snapshots (STATS$SYSTEM_EVENT)",
                $"""
                SELECT s.snap_id,
                       TO_CHAR(s.snap_time, 'YYYY-MM-DD HH24:MI') AS snap_time,
                       e.total_waits - LAG(e.total_waits) OVER (ORDER BY s.snap_id) AS waits,
                       ROUND((e.time_waited_micro - LAG(e.time_waited_micro) OVER (ORDER BY s.snap_id)) / 1e6, 1) AS seconds_waited
                FROM   perfstat.stats$system_event e
                       JOIN perfstat.stats$snapshot s
                         ON s.snap_id = e.snap_id AND s.dbid = e.dbid AND s.instance_number = e.instance_number
                WHERE  e.event = '{ev.Groups["ev"].Value}'
                AND    e.instance_number = &inst_num
                AND    s.snap_id BETWEEN &begin_snap - 1 AND &end_snap
                ORDER  BY s.snap_id;
                """, "The counters are cumulative since instance startup; the LAG() difference gives the activity per snapshot interval. Run it as PERFSTAT (or a user with SELECT on the STATS$ tables).");

        if (code.Contains("dba_hist_snapshot", StringComparison.OrdinalIgnoreCase) && !code.Contains("dba_hist_sys", StringComparison.OrdinalIgnoreCase)
            && !code.Contains("dba_hist_osstat", StringComparison.OrdinalIgnoreCase) && !code.Contains("DBMS_WORKLOAD", StringComparison.OrdinalIgnoreCase))
            return SnapshotList();

        if (code.Contains("dba_hist_sys_time_model", StringComparison.OrdinalIgnoreCase) || code.Contains("dba_hist_osstat", StringComparison.OrdinalIgnoreCase))
            return new SqlSnippet("Host CPU and DB time per snapshot (STATS$OSSTAT / STATS$SYS_TIME_MODEL)",
                """
                SELECT sn.snap_id,
                       TO_CHAR(sn.snap_time, 'YYYY-MM-DD HH24:MI') AS snap_time,
                       tm.value AS cumulative_db_time_micro
                FROM   perfstat.stats$snapshot sn
                       JOIN perfstat.stats$sys_time_model tm
                         ON tm.snap_id = sn.snap_id AND tm.dbid = sn.dbid AND tm.instance_number = sn.instance_number
                       JOIN perfstat.stats$time_model_statname n ON n.stat_id = tm.stat_id
                WHERE  n.stat_name = 'DB time'
                AND    sn.snap_id BETWEEN &begin_snap - 1 AND &end_snap
                ORDER  BY sn.snap_id;
                """, "Values are cumulative since startup (microseconds); subtract consecutive snapshots to get the busy period. Time model data exists from Oracle 10g.");

        if (code.Contains("dba_hist_sysstat", StringComparison.OrdinalIgnoreCase))
            return new SqlSnippet("Statistic per snapshot (STATS$SYSSTAT)",
                $"""
                SELECT sn.snap_id,
                       TO_CHAR(sn.snap_time, 'YYYY-MM-DD HH24:MI') AS snap_time,
                       st.value - LAG(st.value) OVER (ORDER BY sn.snap_id) AS delta
                FROM   perfstat.stats$sysstat st
                       JOIN perfstat.stats$snapshot sn
                         ON sn.snap_id = st.snap_id AND sn.dbid = st.dbid AND sn.instance_number = st.instance_number
                WHERE  st.name = '{StatNameFrom(code)}'
                AND    sn.snap_id BETWEEN &begin_snap - 1 AND &end_snap
                ORDER  BY sn.snap_id;
                """, "Change the statistic name as needed (V$STATNAME). Counters are cumulative since startup.");

        if (code.Contains("dba_hist_sql_plan", StringComparison.OrdinalIgnoreCase) && ObjectLiteral().Match(code) is { Success: true } ob)
            return new SqlSnippet($"Statements whose plan uses {ob.Groups["o"].Value}.{ob.Groups["n"].Value} (STATS$SQL_PLAN)",
                $"""
                SELECT p.plan_hash_value, p.operation, p.options, COUNT(*) AS plan_steps, MIN(p.hash_value) AS hash_value
                FROM   perfstat.stats$sql_plan p
                WHERE  p.object_owner = '{ob.Groups["o"].Value}' AND p.object_name = '{ob.Groups["n"].Value}'
                GROUP  BY p.plan_hash_value, p.operation, p.options
                ORDER  BY plan_steps DESC;
                """, "Plans are stored from snapshot level 6; join the hash value to STATS$SQL_SUMMARY for the statistics. In a CDB run it inside the PDB.");

        if (code.Contains("q.module =", StringComparison.OrdinalIgnoreCase) && code.Contains("dba_hist_sqlstat", StringComparison.OrdinalIgnoreCase))
        {
            var mod = Regex.Match(code, @"q\.module\s*=\s*'(?<m>(?:[^']|'')+)'", RegexOptions.IgnoreCase);
            return new SqlSnippet("The SQL statements this program runs (same module, STATS$SQL_SUMMARY)",
                $"""
                SELECT *
                FROM  (SELECT ss.old_hash_value,
                              MAX(ss.executions) AS executions,
                              ROUND(MAX(ss.elapsed_time) / 1e6) AS elapsed_s,
                              MAX(ss.buffer_gets) AS buffer_gets
                       FROM   perfstat.stats$sql_summary ss
                       WHERE  ss.snap_id BETWEEN &begin_snap AND &end_snap
                       AND    ss.module = '{mod.Groups["m"].Value}'
                       GROUP  BY ss.old_hash_value
                       ORDER  BY elapsed_s DESC)
                WHERE ROWNUM <= 10;
                """, "Values are cumulative while the cursor stays cached; MAX over the range approximates the total for the period.");
        }

        if (code.Contains("dba_tab_statistics", StringComparison.OrdinalIgnoreCase)) return null;

        if (code.Contains("dba_hist_sqlstat", StringComparison.OrdinalIgnoreCase) || code.Contains("dba_hist_sqltext", StringComparison.OrdinalIgnoreCase)
            || code.Contains("DISPLAY_AWR", StringComparison.OrdinalIgnoreCase))
        {
            var id = SqlIdLiteral().Match(code) is { Success: true } m ? m.Groups["id"].Value : "&hash_value";
            if (id.StartsWith("&", StringComparison.Ordinal)) id = "&hash_value";
            return SqlHistory(id);
        }
        return null;
    }

    /// <summary>Tuning Advisor / SQL plan management snippets that read AWR snapshot ranges.</summary>
    private static bool IsAwrTuningCode(string code) =>
        code.Contains("SELECT_WORKLOAD_REPOSITORY", StringComparison.OrdinalIgnoreCase)
        || (code.Contains("DBMS_SQLTUNE", StringComparison.OrdinalIgnoreCase) && code.Contains("begin_snap", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"object_owner\s*=\s*'(?<o>[^']+)'\s+AND\s+p\.object_name\s*=\s*'(?<n>[^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex ObjectLiteral();

    private static string StatNameFrom(string code)
    {
        var m = Regex.Match(code, @"stat_name\s*=\s*'(?<n>[^']+)'", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["n"].Value : "user commits";
    }

    private static SqlSnippet SnapshotList() => new("List the Statspack snapshots (STATS$SNAPSHOT)",
        """
        SELECT snap_id, snap_level, TO_CHAR(snap_time, 'YYYY-MM-DD HH24:MI:SS') AS snap_time, startup_time, ucomment
        FROM   perfstat.stats$snapshot
        ORDER  BY snap_id DESC;
        """, "snap_level shows which sections each snapshot captured (5 or higher stores SQL, 7 adds segment statistics).");

    /// <summary>History of one statement from the SQL summary table (hash value in 9i/10g, SQL_ID in 11g and later).</summary>
    private static SqlSnippet SqlHistory(string id)
    {
        var numeric = id.All(char.IsDigit);
        var filter = numeric || id.StartsWith("&", StringComparison.Ordinal) ? $"ss.old_hash_value = {id}" : $"ss.sql_id = '{id}'";
        return new SqlSnippet("History of this statement per snapshot (STATS$SQL_SUMMARY)",
            $"""
            SELECT ss.snap_id,
                   TO_CHAR(sn.snap_time, 'YYYY-MM-DD HH24:MI') AS snap_time,
                   ss.plan_hash_value,
                   ss.executions,
                   ss.buffer_gets,
                   ss.disk_reads,
                   ROUND(ss.cpu_time / 1e6, 1)     AS cpu_seconds,
                   ROUND(ss.elapsed_time / 1e6, 1) AS elapsed_seconds,
                   ss.module
            FROM   perfstat.stats$sql_summary ss
                   JOIN perfstat.stats$snapshot sn
                     ON sn.snap_id = ss.snap_id AND sn.dbid = ss.dbid AND sn.instance_number = ss.instance_number
            WHERE  {filter}
            AND    ss.snap_id BETWEEN &begin_snap - 1 AND &end_snap
            ORDER  BY ss.snap_id;
            """, "Values are cumulative while the cursor stays in the shared pool, so subtract consecutive rows. Only statements above the snapshot thresholds are stored. For the plan run @?/rdbms/admin/sprepsql.sql and enter the begin/end snapshot and the hash value; the SQL_ID column of STATS$SQL_SUMMARY exists from Oracle 11g.");
    }
}
