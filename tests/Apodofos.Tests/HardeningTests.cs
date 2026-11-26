// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Diagnostics;
using System.Text;
using Apodofos.Core.Export;
using Apodofos.Core.Parsing;
using Apodofos.Core.Providers;

namespace Apodofos.Tests;

/// <summary>Damaged or hostile report files must be refused or read safely, never crash the program.</summary>
public class HardeningTests
{
    private static string Data(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    private static string Repeat(string s, int n) => new StringBuilder(s.Length * n).Insert(0, s, n).ToString();

    private static string Nested(string title, string open, string close, int n, bool closed) =>
        "<html><head><title>" + title + "</title></head><body><h1>" + title + "</h1>" + Repeat(open, n) + "x" + (closed ? Repeat(close, n) : "") + "</body></html>";

    [Theory]
    [InlineData("<span>", "</span>", 5000, false)]
    [InlineData("<div>", "</div>", 5000, false)]
    [InlineData("<div>", "</div>", 5000, true)]
    [InlineData("<table><tr><td>", "</td></tr></table>", 5000, false)]
    [InlineData("<table><tr><td>", "</td></tr></table>", 5000, true)]
    [InlineData("<span>", "</span>", 200000, false)]
    [InlineData("<div>", "</div>", 200000, true)]
    [InlineData("<table><tr><td>", "</td></tr></table>", 200000, false)]
    [InlineData("<font>", "</font>", 200000, false)]
    public void DeeplyNestedHtml_IsRefusedQuickly(string open, string close, int n, bool closed)
    {
        foreach (var title in new[] { "WORKLOAD REPOSITORY report for", "ASH Report For" })
        {
            var html = Nested(title, open, close, n, closed);
            var clock = Stopwatch.StartNew();
            var ex = Assert.ThrowsAny<ReportFormatException>(() => ReportProviderRegistry.AnalyzeContent(html, "deep.html"));
            Assert.Contains("nested", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
        }
    }

    [Fact]
    public void ModeratelyNestedHtml_StillParses()
    {
        var html = Data("awr-19c-synthetic.html").Replace("<body", "<body data-x=\"1\"", StringComparison.Ordinal);
        var at = html.IndexOf('>', html.IndexOf("<body", StringComparison.Ordinal)) + 1;
        var wrapped = html.Insert(at, Repeat("<div>", 200));
        Assert.NotEmpty(ReportProviderRegistry.AnalyzeContent(wrapped, "a.html").Analysis.Findings);
    }

    [Theory]
    [InlineData("1e999")]
    [InlineData("-1e999")]
    [InlineData("1e308K")]
    [InlineData("LONG")]
    public void NonFiniteNumbers_AreNotNumbers(string text)
    {
        if (text == "LONG") text = new string('9', 400);
        Assert.Null(NumberParser.Parse(text));
        Assert.Null(NumberParser.ParseMinutes(text));
    }

    [Theory]
    [InlineData("1e999")]
    [InlineData("LONG")]
    public void ReportWithNonFiniteCell_StillLoads(string value)
    {
        if (value == "LONG") value = new string('9', 400);
        foreach (var name in new[] { "awr-19c-synthetic.html", "ash-19c-hot-event.html" })
        {
            var html = System.Text.RegularExpressions.Regex.Replace(Data(name), @"(<td[^>]*>)\s*[\d,]+\.\d+\s*(</td>)", "${1}" + value + "${2}");
            Assert.Contains(value, html);
            var result = ReportProviderRegistry.AnalyzeContent(html, name);
            Assert.NotEmpty(DashboardBuilder.ToJson(result.Analysis, result.ReportHtml, DashboardMode.Export));
        }
    }

    private const string CspStart = "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none';";

    [Theory]
    [InlineData("<html><head></head><body>", "</body></html>")]
    [InlineData("<html><body>", "</body></html>")]
    [InlineData("<html><body><p>first</p><head></head>", "</body></html>")]
    [InlineData("<html><body><img src=x><div><head><title>t</title></head></div>", "</body></html>")]
    [InlineData("<p>before</p><html><head></head><body>", "</body></html>")]
    [InlineData("<body><style>p{}</style></body><head></head><html><body>", "</body></html>")]
    [InlineData("<!DOCTYPE html><html><body><template><head></head></template>", "</body></html>")]
    [InlineData("<!-- c --><!DOCTYPE html>\n<html><textarea><head></textarea><body>", "</body></html>")]
    [InlineData("<?xml version=\"1.0\"?><html><title><head></title><body>", "</body></html>")]
    [InlineData("<table>", "</table>")]
    public void ContentSecurityPolicy_ComesFirstWhateverTheMarkup(string before, string after)
    {
        var html = before + "<table><tr><th>DB Name</th><th>Release</th></tr><tr><td>DEMO</td><td>19.0.0.0.0</td></tr></table>" + after;
        var sanitized = AwrHtmlParser.Parse(html).SanitizedHtml;
        var rest = sanitized;
        if (rest.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)) rest = rest[(rest.IndexOf('>') + 1)..];
        Assert.StartsWith(CspStart, rest);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(sanitized, "default-src 'none'"));
    }

    [Fact]
    public void ExportedDashboard_CarriesTheReportWithThePolicyFirst()
    {
        var html = Data("awr-19c-synthetic.html").Replace("<head>", "", StringComparison.OrdinalIgnoreCase).Replace("</head>", "<head></head>", StringComparison.OrdinalIgnoreCase);
        var result = ReportProviderRegistry.AnalyzeContent(html, "a.html");
        Assert.StartsWith(CspStart, result.ReportHtml);
        using var json = System.Text.Json.JsonDocument.Parse(DashboardBuilder.ToJson(result.Analysis, result.ReportHtml, DashboardMode.Export));
        Assert.StartsWith(CspStart, json.RootElement.GetProperty("reportHtml").GetString());
    }

    public static TheoryData<string> AshFiles => new() { "ash-19c-hot-event.html", "ash-19c-hot-event.txt" };

    /// <summary>Blanks one column, or keeps one row, of every table in turn: no rule may fail on what is left.</summary>
    [Theory]
    [MemberData(nameof(AshFiles))]
    public void MutatedAshTables_NeverBreakARule(string name)
    {
        var failures = new List<string>();
        var tables = AshReportLoader.LoadContent(Data(name), name).Tables.Count;
        for (var ti = 0; ti < tables; ti++)
        {
            var columns = AshReportLoader.LoadContent(Data(name), name).Tables[ti].ColumnCount;
            for (var col = -1; col < columns; col++)
            {
                var report = AshReportLoader.LoadContent(Data(name), name);
                var table = report.Tables[ti];
                if (col < 0) { if (table.Rows.Count > 1) table.Rows.RemoveRange(1, table.Rows.Count - 1); }
                else
                    foreach (var row in table.Rows)
                        if (col < row.Length) row[col] = "";
                var result = Apodofos.Core.Analysis.AnalysisEngine.Analyze(report);
                failures.AddRange(result.Warnings.Where(w => w.Contains("could not", StringComparison.OrdinalIgnoreCase) && w.StartsWith("Rule", StringComparison.Ordinal))
                    .Select(w => $"{table.Title} column {col}: {w}"));
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Distinct()));
    }
}
