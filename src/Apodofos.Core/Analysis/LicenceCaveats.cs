// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text.RegularExpressions;

namespace Apodofos.Core.Analysis;

/// <summary>
/// One central place that decides which generated SQL and advice needs an Oracle licence caveat. AWR / ASH repository
/// views and packages are part of the Diagnostics Pack; SQL Tuning Advisor, SQL Access Advisor and SQL Monitor belong to
/// the Tuning Pack. The engine runs <see cref="Apply"/> over every finding, so individual rules never have to remember it.
/// </summary>
public static partial class LicenceCaveats
{
    public const string Diagnostics = "Requires the Oracle Diagnostics Pack (AWR and ASH views); check your licence before running it.";
    public const string Tuning = "Requires the Oracle Tuning Pack (SQL Tuning Advisor, SQL Access Advisor, SQL Monitor), which itself needs the Diagnostics Pack; check your licence before running it.";

    [GeneratedRegex(@"\bDBA_HIST_\w+|\bDBMS_WORKLOAD_REPOSITORY\b|\bDISPLAY_AWR\b|\bG?V\$ACTIVE_SESSION_HISTORY\b|\bDBA_HIST\b|\bDBMS_ASH\b|\bAWR_(?:PDB|ROOT|CDB)_\w+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticsSql();

    [GeneratedRegex(@"\bDBMS_SQLTUNE\b|\bDBMS_ADVISOR\b|\bDBA_ADVISOR_\w+|\bDBMS_AUTO_SQLTUNE\b|\bDBMS_SQL_MONITOR\b|\bG?V\$SQL_MONITOR\w*|\bCREATE_TUNING_TASK\b|\bDBMS_SQLDIAG\b|\bDBA_SQLTUNE_\w+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TuningSql();

    [GeneratedRegex(@"SQL Tuning Advisor|SQL Access Advisor|\bSQL Monitor\b|\bSQL Tuning Set", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TuningText();

    [GeneratedRegex(@"DISPLAY_AWR|DBA_HIST_\w+|DBMS_WORKLOAD_REPOSITORY|ASH report|ashrpt|awrrpt|awrsqrpt", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticsText();

    // any statement of the licence requirement, in the uniform wording or in the older per-rule wording
    [GeneratedRegex(@"(?:requires|needs)\s+the\s+Oracle\s+(?:Diagnostics|Tuning)\s+Pack|(?:Diagnostics|Tuning)\s+Pack\s+licen[cs]e", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnyCaveat();

    [GeneratedRegex(@"\s*Needs the Oracle (?:Diagnostics|Tuning) Pack licen[cs]e(?:\s*\([^)]*\))?[;.]?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OldWording();

    /// <summary>True when this SQL touches AWR / ASH views or packages (Diagnostics Pack) or the tuning advisors (Tuning Pack).</summary>
    public static bool Needs(string? sql, out bool diagnostics, out bool tuning)
    {
        diagnostics = !string.IsNullOrEmpty(sql) && DiagnosticsSql().IsMatch(StripComments(sql));
        tuning = !string.IsNullOrEmpty(sql) && TuningSql().IsMatch(StripComments(sql));
        return diagnostics || tuning;
    }

    /// <summary>True when the caution already states the uniform licence requirement for what the SQL needs.</summary>
    public static bool IsCovered(string? sql, string? caution)
    {
        if (!Needs(sql, out var diag, out var tune)) return true;
        var c = caution ?? "";
        if (tune) return c.Contains("Requires the Oracle Tuning Pack", StringComparison.Ordinal);
        return c.Contains("Requires the Oracle Diagnostics Pack", StringComparison.Ordinal)
               || c.Contains("Requires the Oracle Tuning Pack", StringComparison.Ordinal);
    }

    private static string StripComments(string sql) => Regex.Replace(sql, @"--[^\n]*", " ");

    public static SqlSnippet Apply(SqlSnippet s)
    {
        if (IsCovered(s.Code, s.Caution)) return s;
        Needs(s.Code, out _, out var tune);
        var caution = OldWording().Replace(s.Caution ?? "", "").Trim();
        var caveat = tune ? Tuning : Diagnostics;
        return s with { Caution = caution.Length == 0 ? caveat : caution + " " + caveat };
    }

    /// <summary>Adds the caveat to every snippet that needs it, and to advice text that points at those features.</summary>
    public static void Apply(Finding f)
    {
        if (f.Sql.Count > 0) f.Sql = f.Sql.Select(Apply).ToList();
        for (var i = 0; i < f.Recommendations.Count; i++)
        {
            var r = f.Recommendations[i];
            if (AnyCaveat().IsMatch(r)) continue;
            if (TuningText().IsMatch(r)) f.Recommendations[i] = r.TrimEnd() + " (Requires the Oracle Tuning Pack.)";
            else if (DiagnosticsText().IsMatch(r)) f.Recommendations[i] = r.TrimEnd() + " (Requires the Oracle Diagnostics Pack.)";
        }
    }
}
