// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using QuietLoad.Core.Analysis;
using QuietLoad.Core.Model;
using QuietLoad.Core.Parsing;
using QuietLoad.Core.Providers;

namespace QuietLoad.Tests;

/// <summary>ASH report support. Fixtures are synthetic (TESTDB19 / testhost01) and mirror the documented ashrpt.sql layouts.</summary>
public class AshTests
{
    private static string Html => Path.Combine(AppContext.BaseDirectory, "TestData", "ash-19c-hot-event.html");
    private static string Txt => Path.Combine(AppContext.BaseDirectory, "TestData", "ash-19c-hot-event.txt");

    private static AnalysisResult Analyze(string path) => ReportProviderRegistry.AnalyzeFile(path).Analysis;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AshWordingNeverSaysDbTimeInPlanOrFindingTitles(bool html)
    {
        var a = Analyze(html ? Html : Txt);
        Assert.NotEmpty(a.ActionPlan);
        foreach (var x in a.ActionPlan)
        {
            Assert.DoesNotContain("DB time", x.ExpectedImpact);
            Assert.DoesNotContain("DB time", x.Why);
        }
        Assert.Contains(a.ActionPlan, x => x.ExpectedImpact.Contains("active session time") || x.ExpectedImpact.Contains("whole workload"));
        Assert.DoesNotContain(a.Findings, f => f.Title.Contains("DB time") || f.Title.StartsWith("Database time was mostly"));
    }

    // Regression: AwrRow is a struct, so a null check on FirstOrDefault() was always true and a parallel-query finding
    // with no PX events in "Top User Events" printed "Top PX event ''".
    private static string WithPqSession(string html, bool withPxEvent)
    {
        const string none = "                  No data exists for this section of the report.";
        var marker = html.IndexOf("name=\"Top Sessions running PQs\"", StringComparison.Ordinal);
        Assert.True(marker > 0);
        var at = html.IndexOf(none, marker, StringComparison.Ordinal);
        Assert.True(at > 0);
        const string table =
            "<table border=\"1\" summary=\"This table displays top sessions running PQs\">\n" +
            "<tr><th class=\"awrbg\" scope=\"col\">Sid, Serial#</th><th class=\"awrbg\" scope=\"col\">User</th><th class=\"awrbg\" scope=\"col\">Program</th><th class=\"awrbg\" scope=\"col\">% Activity</th><th class=\"awrbg\" scope=\"col\">Event</th><th class=\"awrbg\" scope=\"col\">% Event</th></tr>\n" +
            "<tr><td class='awrc'>77, 1234</td><td class='awrc'>BATCH</td><td class='awrc'>sqlplus@testhost01</td><td align=\"right\" class='awrc'>30.00</td><td class='awrc'>CPU + Wait for CPU</td><td align=\"right\" class='awrc'>30.00</td></tr>\n" +
            "</table>";
        html = html.Remove(at, none.Length).Insert(at, table);
        if (withPxEvent)
        {
            const string row = "<tr><td class='awrc'>log file sync</td><td class='awrc'>Commit</td><td align=\"right\" class='awrc'>5.00</td><td align=\"right\" class='awrc'>0.26</td></tr>";
            var r = html.IndexOf(row, StringComparison.Ordinal);
            Assert.True(r > 0);
            html = html.Insert(r, "<tr><td class='awrc'>PX Deq Credit: send blkd</td><td class='awrc'>Idle</td><td align=\"right\" class='awrc'>8.00</td><td align=\"right\" class='awrc'>0.40</td></tr>\n");
        }
        return html;
    }

    [Fact]
    public void ParallelFindingWithoutPxEventsNeverPrintsAnEmptyEventName()
    {
        var html = WithPqSession(File.ReadAllText(Html), withPxEvent: false);
        var a = ReportProviderRegistry.AnalyzeContent(html, "ash-pq.html").Analysis;
        var f = Assert.Single(a.Findings, x => x.Category == "Parallel execution");
        Assert.DoesNotContain("Top PX event", f.Technical);
        Assert.DoesNotContain("''", f.Technical);
    }

    [Fact]
    public void ParallelFindingNamesTheTopPxEventWhenThereIsOne()
    {
        var html = WithPqSession(File.ReadAllText(Html), withPxEvent: true);
        var a = ReportProviderRegistry.AnalyzeContent(html, "ash-pq-px.html").Analysis;
        var f = Assert.Single(a.Findings, x => x.Category == "Parallel execution");
        Assert.Contains("Top PX event 'PX Deq Credit: send blkd'", f.Technical);
    }

    [Fact]
    public void AwrWordingStillSaysDbTime()
    {
        var a = AnalysisEngine.Analyze(Samples.Load(Samples.Syn11));
        Assert.Contains(a.ActionPlan, x => x.ExpectedImpact.Contains("DB time") || x.ExpectedImpact.Contains("whole workload"));
        Assert.DoesNotContain(a.ActionPlan, x => x.ExpectedImpact.Contains("active session time"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProviderRecognisesAshReports(bool html)
    {
        var content = File.ReadAllText(html ? Html : Txt);
        Assert.Equal("oracle-ash", ReportProviderRegistry.Resolve(content, null)!.Id);
    }

    [Fact]
    public void AwrAndStatspackAreNotTakenForAsh()
    {
        Assert.NotEqual("oracle-ash", ReportProviderRegistry.Resolve(File.ReadAllText(Samples.Text), null)?.Id);
        Assert.NotEqual("oracle-ash", ReportProviderRegistry.Resolve(File.ReadAllText(Samples.Syn11), null)?.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HeaderFactsAreRead(bool html)
    {
        var r = AshReportLoader.LoadContent(File.ReadAllText(html ? Html : Txt));
        Assert.Equal(ReportKind.Ash, r.Kind);
        Assert.Equal("TESTDB19", r.Info.DbName);
        Assert.Equal("1234567890", r.Info.DbId);
        Assert.Equal("testhost01", r.Info.HostName);
        Assert.Equal("19.0.0.0.0", r.Info.Release);
        Assert.Equal(4, r.Info.Cpus);
        Assert.Equal(30, r.Info.ElapsedMinutes);
        Assert.Equal(9360, r.Info.AshSampleCount);
        Assert.Equal(5.2, r.Info.AverageActiveSessions!.Value, 2);
        Assert.Equal(1.3, r.Info.AshAasPerCpu!.Value, 2);
        Assert.Equal("12-Nov-26 09:00:00", r.Info.BeginSnapTime);
        Assert.Contains("V$ACTIVE_SESSION_HISTORY", r.Info.AshDataSource);
        Assert.False(r.Info.AshFromHistory);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void KeyTablesAreRecognised(bool html)
    {
        var r = AshReportLoader.LoadContent(File.ReadAllText(html ? Html : Txt));
        foreach (var title in new[] { "Top User Events", "Top Background Events", "Top Event P1/P2/P3 Values", "Top Service/Module", "Top SQL Command Types",
                     "Top Phases of Execution", "Top SQL with Top Events", "Top SQL with Top Row Sources", "Complete List of SQL Text", "Top Sessions",
                     "Top Blocking Sessions", "Top DB Objects", "Top DB Files", "Activity Over Time" })
            Assert.True(r.Table(title) is { Rows.Count: > 0 }, $"table '{title}' missing or empty");
        var users = r.Table("Top User Events")!;
        Assert.Equal("enq: TX - row lock contention", users.Rows[0][0]);
        Assert.Equal(40.0, users.DataRows.First().Num("% Event"));
        // continuation rows inherit their keys (text prints them once, HTML repeats them)
        Assert.All(r.Table("Activity Over Time")!.DataRows, row => Assert.NotEmpty(row.Str("Slot Time (Duration)")));
        Assert.Equal(["1415053318", "524289", "1111"], System.Text.RegularExpressions.Regex.Matches(r.Table("Top Event P1/P2/P3 Values")!.Rows[0][2], "\"([^\"]*)\"").Select(m => m.Groups[1].Value));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HotEventScenarioProducesTheExpectedFindings(bool html)
    {
        var a = Analyze(html ? Html : Txt);
        Assert.Equal("ASH", a.Meta.ReportType);
        var titles = a.Findings.Select(f => f.Title).ToList();
        Assert.Contains(titles, t => t.Contains("enq: TX - row lock contention") && t.Contains("40%"));
        Assert.Contains(titles, t => t.Contains("7gt1k0abc2xyz") && t.Contains("58%"));
        Assert.Contains(titles, t => t.Contains("Application waits"));
        Assert.Contains(titles, t => t.Contains("321,4711(1)") && t.Contains("blocked"));
        Assert.Contains(titles, t => t.Contains("TESTAPP.STOCK"));
        Assert.Contains(titles, t => t.Contains("2 different execution plans"));
        Assert.Contains(titles, t => t.StartsWith("Hard parsing"));
        Assert.Contains(titles, t => t.Contains("decoded parameters"));
        var blocker = a.Findings.First(f => f.Title.Contains("321,4711"));
        Assert.Contains(blocker.Evidence, e => e.Label.Contains("blocker itself active") && e.Value.StartsWith("No"));
        var decoded = a.Findings.First(f => f.Title.Contains("decoded parameters"));
        Assert.Contains(decoded.Evidence, e => e.Note != null && e.Note.Contains("TX lock, mode 6"));
        // every finding carries both audiences' text and at least a doc reference
        Assert.All(a.Findings.Where(f => f.Severity != Severity.Ok), f =>
        {
            Assert.NotEmpty(f.Plain); Assert.NotEmpty(f.WhyItMatters); Assert.NotEmpty(f.Technical); Assert.NotEmpty(f.Docs);
        });
        Assert.DoesNotContain(a.Findings, f => f.Severity == Severity.Ok && f.Title.Contains("CPU") && false);
    }

    [Fact]
    public void SqlSnippetsUseTheAshWindowAndNoAwrOnlyPlaceholders()
    {
        var a = Analyze(Html);
        var code = string.Join("\n", a.Findings.SelectMany(f => f.Sql).Select(s => s.Code));
        Assert.Contains("TIMESTAMP '2026-11-12 09:00:00'", code);
        Assert.DoesNotContain("&begin_snap", code);
        Assert.Contains("FETCH FIRST", code);   // release 19.x
    }

    [Fact]
    public void TextAndHtmlVariantsAgree()
    {
        var h = Analyze(Html);
        var t = Analyze(Txt);
        Assert.Equal(h.Findings.Select(f => f.Title), t.Findings.Select(f => f.Title));
        Assert.Equal(h.Findings.Select(f => f.Severity), t.Findings.Select(f => f.Severity));
        Assert.Equal(h.Charts.Select(c => c.Id), t.Charts.Select(c => c.Id));
        Assert.Equal(h.Tables.Select(x => x.Title), t.Tables.Select(x => x.Title));
        Assert.Equal(h.Meta.AverageActiveSessions, t.Meta.AverageActiveSessions);
        Assert.Equal(h.Health.Score, t.Health.Score);
    }

    [Fact]
    public void ChartsExistAndAreReferencedConsistently()
    {
        var a = Analyze(Html);
        var ids = a.Charts.Select(c => c.Id).ToHashSet();
        foreach (var id in new[] { "ash-activity", "ash-top-events", "ash-wait-classes", "ash-cpu-vs-wait", "ash-top-sql", "ash-top-sessions", "ash-top-objects", "ash-top-modules" })
            Assert.Contains(id, ids);
        Assert.All(a.Findings.SelectMany(f => f.ChartRefs), id => Assert.Contains(id, ids));
        Assert.All(a.Charts, c => Assert.Equal(c.Labels.Count, c.Series[0].Values.Count));
        var timeline = a.Charts.First(c => c.Id == "ash-activity");
        Assert.Equal(11, timeline.Labels.Count);
        Assert.Equal(4, timeline.Reference);
    }

    [Fact]
    public void SummaryAndKpisSpeakAsh()
    {
        var a = Analyze(Html);
        Assert.Contains(a.Health.Paragraphs, p => p.StartsWith("This ASH report"));
        Assert.DoesNotContain(a.Health.Paragraphs, p => p.Contains("AWR") || p.Contains("DB time"));
        Assert.Contains(a.Kpis, k => k.Key == "aas" && k.Value == "5.20");
        Assert.Contains(a.Kpis, k => k.Key == "top-sql" && k.Value == "7gt1k0abc2xyz");
        Assert.Equal("7gt1k0abc2xyz", a.TopSql[0].SqlId);
        Assert.Contains("UPDATE STOCK", a.TopSql[0].Text);
    }

    [Fact]
    public void NoRuleFailsAndAwrRulesDoNotRun()
    {
        var a = Analyze(Txt);
        Assert.DoesNotContain(a.Warnings, w => w.Contains("could not complete"));
        Assert.Empty(a.Warnings);
        Assert.DoesNotContain(a.Findings, f => f.Category == "Memory" || f.SourceViews.Contains("DBA_HIST_SQLSTAT") && f.Title.Contains("elapsed"));
    }

    [Fact]
    public void TextParserNeverThrowsOnGarbage()
    {
        var r = AshTextParser.Parse("ASH Report For X/y\n\nTop User Events    DB/Inst: X/y\n---- ---\nrandom   \n\n\u0001\u0002 ---", "x.txt");
        Assert.Equal(ReportKind.Ash, r.Kind);
        Assert.Throws<AwrFormatException>(() => AshReportLoader.LoadContent("ASH Report For nothing"));
    }

    [Fact]
    public void HistoryDataSourceIsDetected()
    {
        var content = File.ReadAllText(Txt).Replace("V$ACTIVE_SESSION_HISTORY", "DBA_HIST_ACTIVE_SESS_HISTORY");
        var a = ReportProviderRegistry.AnalyzeContent(content, "h.txt").Analysis;
        Assert.Contains(a.Findings, f => f.Title.StartsWith("Data comes from the ASH history"));
        Assert.Contains("dba_hist_active_sess_history", string.Join("\n", a.Findings.SelectMany(f => f.Sql).Select(s => s.Code)));
    }

    /// <summary>Third-party reports live outside the repository (git-ignored) and are never committed; the test is skipped when absent.</summary>
    [ThirdPartyFact(ThirdPartyNeed.LocalCorpus)]
    public void LocalRealWorldSamplesAnalyzeWhenPresent()
    {
        var dir = FindLocalDir();
        if (dir == null) return;
        foreach (var file in Directory.GetFiles(dir, "*.html"))
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("ASH Report For")) continue;
            var a = ReportProviderRegistry.AnalyzeContent(content, file).Analysis;
            Assert.Equal("ASH", a.Meta.ReportType);
            Assert.False(string.IsNullOrEmpty(a.Meta.DbName));
            Assert.NotEmpty(a.Findings);
            Assert.NotEmpty(a.Charts);
            Assert.DoesNotContain(a.Warnings, w => w.Contains("could not complete"));
        }
    }

    private static string? FindLocalDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "EXAMPLES-local", "ASH");
            if (Directory.Exists(p)) return p;
        }
        return null;
    }
}
