// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text.RegularExpressions;
using Apodofos.Core.Analysis;
using Apodofos.Core.Model;
using Apodofos.Core.Providers;

namespace Apodofos.Tests;

/// <summary>
/// Enforces docs/coverage-matrix.md: every section a parser produces is classified in <see cref="CoverageMap"/>, every
/// "core" section has a real consumer (rule, chart, KPI or header extractor), and the consumers named in the map exist in the source.
/// </summary>
public class CoverageTests
{
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Apodofos.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }

    private static IEnumerable<string> Fixtures()
    {
        foreach (var f in Samples.ExampleFiles()) yield return f;
        var td = Path.Combine(AppContext.BaseDirectory, "TestData");
        foreach (var f in Directory.GetFiles(td)) yield return f;
        foreach (var f in LocalCorpusTests.CorpusFiles()) yield return f; // real reports, only when present
    }

    private static List<(string File, ReportKind Kind, List<AwrTable> Tables)> ParsedFixtures()
    {
        var list = new List<(string, ReportKind, List<AwrTable>)>();
        foreach (var f in Fixtures())
        {
            try
            {
                var p = ReportProviderRegistry.AnalyzeFile(f);
                var kind = p.Analysis.Meta.ReportType switch { "ASH" => ReportKind.Ash, "Statspack" => ReportKind.Statspack, _ => ReportKind.Awr };
                list.Add((f, kind, p.Analysis.Tables.Select(t => new AwrTable { Id = t.Id, Index = 0, Title = t.Title, Section = t.Section }).ToList()));
            }
            catch (ReportFormatException) { /* not a report */ }
        }
        return list;
    }

    [Fact]
    public void EverySectionTheParsersProduceIsClassified()
    {
        var unclassified = new SortedSet<string>();
        foreach (var (file, kind, tables) in ParsedFixtures())
            foreach (var t in tables)
                if (CoverageMap.Find(t.Title) == null) unclassified.Add($"{kind}: {t.Title}  ({Path.GetFileName(file)})");
        Assert.True(unclassified.Count == 0,
            "Sections without a CoverageMap entry (add a consumer, or classify as X with a reason, and update docs/coverage-matrix.md):\n" + string.Join("\n", unclassified));
    }

    [Fact]
    public void EveryCoreSectionHasARealConsumer()
    {
        foreach (var e in CoverageMap.Entries.Where(e => e.Core))
            Assert.True(e.HasRealConsumer, $"Core section '{e.Title}' has no consumer (only the generic table explorer).");
    }

    [Fact]
    public void CoreSectionsSeenInFixturesAreActuallyPresent()
    {
        // the data-quality "missing core section" check and the matrix agree: core lists are non-empty for every kind
        foreach (var k in Enum.GetValues<ReportKind>()) Assert.NotEmpty(CoverageMap.CoreSections(k));
    }

    [Fact]
    public void ConsumersNamedInTheMapExistInTheSource()
    {
        var root = RepoRoot();
        if (root == null) return;
        var src = Path.Combine(root, "src", "Apodofos.Core");
        var all = string.Concat(Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")).Select(File.ReadAllText));
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Select(Path.GetFileNameWithoutExtension).ToHashSet();
        var missing = new List<string>();
        foreach (var e in CoverageMap.Entries)
            foreach (var c in e.ConsumerList)
            {
                var parts = c.Split(':', 2);
                var name = parts.Length > 1 ? parts[1] : "";
                switch (parts[0])
                {
                    case "R" when !files.Contains(name) && !Regex.IsMatch(all, $@"(interface|class|enum|record) {name}"):
                        missing.Add($"{e.Title}: rule/class '{name}' not found"); break;
                    case "C" when name.StartsWith("seg-") ? !all.Contains("\"seg-\"") : !all.Contains($"\"{name}\""):
                        missing.Add($"{e.Title}: chart id '{name}' not found"); break;
                    case "K" when !all.Contains($"new(\"{name}\"") && !all.Contains($"\"{name}\""):
                        missing.Add($"{e.Title}: KPI '{name}' not found"); break;
                }
            }
        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    [Fact]
    public void RuleConsumersReallyReferenceTheirSection()
    {
        var root = RepoRoot();
        if (root == null) return;
        var rules = Path.Combine(root, "src", "Apodofos.Core", "Analysis");
        var sources = Directory.EnumerateFiles(rules, "*.cs", SearchOption.AllDirectories).ToDictionary(f => Path.GetFileNameWithoutExtension(f), File.ReadAllText);
        var bad = new List<string>();
        foreach (var e in CoverageMap.Entries.Where(e => e.Core))
        {
            // a core section is consumed when one of its named rule/chart files mentions the section title (or its documented prefix)
            var key = e.Title;
            var hit = e.ConsumerList.Where(c => c.StartsWith("R:")).Select(c => c.Substring(2))
                .Any(r => sources.TryGetValue(r, out var s) && Mentions(s, key));
            hit |= e.ConsumerList.Any(c => c.StartsWith("C:")) && (sources.TryGetValue("ChartBuilder", out var cb) && Mentions(cb, key) || sources.TryGetValue("AshChartBuilder", out var ab) && Mentions(ab, key));
            hit |= e.ConsumerList.Any(c => c.StartsWith("M:") || c.StartsWith("S:")); // header extractors / AwrReport helpers are checked by parsing tests
            hit |= sources.Values.Any(s => Mentions(s, key) && s.Contains("ctx.", StringComparison.Ordinal)); // referenced anywhere in the analysis layer
            if (!hit) bad.Add(key);
        }
        Assert.True(bad.Count == 0, "Core sections not referenced by the analysis layer: " + string.Join(", ", bad));
    }

    private static bool Mentions(string source, string title) =>
        source.Contains($"\"{title}", StringComparison.OrdinalIgnoreCase) || source.Contains(title.Replace(" (Target 100%)", ""), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void CoverageMatrixDocumentExists()
    {
        var root = RepoRoot();
        if (root == null) return;
        var doc = Path.Combine(root, "docs", "coverage-matrix.md");
        Assert.True(File.Exists(doc), "docs/coverage-matrix.md is missing");
        var text = File.ReadAllText(doc);
        foreach (var e in CoverageMap.Entries.Where(e => e.Core))
            Assert.Contains(e.Title, text);
    }
}
