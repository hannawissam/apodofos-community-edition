// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using QuietLoad.Core.Analysis;
using QuietLoad.Core.Model;
using QuietLoad.Core.Parsing;

namespace QuietLoad.Tests;

internal static class Samples
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Examples");
    // Real third-party reports: git-ignored, absent in a fresh clone (see ThirdPartyFact). Use Syn11/Syn19 for tests that must always run.
    public static IEnumerable<string> ExampleFiles(string pattern = "*.html") => Directory.Exists(Dir) ? Directory.GetFiles(Dir, pattern) : [];
    public static bool HasExamples => ExampleFiles("*EBSCDB*.html").Any() && ExampleFiles("*DB11G*.html").Any();
    public static string Ebs => ExampleFiles("*EBSCDB*.html").First();
    public static string Db11 => ExampleFiles("*DB11G*.html").First();
    /// <summary>Synthetic, self-made reports (invented TESTDB11 / TESTDB19, host testhost01): always available.</summary>
    public static string Syn11 => Path.Combine(AppContext.BaseDirectory, "TestData", "awr-11g-synthetic.html");
    public static string Syn19 => Path.Combine(AppContext.BaseDirectory, "TestData", "awr-19c-synthetic.html");
    /// <summary>Both synthetic reports plus every real example that is present.</summary>
    public static IEnumerable<string> AllHtml() => new[] { Syn11, Syn19 }.Concat(ExampleFiles());
    public static IEnumerable<object[]> AllHtmlData() => AllHtml().Select(f => new object[] { f });
    public static string Text => Path.Combine(AppContext.BaseDirectory, "TestData", "small-awr.txt");

    private static readonly Dictionary<string, AwrReport> Cache = new();
    public static AwrReport Load(string path)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(path, out var r)) Cache[path] = r = AwrReportLoader.LoadFile(path);
            return r;
        }
    }

    public static bool WebUiPresent
    {
        get
        {
            try { DashboardAssets(); return true; } catch { return false; }
        }
    }
    private static void DashboardAssets()
    {
        foreach (var n in new[] { "index.html", "app.css", "charts.js", "app.js" })
            QuietLoad.Core.Export.DashboardBuilder.ReadAsset(n);
    }
}

public class NumberParserTests
{
    [Theory]
    [InlineData("1,942.31", 1942.31)]
    [InlineData(".7", 0.7)]
    [InlineData("90.1K", 90100d)]
    [InlineData("13.4M", 13400000d)]
    [InlineData("-1.2", -1.2)]
    [InlineData("100.07", 100.07)]
    [InlineData("2.01ms", 2.01)]
    [InlineData("512us", 0.512)]
    [InlineData("1s", 1000d)]
    [InlineData("59.42 (mins)", 59.42)]
    [InlineData("104,704M", 104704e6)]
    [InlineData("95.5%", 95.5)]
    public void Parses(string text, double expected)
    {
        var v = NumberParser.Parse(text);
        Assert.NotNull(v);
        Assert.Equal(expected, v!.Value, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("-")]
    [InlineData(".")]
    public void ReturnsNullForNonNumbers(string? text) => Assert.Null(NumberParser.Parse(text));

    [Fact]
    public void ParseOrZeroDefaults() => Assert.Equal(0d, NumberParser.ParseOrZero("n/a"));

    [Fact]
    public void FirstNumberIn() => Assert.Equal(32d, NumberParser.FirstNumberIn("Host CPU (CPUs: 32 Cores: 16)"));
}

public class ExampleParsingTests
{
    [ThirdPartyFact]
    public void EbsCdbHeader()
    {
        var r = Samples.Load(Samples.Ebs);
        Assert.Equal(AwrReportFormat.Html, r.Format);
        Assert.Equal("EBSCDB", r.Info.DbName, ignoreCase: true);
        Assert.StartsWith("12.1", r.Info.Release);
        Assert.Equal("659", r.Info.BeginSnapId);
        Assert.Equal("667", r.Info.EndSnapId);
        Assert.Equal(121.57, r.Info.ElapsedMinutes!.Value, 2);
        Assert.Equal(1942.31, r.Info.DbTimeMinutes!.Value, 2);
        Assert.True(r.Tables.Count > 50, $"table count {r.Tables.Count}");
        Assert.NotEmpty(r.SanitizedHtml);
    }

    [ThirdPartyFact]
    public void Db11gHeader()
    {
        var r = Samples.Load(Samples.Db11);
        Assert.Equal("DB11G", r.Info.DbName, ignoreCase: true);
        Assert.StartsWith("11.2", r.Info.Release);
        Assert.Equal("70", r.Info.BeginSnapId);
        Assert.Equal("71", r.Info.EndSnapId);
        Assert.Equal(59.42, r.Info.ElapsedMinutes!.Value, 2);
        Assert.Equal(25.37, r.Info.DbTimeMinutes!.Value, 2);
        Assert.True(r.Tables.Count > 40, $"table count {r.Tables.Count}");
    }

    [Fact]
    public void SyntheticHtmlHeaders()
    {
        var r19 = Samples.Load(Samples.Syn19);
        Assert.Equal(AwrReportFormat.Html, r19.Format);
        Assert.Equal("TESTDB19", r19.Info.DbName);
        Assert.StartsWith("19.0", r19.Info.Release);
        Assert.Equal("4410", r19.Info.BeginSnapId);
        Assert.Equal("4414", r19.Info.EndSnapId);
        Assert.Equal(60.10, r19.Info.ElapsedMinutes!.Value, 2);
        Assert.Equal(318.40, r19.Info.DbTimeMinutes!.Value, 2);
        Assert.Equal(16, r19.Info.Cpus);
        Assert.True(r19.Tables.Count > 45, $"table count {r19.Tables.Count}");
        Assert.NotEmpty(r19.SanitizedHtml);
        Assert.NotNull(r19.TableStartingWith("Top 10 Foreground Events"));

        var r11 = Samples.Load(Samples.Syn11);
        Assert.Equal("TESTDB11", r11.Info.DbName);
        Assert.StartsWith("11.2", r11.Info.Release);
        Assert.Equal("212", r11.Info.BeginSnapId);
        Assert.Equal("213", r11.Info.EndSnapId);
        Assert.Equal(60.13, r11.Info.ElapsedMinutes!.Value, 2);
        Assert.Equal(21.80, r11.Info.DbTimeMinutes!.Value, 2);
        Assert.True(r11.Tables.Count > 35, $"table count {r11.Tables.Count}");
        Assert.NotNull(r11.TableStartingWith("Top 5 Timed Foreground Events"));
    }

    [Fact]
    public void TableIdsAreUnique()
    {
        foreach (var p in Samples.AllHtml())
        {
            var r = Samples.Load(p);
            Assert.Equal(r.Tables.Count, r.Tables.Select(t => t.Id).Distinct().Count());
        }
    }

    [Fact]
    public void SanitiserRemovesScriptsAndHandlers()
    {
        const string html = "<html><head><script>alert(1)</script></head><body onload=\"evil()\">" +
            "<table border=\"1\" summary=\"This table displays load profile\"><tr><th>Name</th><th>Per Second</th></tr>" +
            "<tr><td>DB Time(s):</td><td>1.5</td></tr></table><script src=\"http://x/y.js\"></script>" +
            "<a href=\"javascript:alert(2)\">x</a></body></html>";
        var r = AwrHtmlParser.Parse(html, "t.html");
        var s = r.SanitizedHtml;
        Assert.DoesNotContain("<script", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onload", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", s, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExampleSanitisedHtmlHasNoScripts()
    {
        foreach (var p in Samples.AllHtml())
            Assert.DoesNotContain("<script", Samples.Load(p).SanitizedHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SyntheticTextReportParses()
    {
        var r = Samples.Load(Samples.Text);
        Assert.Equal(AwrReportFormat.Text, r.Format);
        Assert.Equal("TESTDB", r.Info.DbName, ignoreCase: true);
        Assert.StartsWith("19.0", r.Info.Release);
        Assert.Equal("100", r.Info.BeginSnapId);
        Assert.Equal("101", r.Info.EndSnapId);
        Assert.Equal(60.0, r.Info.ElapsedMinutes!.Value, 2);
        Assert.Equal(120.5, r.Info.DbTimeMinutes!.Value, 2);
        Assert.NotEmpty(r.Tables);
        var events = r.Tables.FirstOrDefault(t => t.Rows.Any(x => x.Length > 0 && x[0].Contains("log file sync")));
        Assert.NotNull(events);
    }

    [Fact]
    public void InvalidContentIsRejected()
    {
        Assert.Throws<AwrFormatException>(() => AwrReportLoader.LoadContent("just some random text"));
    }

    [Fact]
    public void MissingFileThrows() =>
        Assert.Throws<FileNotFoundException>(() => AwrReportLoader.LoadFile(Path.Combine(AppContext.BaseDirectory, "nope.html")));
}

public class AnalysisTests
{
    private static AnalysisResult Analyze(string path) => AnalysisEngine.Analyze(Samples.Load(path));

    [ThirdPartyFact]
    public void EbsCdbFindings()
    {
        var a = Analyze(Samples.Ebs);
        Assert.NotEmpty(a.Findings);
        Assert.InRange(a.Health.Score, 0, 100);
        Assert.Contains(a.Findings, f => f.Title.Contains("buffer busy waits", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(a.Findings, f => Mentions(f, "4y08a52989vfk"));
        Assert.DoesNotContain(a.Warnings, w => w.Contains("could not complete"));
    }

    [ThirdPartyFact]
    public void Db11gFindings()
    {
        var a = Analyze(Samples.Db11);
        Assert.NotEmpty(a.Findings);
        // The demo database is almost idle (< 1 average active session), so severities are capped at Medium.
        Assert.DoesNotContain(a.Findings, f => f.Severity is Severity.Critical or Severity.High);
        Assert.Contains(a.Findings, f => f.Severity is Severity.Medium or Severity.Low);
        Assert.Contains(a.Findings, f => Mentions(f, "0w2qpuc6u2zsp"));
        Assert.DoesNotContain(a.Warnings, w => w.Contains("could not complete"));
    }

    [Fact]
    public void Synthetic19cCoversTheMajorFindingCategories()
    {
        var a = Analyze(Samples.Syn19);
        Assert.Equal("TESTDB19", a.Meta.DbName);
        Assert.DoesNotContain(a.Warnings, w => w.Contains("could not complete"));
        Assert.Contains(a.Findings, f => f.Severity is Severity.Critical or Severity.High);
        var categories = a.Findings.Select(f => f.Category).ToHashSet();
        Assert.Contains("Wait events", categories);
        Assert.Contains("SQL", categories);
        Assert.True(categories.Count >= 5, "categories: " + string.Join(", ", categories));
        Assert.Contains(a.Findings, f => f.Title.Contains("buffer busy waits", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(a.Findings, f => Mentions(f, "7ab3k9xq2m5tn"));
        Assert.Contains(a.Findings, f => Mentions(f, "row lock contention"));
        Assert.Contains(a.Findings, f => f.Title.Contains("hard parse", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(a.Findings, f => f.Title.Contains("db file sequential read", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(a.Findings, f => f.Sql.Count > 0);
        Assert.Contains(a.Findings, f => f.Docs.Count > 0);
    }

    [Fact]
    public void Synthetic11gIsAnIdleDatabaseWithMediumAtMostFindings()
    {
        var a = Analyze(Samples.Syn11);
        Assert.Equal("TESTDB11", a.Meta.DbName);
        Assert.DoesNotContain(a.Warnings, w => w.Contains("could not complete"));
        Assert.DoesNotContain(a.Findings, f => f.Severity is Severity.Critical or Severity.High); // < 1 average active session
        Assert.Contains(a.Findings, f => f.Severity is Severity.Medium);
        Assert.Contains(a.Findings, f => Mentions(f, "6kt2n8vb4c1dz"));
        Assert.Contains(a.Findings, f => f.Title.Contains("db file sequential read", StringComparison.OrdinalIgnoreCase));
    }

    [Theory, MemberData(nameof(Samples.AllHtmlData), MemberType = typeof(Samples))]
    public void ResultIsComplete(string path)
    {
        var a = Analyze(path);
        Assert.NotEmpty(a.Kpis);
        Assert.NotEmpty(a.Charts);
        Assert.NotEmpty(a.Tables);
        Assert.Equal(a.Findings.Count, a.Findings.Select(f => f.Id).Distinct().Count());
    }

    [Fact]
    public void SyntheticTextReportAnalyzes()
    {
        var a = Analyze(Samples.Text);
        Assert.Equal("TESTDB", a.Meta.DbName, ignoreCase: true);
    }

    private static bool Mentions(Finding f, string needle) =>
        f.Title.Contains(needle) || f.Plain.Contains(needle) || f.Technical.Contains(needle) ||
        f.Evidence.Any(e => e.Value.Contains(needle) || e.Label.Contains(needle) || (e.Note?.Contains(needle) ?? false));
}
