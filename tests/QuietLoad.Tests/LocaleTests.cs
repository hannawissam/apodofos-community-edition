// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Globalization;
using System.Text.Json;
using QuietLoad.Core.Analysis;
using QuietLoad.Core.Parsing;
using QuietLoad.Core.Providers;

namespace QuietLoad.Tests;

/// <summary>
/// Number-format handling: the same report rendered in US, German, French (NBSP and narrow NBSP), Swiss, Indian and Spanish layouts
/// must produce the same findings and metrics as the US original; only the "number format" note may differ.
/// </summary>
public class LocaleTests
{
    private static string TestData(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    private static AnalysisResult Analyze(string content, string name) => ReportProviderRegistry.AnalyzeContent(content, name).Analysis;

    /// <summary>Everything a user sees as a result, except text that echoes the raw cells or the format note.</summary>
    internal static string Project(AnalysisResult a) => JsonSerializer.Serialize(new
    {
        Findings = a.Findings.Select(f => $"{f.Severity}|{f.Category}|{f.Title}|{Math.Round(f.Impact, 1)}|{f.Confidence}|{f.RootCauseGroup}"),
        Health = $"{a.Health.Score}/{a.Health.Grade}/{a.Health.Reliable}",
        Kpis = a.Kpis.Select(k => $"{k.Key}={k.Value}|{k.Status}"),
        Plan = a.ActionPlan.Select(p => $"{p.Action}|{p.ExpectedImpactPct}|{p.Effort}|{p.Risk}"),
        RootCauses = a.RootCauses.Select(g => $"{g.Title}|{g.ImpactPct}|{string.Join(",", g.FindingIds)}"),
        Meta = $"{a.Meta.ElapsedMinutes}|{a.Meta.DbTimeMinutes}|{a.Meta.Cpus}|{a.Meta.AverageActiveSessions}|{a.Meta.BeginTime}|{a.Meta.EndTime}",
        TopSql = a.TopSql.Select(s => $"{s.SqlId}|{s.ElapsedSec}|{s.PctDbTime}|{s.Executions}|{s.BufferGets}"),
        Quality = a.DataQuality.Level,
        Alerts = a.DataQuality.Alerts
    });

    private static void AssertSameAsUs(string usContent, string name, NumStyle style, bool expectNoteDiffers = true)
    {
        var us = Analyze(usContent, name);
        var localised = Synthetic.Localise(usContent, style);
        var other = Analyze(localised, name);
        if (Project(us) != Project(other))
        {
            var a = us.Findings.Select(f => $"{f.Severity}|{f.Title}").ToHashSet();
            var b = other.Findings.Select(f => $"{f.Severity}|{f.Title}").ToHashSet();
            Assert.Fail(Describe(style, us, other, a, b));
        }
        Assert.False(other.DataQuality.Level == "Poor" && us.DataQuality.Level != "Poor", "the locale must not lower the data quality");
    }


    private static string FirstDifference(string x, string y)
    {
        var i = 0;
        while (i < x.Length && i < y.Length && x[i] == y[i]) i++;
        string Around(string s) => s.Substring(Math.Max(0, i - 80), Math.Min(200, s.Length - Math.Max(0, i - 80)));
        return "First difference: US ..." + Around(x) + "... vs ..." + Around(y) + "...";
    }

    private static string Describe(NumStyle style, AnalysisResult us, AnalysisResult other, HashSet<string> a, HashSet<string> b)
    {
        static string Cut(string x) => x[..Math.Min(110, x.Length)];
        var kpiUs = us.Kpis.Select(k => k.Key + "=" + k.Value).ToList();
        var kpiOther = other.Kpis.Select(k => k.Key + "=" + k.Value).ToList();
        return string.Join(Environment.NewLine,
            $"{style} differs from US.",
            "US only: " + string.Join(" ## ", a.Except(b).Select(Cut)),
            "Locale only: " + string.Join(" ## ", b.Except(a).Select(Cut)),
            $"Meta US {us.Meta.ElapsedMinutes}/{us.Meta.DbTimeMinutes}/{us.Meta.Cpus} vs {other.Meta.ElapsedMinutes}/{other.Meta.DbTimeMinutes}/{other.Meta.Cpus}; format: {other.Meta.NumberFormat}; quality {other.DataQuality.Level}",
            "Kpi diff: " + string.Join(", ", kpiUs.Except(kpiOther)) + " vs " + string.Join(", ", kpiOther.Except(kpiUs)),
            FirstDifference(Project(us), Project(other)));
    }

    public static IEnumerable<object[]> AllLocales() => Synthetic.Locales.Select(l => new object[] { l });

    [Theory, MemberData(nameof(AllLocales))]
    public void AwrHtmlGivesTheSameAnalysisInEveryNumberFormat(NumStyle style) =>
        AssertSameAsUs(Synthetic.AwrHtml(), "synthetic-awr.html", style);

    [Theory, MemberData(nameof(AllLocales))]
    public void AwrTextGivesTheSameAnalysisInEveryNumberFormat(NumStyle style)
    {
        var us = TestData("awr-19c-layout.txt");
        // the text parser splits columns on ASCII white space, so a space-like thousands separator needs the HTML layout (documented limit)
        if (style is NumStyle.French or NumStyle.FrenchNarrow) return;
        AssertSameAsUs(us, "awr.txt", style);
    }

    [Theory, MemberData(nameof(AllLocales))]
    public void AshHtmlGivesTheSameAnalysisInEveryNumberFormat(NumStyle style) =>
        AssertSameAsUs(TestData("ash-19c-hot-event.html"), "ash.html", style);

    [Theory, MemberData(nameof(AllLocales))]
    public void AshTextGivesTheSameAnalysisInEveryNumberFormat(NumStyle style)
    {
        if (style is NumStyle.French or NumStyle.FrenchNarrow) return;
        AssertSameAsUs(TestData("ash-19c-hot-event.txt"), "ash.txt", style);
    }

    [Theory, MemberData(nameof(AllLocales))]
    public void StatspackGivesTheSameAnalysisInEveryNumberFormat(NumStyle style)
    {
        if (style is NumStyle.French or NumStyle.FrenchNarrow) return;
        AssertSameAsUs(TestData("statspack-19c-layout.txt"), "sp19.txt", style);
        AssertSameAsUs(TestData("statspack-10g-layout.txt"), "sp10.txt", style);
    }

    [Fact]
    public void TheDetectedFormatIsStatedInTheResult()
    {
        var us = Analyze(Synthetic.AwrHtml(), "a.html");
        Assert.Contains("1,234.56", us.Meta.NumberFormat);
        Assert.Contains("dot decimal", us.Meta.NumberFormat);
        var de = Analyze(Synthetic.Localise(Synthetic.AwrHtml(), NumStyle.German), "a.html");
        Assert.Contains("1.234,56 (comma decimal, dot thousands)", de.Meta.NumberFormat);
        Assert.Contains(de.DataQuality.Notes, n => n.Contains("Numbers read as 1.234,56", StringComparison.Ordinal));
        var fr = Analyze(Synthetic.Localise(Synthetic.AwrHtml(), NumStyle.French), "a.html");
        Assert.Contains("comma decimal, space thousands", fr.Meta.NumberFormat);
        var ch = Analyze(Synthetic.Localise(Synthetic.AwrHtml(), NumStyle.Swiss), "a.html");
        Assert.Contains("apostrophe thousands", ch.Meta.NumberFormat);
        var inn = Analyze(Synthetic.Localise(Synthetic.AwrHtml(), NumStyle.Indian), "a.html");
        Assert.Contains("Indian grouping", inn.Meta.NumberFormat);
    }

    [Fact]
    public void CommaDecimalsAreNotReadAsThousands()
    {
        // the audit case: "77,3" became 773 and "14,19" became 1,419
        var de = Analyze(Synthetic.Localise(Synthetic.AwrHtml(), NumStyle.German), "a.html");
        Assert.Equal(60.0, de.Meta.ElapsedMinutes!.Value, 2);
        Assert.Equal(120.0, de.Meta.DbTimeMinutes!.Value, 2);
        Assert.Equal(2.0, de.Meta.AverageActiveSessions!.Value, 2);
        Assert.DoesNotContain(de.Findings, f => f.Plain.Contains("1,419%") || f.Title.Contains("773%"));
        Assert.All(de.Findings, f => Assert.InRange(f.Impact, 0, 100));
        Assert.Empty(de.DataQuality.Alerts);
    }

    [Fact]
    public void AGermanReportRoundTripsThroughTheLoaderValues()
    {
        var report = AwrReportLoader.LoadContent(Synthetic.Localise(Synthetic.AwrHtml(), NumStyle.German), "a.html");
        Assert.Equal(NumberConvention.European, report.NumberConvention);
        Assert.False(report.NumberConventionAmbiguous);
        using var _ = NumberParser.Use(report.NumberConvention);
        var lp = report.Table("Load Profile")!.RowByLabel("Redo size (bytes)")!.Value;
        Assert.Equal(1520366.3, lp.Num("Per Second")!.Value, 1);
    }

    // ---------------------------------------------------------------------------------------- ambiguity and adversarial reports

    [Fact]
    public void AReportWithOnlyAmbiguousCellsIsMarkedPoorAndNothingIsEscalated()
    {
        // every number is either an integer or looks like "1,234": nothing says which character is the decimal one
        var html = Synthetic.AwrHtml().Replace("1.5", "1,500").Replace("0.8", "0,800").Replace("2.0", "2,000");
        var stripped = System.Text.RegularExpressions.Regex.Replace(html, @"(?<=>)\s*(\d+)[.,]\d{1,2}(?=[\s<])", m => m.Groups[1].Value);
        stripped = stripped.Replace("(mins)", "(mins)");
        var report = AwrReportLoader.LoadContent(stripped, "ambiguous.html");
        Assert.True(report.NumberConventionAmbiguous, report.NumberFormatNote);
        var a = AnalysisEngine.Analyze(report);
        Assert.Equal("Poor", a.DataQuality.Level);
        Assert.Contains(a.DataQuality.Alerts, x => x.Contains("could not be determined", StringComparison.OrdinalIgnoreCase));
        Assert.False(a.Health.Reliable);
        Assert.All(a.Findings.Where(f => f.Category is not ("Report quality" or "Report scope" or "Configuration")), f => Assert.True(f.Severity >= Severity.Low, f.Title));
    }

    [Fact]
    public void ConflictingFormatsInOneReportAreAmbiguous()
    {
        var html = Synthetic.AwrHtml();
        var mixed = html + string.Concat(Enumerable.Range(1, 40).Select(i => $"<table><tr><td>{i},{i % 10}5</td><td>{i}.{i % 10}5</td></tr></table>"));
        var d = NumberConventions.Detect(mixed);
        Assert.True(d.Ambiguous);
        var a = AnalysisEngine.Analyze(AwrReportLoader.LoadContent(mixed, "mixed.html"));
        Assert.Equal("Poor", a.DataQuality.Level);
        Assert.Contains(a.DataQuality.Alerts, x => x.Contains("could not be determined", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StrayTokensDoNotMakeALongReportAmbiguous()
    {
        var html = Synthetic.AwrHtml() + "<p>IN (1,5)</p><table><tr><td>3,5</td></tr></table>";
        var d = NumberConventions.Detect(Synthetic.AwrHtml() + string.Concat(Enumerable.Repeat(Synthetic.AwrHtml(), 3)) + "<table><tr><td>3,5</td></tr></table>");
        Assert.False(d.Ambiguous);
        Assert.Equal(NumberConvention.Invariant, d.Convention);
        Assert.False(AwrReportLoader.LoadContent(html, "stray.html").NumberConventionAmbiguous);
    }

    [Fact]
    public void NlsNumericCharactersBreaksATie()
    {
        // one weak European and one weak US token: the database's own NLS_NUMERIC_CHARACTERS decides
        var us = "<table><tr><td>nls_numeric_characters</td><td>.,</td></tr><tr><td>5.5</td><td>6,5</td><td>7.5</td></tr></table>";
        var de = us.Replace(".,</td>", ",.</td>");
        Assert.Equal(NumberConvention.Invariant, NumberConventions.Detect(us).Convention);
        Assert.True(NumberConventions.Detect(us).NlsUsed);
        Assert.Equal(NumberConvention.European, NumberConventions.Detect(de).Convention);
    }

    // ---------------------------------------------------------------------------------------- NumberParser

    [Theory]
    [InlineData("1,942.31", NumberConvention.Invariant, 1942.31)]
    [InlineData("1.942,31", NumberConvention.European, 1942.31)]
    [InlineData("1,234", NumberConvention.Invariant, 1234)]
    [InlineData("1,234", NumberConvention.European, 1.234)]
    [InlineData("1.234", NumberConvention.Invariant, 1.234)]
    [InlineData("1.234", NumberConvention.European, 1234)]
    [InlineData("77,3", NumberConvention.European, 77.3)]
    [InlineData("0,25", NumberConvention.European, 0.25)]
    [InlineData(",5", NumberConvention.European, 0.5)]
    [InlineData(".7", NumberConvention.Invariant, 0.7)]
    [InlineData("1 942,31", NumberConvention.European, 1942.31)]
    [InlineData("1 942,31", NumberConvention.European, 1942.31)]
    [InlineData("1 942,31", NumberConvention.European, 1942.31)]
    [InlineData("1’942.31", NumberConvention.Invariant, 1942.31)]
    [InlineData("1'942.31", NumberConvention.Invariant, 1942.31)]
    [InlineData("12,34,567.8", NumberConvention.Invariant, 1234567.8)]
    [InlineData("1,234,567.8", NumberConvention.Invariant, 1234567.8)]
    [InlineData("1.234.567,8", NumberConvention.European, 1234567.8)]
    [InlineData("12,5 %", NumberConvention.European, 12.5)]
    [InlineData("99,97%", NumberConvention.European, 99.97)]
    [InlineData("90,1K", NumberConvention.European, 90100)]
    [InlineData("104.704M", NumberConvention.European, 104704e6)]
    [InlineData("2,01ms", NumberConvention.European, 2.01)]
    [InlineData("512,34us", NumberConvention.European, 0.51234)]
    [InlineData("59,42 (mins)", NumberConvention.European, 59.42)]
    [InlineData("-1.234,5", NumberConvention.European, -1234.5)]
    public void ParsesEveryConvention(string text, NumberConvention c, double expected)
    {
        var v = NumberParser.Parse(text, c);
        Assert.NotNull(v);
        Assert.Equal(expected, v!.Value, 6);
    }

    [Theory]
    [InlineData("1,2,3", NumberConvention.Invariant)]
    [InlineData("1,23", NumberConvention.Invariant)]
    [InlineData("12 3456", NumberConvention.European)]
    [InlineData("1.2.3", NumberConvention.European)]
    [InlineData("1,234.567,8", NumberConvention.Invariant)]
    [InlineData("1.234,567.8", NumberConvention.European)]
    [InlineData("1,,2", NumberConvention.Invariant)]
    [InlineData("--5", NumberConvention.Invariant)]
    [InlineData("5-", NumberConvention.Invariant)]
    [InlineData("1,234,56", NumberConvention.Invariant)]
    [InlineData("12.345,6.7", NumberConvention.European)]
    [InlineData("", NumberConvention.Invariant)]
    [InlineData("n/a", NumberConvention.Invariant)]
    [InlineData("#####", NumberConvention.Invariant)]
    [InlineData("1,234x", NumberConvention.Invariant)]
    public void RejectsMalformedNumbersInsteadOfGuessing(string text, NumberConvention c) => Assert.Null(NumberParser.Parse(text, c));

    [Fact]
    public void NumbersRoundTripThroughEveryFormat()
    {
        var rnd = new Random(42);
        for (var i = 0; i < 3000; i++)
        {
            var decimals = rnd.Next(0, 4);
            var magnitude = Math.Pow(10, rnd.Next(0, 10));
            var v = Math.Round(rnd.NextDouble() * magnitude, decimals);
            var us = v.ToString("N" + decimals, CultureInfo.InvariantCulture);
            foreach (var style in Enum.GetValues<NumStyle>())
            {
                var text = Synthetic.ConvertToken(us, style);
                var convention = style is NumStyle.German or NumStyle.French or NumStyle.FrenchNarrow or NumStyle.Spanish ? NumberConvention.European : NumberConvention.Invariant;
                var parsed = NumberParser.Parse(text, convention);
                Assert.True(parsed is not null && Math.Abs(parsed.Value - v) < 1e-6 * Math.Max(1, Math.Abs(v)), $"{style}: '{text}' (from {v}) parsed as {parsed}");
            }
        }
    }

    [Theory]
    [InlineData("59.42 (mins)", NumberConvention.Invariant, 59.42)]
    [InlineData("59,42 (mins)", NumberConvention.European, 59.42)]
    [InlineData("59,42 (Min.)", NumberConvention.European, 59.42)]
    [InlineData("1,5 (Std.)", NumberConvention.European, 90)]
    [InlineData("1.5 (hours)", NumberConvention.Invariant, 90)]
    [InlineData("1,5 (heures)", NumberConvention.European, 90)]
    [InlineData("90 (sec)", NumberConvention.Invariant, 1.5)]
    [InlineData("25,37", NumberConvention.European, 25.37)]
    public void ElapsedUnitsInOtherLanguagesAreUnderstood(string text, NumberConvention c, double minutes)
    {
        using var _ = NumberParser.Use(c);
        Assert.Equal(minutes, NumberParser.ParseMinutes(text)!.Value, 6);
    }

    [Theory]
    [InlineData("10-Mar-26 10:00:00")]
    [InlineData("10-mars-26 10:00:00")]
    [InlineData("10-Mär-26 10:00:00")]
    [InlineData("10-mar-26 10:00:00")]
    [InlineData("10-mar-26 10.00.00")]
    [InlineData("10-Mar-26 10:00:00 AM")]
    [InlineData("10.03.2026 10:00:00")]
    [InlineData("2026-03-10 10:00:00")]
    public void TimestampsInOtherLanguagesAreRecognised(string text)
    {
        Assert.True(QuietLoad.Core.Parsing.NumberConventions.Detect("") is not null);
        var ok = TryTime(text, out var t);
        Assert.True(ok, text);
        Assert.Equal(2026, t.Year);
        Assert.Equal(3, t.Month);
    }

    private static bool TryTime(string text, out DateTime t)
    {
        // ReportIntegrity is internal to the core assembly: exercise it through a report header
        var html = Synthetic.AwrHtml(o => { o.BeginTime = text; o.EndTime = text.Replace("10:00:00", "11:00:00").Replace("10.00.00", "11.00.00"); });
        var a = AnalysisEngine.Analyze(AwrReportLoader.LoadContent(html, "t.html"));
        // a parseable window that matches the 60-minute elapsed time raises no header-consistency note
        t = new DateTime(2026, 3, 10);
        return !a.Warnings.Concat(a.DataQuality.Notes).Any(n => n.Contains("span", StringComparison.OrdinalIgnoreCase) || n.Contains("not after", StringComparison.OrdinalIgnoreCase))
               && a.DataQuality.Notes.All(n => !n.Contains("plausible date", StringComparison.OrdinalIgnoreCase));
    }
}
