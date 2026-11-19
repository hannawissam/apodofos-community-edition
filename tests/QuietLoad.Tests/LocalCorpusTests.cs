// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text.Json;
using QuietLoad.Core.Analysis;
using QuietLoad.Core.Export;
using QuietLoad.Core.Model;
using QuietLoad.Core.Providers;
using QuietLoad.Core.Parsing;

namespace QuietLoad.Tests;

/// <summary>
/// Regression tests over the git-ignored EXAMPLES-local corpus (real third-party reports). They return early
/// when the folder is absent, so a clean checkout stays green; the reports are never copied into the repo.
/// </summary>
public class LocalCorpusTests
{
    internal static string? CorpusDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QuietLoad.slnx"))) dir = dir.Parent;
        var p = dir == null ? null : Path.Combine(dir.FullName, "EXAMPLES-local");
        return p != null && Directory.Exists(p) ? p : null;
    }

    internal static List<string> CorpusFiles()
    {
        var d = CorpusDir();
        if (d == null) return [];
        return Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    [ThirdPartyFact(ThirdPartyNeed.LocalCorpus)]
    public void EveryLocalReportAnalyzesCleanly()
    {
        var files = CorpusFiles();
        if (files.Count == 0) return; // corpus not present on this machine
        var problems = new List<string>();
        int analyzed = 0, skippedNotReports = 0;
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            try
            {
                AnalysisResult a;
                try { a = ReportProviderRegistry.AnalyzeFile(f).Analysis; }
                catch (ReportFormatException) { skippedNotReports++; continue; } // blog pages / articles saved next to the reports
                // informational notes (old release, estimated DB time) are fine; a rule or step that failed is not
                var failed = a.Warnings.Where(w => w.Contains("could not", StringComparison.OrdinalIgnoreCase) || w.Contains("failed", StringComparison.OrdinalIgnoreCase)).ToList();
                if (failed.Count > 0) problems.Add($"{name}: warnings: {string.Join(" | ", failed)}");
                analyzed++;
                var real = a.Findings.Where(x => x.Severity != Severity.Ok).ToList();
                if (a.Findings.Count == 0) problems.Add($"{name}: no findings");
                if (!a.Findings.Any(x => x.Docs.Count > 0)) problems.Add($"{name}: no finding carries documentation links");
                if (a.Health.Score is < 0 or > 100) problems.Add($"{name}: health score {a.Health.Score}");
                var dup = a.Findings.GroupBy(x => x.Title, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (dup.Count > 0) problems.Add($"{name}: duplicate finding titles: {string.Join("; ", dup)}");
                var json = DashboardBuilder.ToJson(a, null, DashboardMode.Export);
                foreach (var bad in new[] { "NaN", "Infinity" })
                    if (json.Contains($":{bad}") || json.Contains($"\"{bad}\"")) problems.Add($"{name}: JSON contains {bad}");
                using var _ = JsonDocument.Parse(json);
                if (a.Findings.Any(x => !Enum.IsDefined(x.Confidence))) problems.Add($"{name}: invalid confidence");
                var ids = a.Findings.Select(x => x.Id).ToHashSet();
                foreach (var x in a.Findings)
                    foreach (var rel in x.RelatedIds)
                        if (!ids.Contains(rel)) problems.Add($"{name}: {x.Id} relates to unknown {rel}");
            }
            catch (Exception ex) { problems.Add($"{name}: threw {ex.GetType().Name}: {ex.Message}"); }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(analyzed > 0, $"no analyzable report found in EXAMPLES-local ({skippedNotReports} non-report files skipped)");
    }
}
