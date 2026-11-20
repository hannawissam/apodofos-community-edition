// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Core.Model;
using Apodofos.Core.Parsing;

namespace Apodofos.Core.Analysis;

/// <summary>
/// Finds values that cannot be true (a statement using 1,419% of DB time, host CPU above 100%) in the columns that
/// hold shares of a whole. Such cells are marked invalid on their table, so <see cref="AwrRow.Num"/> returns null for
/// them: no rule can escalate a severity from, or print, an impossible number. Each affected table yields one check
/// that feeds the top-of-report warning and the data-quality level.
/// </summary>
internal static class SanityScan
{
    // normalised header (see AwrTable.Normalize) of columns that are a share of a total and therefore at most 100
    private static readonly HashSet<string> ShareColumns = new(StringComparer.Ordinal)
    {
        "%total", "% total", "% db time", "%db time", "% of db time", "% total call time", "%total call time",
        "% of capture", "% activity", "% event", "%user", "%system", "%idle", "%busy", "%total cpu", "%busy cpu"
    };

    private static readonly string[] Checked =
    [
        "Top 10 Foreground Events", "Top 5 Timed", "Foreground Wait Class", "Foreground Wait Events", "SQL ordered by Elapsed Time", "SQL ordered by CPU Time", "Host CPU"
    ];

    private const double Tolerance = 100.5;

    // Oracle measures DB CPU and DB time separately, so on real systems DB CPU can legitimately print slightly above 100% of
    // DB time (a real report showed DB CPU 101.13% and "Captured Time accounts for 103.6%"). That row gets a wider margin;
    // anything beyond it, and every other row, is still an impossible value.
    internal const double CpuRowTolerance = 150.0;

    internal static bool IsCpuRow(string? label)
    {
        var l = (label ?? "").Trim();
        return l.Equals("DB CPU", StringComparison.OrdinalIgnoreCase) || l.Equals("CPU time", StringComparison.OrdinalIgnoreCase);
    }

    public static List<DataQualityAnalyzer.Check> Run(AwrReport report)
    {
        var checks = new List<DataQualityAnalyzer.Check>();
        foreach (var t in report.Tables)
        {
            // Only tables whose shares are provably at most 100% of one total. Others (Instance CPU, Time Model background rows,
            // "Segments by ...", "... (UnOptimized)") legitimately print values above 100 in real reports.
            if (report.Kind != ReportKind.Ash && !Checked.Any(p => t.TitleStartsWith(p))) continue;
            try
            {
                var cols = new List<int>();
                for (var i = 0; i < t.Headers.Count; i++)
                    if (ShareColumns.Contains(AwrTable.Normalize(t.Headers[i]))) cols.Add(i);
                if (cols.Count == 0) continue;
                var bad = 0;
                double worst = 0;
                string worstLabel = "";
                foreach (var row in t.Rows)
                    foreach (var c in cols)
                    {
                        if (c >= row.Length) continue;
                        var v = NumberParser.Parse(row[c]);
                        var limit = row.Length > 0 && IsCpuRow(row[0]) ? CpuRowTolerance : Tolerance;
                        if (v is null || v.Value <= limit) continue;
                        if (!t.InvalidCells.TryGetValue(row, out var set)) t.InvalidCells[row] = set = [];
                        set.Add(c);
                        bad++;
                        if (v > worst) { worst = v.Value; worstLabel = row.Length > 0 ? row[0] : ""; }
                    }
                if (bad == 0) continue;
                var shown = worstLabel.Length > 40 ? worstLabel[..40] + "..." : worstLabel;
                checks.Add(new("impossible-" + t.Id, $"Impossible share in '{t.Title}'",
                    $"'{t.Title}' lists {bad} value(s) above 100% of the total {(shown.Length > 0 ? $"(for example in the row '{shown}')" : "")}. A share cannot exceed 100%, so these values were ignored.", true, Impossible: true));
            }
            catch { /* a failing scan must never break the analysis */ }
        }
        return checks;
    }
}
