// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using QuietLoad.Core.Analysis;
using QuietLoad.Core.Model;
using QuietLoad.Core.Parsing;
using QuietLoad.Core.Providers;

namespace QuietLoad.Tests;

/// <summary>Statspack support: provider routing, parsing of the 10g-style and 19c-style synthetic layouts, shared and Statspack-specific rules.</summary>
public class StatspackTests
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "TestData");
    private static string Text10g => File.ReadAllText(Path.Combine(Dir, "statspack-10g-layout.txt"));
    private static string Text19c => File.ReadAllText(Path.Combine(Dir, "statspack-19c-layout.txt"));

    private static AwrReport Parse(string text) => StatspackTextParser.Parse(text, "statspack.txt");
    private static AnalysisResult Analyze(string text) => ReportProviderRegistry.AnalyzeContent(text, "statspack.txt").Analysis;

    public static IEnumerable<object[]> Layouts() => [["statspack-10g-layout.txt"], ["statspack-19c-layout.txt"]];

    // ------------------------------------------------------------------------------------------ routing

    [Fact]
    public void StatspackProviderIsRegistered()
    {
        var p = Assert.Single(ReportProviderRegistry.All, x => x.Id == "oracle-statspack");
        Assert.Equal("Oracle Database", p.Engine);
    }

    [Theory, MemberData(nameof(Layouts))]
    public void StatspackTextRoutesToStatspackProvider(string file)
    {
        var text = File.ReadAllText(Path.Combine(Dir, file));
        Assert.Equal("oracle-statspack", ReportProviderRegistry.Resolve(text, file)!.Id);
    }

    [Fact]
    public void AwrTextStillRoutesToAwrProvider()
    {
        foreach (var f in new[] { "awr-19c-layout.txt", "small-awr.txt" })
        {
            var text = File.ReadAllText(Path.Combine(Dir, f));
            Assert.Equal("oracle-awr", ReportProviderRegistry.Resolve(text, f)!.Id);
        }
    }

    // ------------------------------------------------------------------------------------------ header

    [Fact]
    public void Header10g()
    {
        var r = Parse(Text10g);
        Assert.Equal(ReportKind.Statspack, r.Kind);
        Assert.Equal(AwrReportFormat.Text, r.Format);
        Assert.Equal("TESTDB10", r.Info.DbName);
        Assert.Equal("1234567890", r.Info.DbId);
        Assert.Equal("testdb10", r.Info.InstanceName);
        Assert.Equal("10.2.0.5.0", r.Info.Release);
        Assert.Equal("testhost01", r.Info.HostName);
        Assert.Equal(4, r.Info.Cpus);
        Assert.Equal("101", r.Info.BeginSnapId);
        Assert.Equal("102", r.Info.EndSnapId);
        Assert.Equal(60.08, r.Info.ElapsedMinutes);
        // DB time is not printed before 11g: taken from the time model (6,165 s)
        Assert.Equal(6165.0 / 60, r.Info.DbTimeMinutes!.Value, 3);
        Assert.InRange(r.Info.AverageActiveSessions!.Value, 1.6, 1.8);
    }

    [Fact]
    public void Header19c()
    {
        var r = Parse(Text19c);
        Assert.Equal("TESTDB19", r.Info.DbName);
        Assert.Equal("testhost01", r.Info.HostName);
        Assert.Equal("Linux x86 64-bit", r.Info.Platform);
        Assert.Equal(8, r.Info.Cpus);
        Assert.Equal(4, r.Info.Cores);
        Assert.Equal(180.40, r.Info.DbTimeMinutes);
        Assert.Equal(30.12, r.Info.ElapsedMinutes);
        Assert.InRange(r.Info.AverageActiveSessions!.Value, 5.9, 6.1);
    }

    // ------------------------------------------------------------------------------------------ tables

    [Theory, MemberData(nameof(Layouts))]
    public void KeyTablesAreRecognised(string file)
    {
        var r = Parse(File.ReadAllText(Path.Combine(Dir, file)));
        foreach (var title in new[] { "Load Profile", "Instance Efficiency Percentages", "Shared Pool Statistics", "Host CPU", "Time Model Statistics",
                                      "Foreground Wait Events", "Background Wait Events", "Wait Classes by Total Wait Time", "init.ora Parameters" })
        {
            var t = r.TableStartingWith(title);
            Assert.True(t is { Rows.Count: > 0 }, $"table '{title}' missing or empty in {file}");
        }
        Assert.NotNull(r.TableStartingWith("Top 5 Timed"));
        Assert.NotEmpty(r.EfficiencyPercentages());
        Assert.NotEmpty(r.InitParameters());
        Assert.Equal(r.Tables.Count, r.Tables.Select(t => t.Id).Distinct().Count());
        Assert.All(r.Tables, t => Assert.Equal($"awr-t-{t.Index}", t.Id));
    }

    [Fact]
    public void NumericValues10g()
    {
        var r = Parse(Text10g);
        var lp = r.Table("Load Profile")!;
        Assert.Equal(120500.25, lp.RowByLabel("Redo size (bytes)")!.Value.Num("Per Second"));
        Assert.Equal(95.4, lp.RowByLabel("Hard parses (SQL)")!.Value.Num("Per Second"));
        Assert.Equal(40.0, lp.RowByLabel("Transactions")!.Value.Num("Per Second"));
        Assert.NotNull(lp.RowByLabel("DB Time(s)")); // derived for releases that do not print it

        var top = r.TableStartingWith("Top 5 Timed")!;
        // "CPU time" is mapped to DB CPU; the idle 'dispatcher listen timer' is moved out of the ranking
        Assert.Equal("DB CPU", top.Rows[0][0]);
        Assert.DoesNotContain(top.Rows, x => x[0].StartsWith("dispatcher", StringComparison.Ordinal));
        Assert.Contains(r.Tables, t => t.Title == "Top 5 Idle Events (excluded)" && t.Rows.Count == 1);
        Assert.Equal(32.1, top.RowByLabel("DB CPU")!.Value.Num("% DB time"));

        var fg = r.Table("Foreground Wait Events")!;
        var latch = fg.RowByLabel("latch free")!.Value;
        Assert.Equal(412300, latch.Num("Waits"));
        Assert.Equal(1150, latch.Num("Total Wait Time (s)"));
        Assert.InRange(latch.Num("% DB time")!.Value, 18.5, 18.8);
        Assert.Equal("Other", latch.Str("Wait Class"));
        Assert.Null(fg.RowByLabel("SQL*Net message from client")); // idle events are separated
        Assert.NotNull(r.Table("Idle Wait Events")!.RowByLabel("SQL*Net message from client"));

        var host = r.TableStartingWith("Host CPU")!.DataRows.First();
        Assert.Equal(23.5, host.Num("%Idle"));
        Assert.Equal(62.4, host.Num("%User"));

        var tm = r.Table("Time Model Statistics")!;
        Assert.Equal(1980.0, tm.RowByLabel("DB CPU")!.Value.Num("Time (s)"));

        var act = r.Table("Instance Activity Stats")!;
        Assert.Equal(15, act.Rows.Count); // the two continuation pages are merged
        Assert.Equal(434400000, act.RowByLabel("redo size")!.Value.Num("Total"));
    }

    [Fact]
    public void SqlSectionsKeepHashValueModuleAndWrappedText()
    {
        var r = Parse(Text10g);
        var gets = r.Table("SQL ordered by Gets")!;
        Assert.Equal(2, gets.Rows.Count);
        var first = gets.DataRows.First();
        Assert.Equal("3111222333", first.Str("SQL Id")); // the hash value is exposed as the statement id
        Assert.Equal("ORDERENTRY", first.Str("SQL Module"));
        Assert.Equal(120400000, first.Num("Buffer Gets"));
        Assert.Contains("order_lines l WHERE l.order_id = o.order_id", first.Str("SQL Text"));
        Assert.Equal(24.3, r.Table("SQL ordered by Elapsed Time")!.DataRows.First().Num("%Total"));
        Assert.True(r.SqlTexts().ContainsKey("4222333444"));

        var s19 = Parse(Text19c).Table("SQL ordered by Elapsed Time")!;
        Assert.Equal("abcd1234efgh5", s19.DataRows.First().Str("SQL Id"));
        Assert.Equal(6300.10, s19.DataRows.First().Num("Elapsed Time (s)"));
    }

    [Fact]
    public void HistogramHasBucketsAndFullEventNames()
    {
        var h = Parse(Text10g).Table("Wait Event Histogram")!;
        Assert.Equal("<1ms", h.Headers[2]);
        Assert.Equal(">1s", h.Headers[^1]);
        var row = h.RowByLabel("db file sequential read")!.Value;
        Assert.Equal(690000, row.Num("Total Waits"));
        Assert.Equal(70.2, row.NumAt(2));
    }

    [Fact]
    public void Segments19c()
    {
        var r = Parse(Text19c);
        var seg = r.Table("Segments by Logical Reads")!;
        Assert.Equal("ORDER_HEADER", seg.DataRows.First().Str("Object Name"));
        Assert.Equal(62.5, seg.DataRows.First().Num("%Total"));
        Assert.Equal(99.7, r.Table("Segments by Row Lock Waits")!.DataRows.First().Num("% of Capture"));
    }

    [Fact]
    public void HtmlViewHasTableAnchorsAndIsEscaped()
    {
        var r = Parse(Text10g.Replace("ORDERENTRY", "ORDER<b>ENTRY"));
        Assert.Contains("<pre id=\"awr-t-", r.SanitizedHtml);
        Assert.DoesNotContain("<b>ENTRY", r.SanitizedHtml);
        Assert.Contains("&lt;b&gt;", r.SanitizedHtml);
    }

    [Fact]
    public void ParserNeverThrowsOnDamagedInput()
    {
        var damaged = Text10g[..(Text10g.Length / 3)] + "\n~~~~~\n  ----- ---\n";
        var r = Parse(damaged);
        Assert.Equal(ReportKind.Statspack, r.Kind);
        var a = new OracleStatspackProvider().Analyze("STATSPACK report for\n\nnothing else", "x.txt");
        Assert.NotNull(a.Analysis);
    }

    // ------------------------------------------------------------------------------------------ analysis

    [Theory, MemberData(nameof(Layouts))]
    public void AnalysisCompletesWithChartsAndNoRuleFailures(string file)
    {
        var a = Analyze(File.ReadAllText(Path.Combine(Dir, file)));
        Assert.DoesNotContain(a.Warnings, w => w.Contains("could not complete"));
        Assert.NotEmpty(a.Findings);
        var ids = a.Charts.Select(c => c.Id).ToList();
        foreach (var id in new[] { "db-time-breakdown", "top-events", "time-model", "host-cpu", "efficiency", "load-profile", "fg-events" })
            Assert.Contains(id, ids);
        Assert.All(a.Charts, c => Assert.NotEmpty(c.Labels));
        Assert.Contains("Statspack report", string.Join(" ", a.Health.Paragraphs));
        Assert.Equal(a.Findings.Count, a.Findings.Select(f => f.Id).Distinct().Count());
    }

    [Fact]
    public void KpisAreFilled()
    {
        var a = Analyze(Text10g);
        string V(string id) => a.Kpis.First(k => k.Key == id).Value;
        Assert.Equal("40.0", V("tps"));
        Assert.StartsWith("901", V("execs"));
        Assert.Equal("32.1%", V("dbcpu"));
        Assert.StartsWith("1.7", V("aas"));
    }

    [Fact]
    public void SharedRulesFireOnHotEventScenario10g()
    {
        var a = Analyze(Text10g);
        Assert.Contains(a.Findings, f => f.Category == "Wait events" && f.Title.Contains("'latch free'") && f.Severity <= Severity.Medium);
        Assert.Contains(a.Findings, f => f.Category == "Wait events" && f.Title.Contains("'buffer busy waits'"));
        Assert.Contains(a.Findings, f => f.Category == "SQL" && f.Title.Contains("3111222333"));
        Assert.Contains(a.Findings, f => f.Category == "Parsing");
        // the idle dispatcher wait must never become a finding
        Assert.DoesNotContain(a.Findings, f => f.Title.Contains("dispatcher listen timer") && f.Category == "Wait events");
    }

    [Fact]
    public void SharedRulesFireOnRowLockScenario19c()
    {
        var a = Analyze(Text19c);
        var lockFinding = a.Findings.First(f => f.Title.Contains("enq: TX - row lock contention"));
        Assert.Equal(Severity.Critical, lockFinding.Severity);
        Assert.Contains(a.Findings, f => f.Category == "SQL" && f.Title.Contains("abcd1234efgh5"));
        Assert.Contains(a.Findings, f => f.Title.Contains("ORDER_HEADER"));
    }

    // ------------------------------------------------------------------------------------------ Statspack rules

    [Fact]
    public void LegacyWaitNamesAreEnrichedNotDuplicated()
    {
        var a = Analyze(Text10g);
        Assert.Single(a.Findings, f => f.Title.Contains("'latch free'"));
        var latch = a.Findings.First(f => f.Title.Contains("'latch free'"));
        Assert.Contains(latch.Evidence, e => e.Label.Contains("cache buffers chains"));
        Assert.Contains(latch.Sql, s => s.Code.Contains("v$latch", StringComparison.OrdinalIgnoreCase));
        var bbw = a.Findings.First(f => f.Title.Contains("'buffer busy waits'"));
        Assert.Contains(bbw.Evidence, e => e.Label.Contains("data block"));
    }

    [Fact]
    public void IdleTopEventAndMissingSegmentsAreExplained()
    {
        var a = Analyze(Text10g);
        Assert.Contains(a.Findings, f => f.Category == "Statspack" && f.Title.Contains("idle waits") && f.Severity == Severity.Info);
        var seg = a.Findings.First(f => f.Category == "Statspack" && f.Title.Contains("no segment statistics"));
        Assert.Contains(seg.Sql, s => s.Code.Contains("i_snap_level => 7"));
        // the 19c fixture does have segment sections
        Assert.DoesNotContain(Analyze(Text19c).Findings, f => f.Title.Contains("no segment statistics"));
    }

    [Fact]
    public void LowSnapshotLevelIsDetectedWhenSqlSectionsAreMissing()
    {
        var text = Text10g;
        var from = text.IndexOf("SQL ordered by Elapsed", StringComparison.Ordinal);
        var to = text.IndexOf("Instance Activity Stats", StringComparison.Ordinal);
        var noSql = text[..from] + text[to..];
        var a = Analyze(noSql);
        var f = a.Findings.First(x => x.Category == "Statspack" && x.Title.Contains("no SQL sections"));
        Assert.True(f.Severity <= Severity.Medium);
        Assert.Contains(f.Sql, s => s.Code.Contains("STATSPACK.SNAP(i_snap_level => 7)"));
        Assert.DoesNotContain(Analyze(Text10g).Findings, x => x.Title.Contains("no SQL sections"));
    }

    [Fact]
    public void TimedStatisticsOffIsReported()
    {
        var text = Text10g.Replace("timed_statistics              TRUE", "timed_statistics              FALSE");
        var f = Analyze(text).Findings.First(x => x.Title.Contains("Timing statistics were switched off"));
        Assert.Equal(Severity.High, f.Severity);
        Assert.DoesNotContain(Analyze(Text10g).Findings, x => x.Title.Contains("Timing statistics were switched off"));

        var warn = Text10g.Replace("Cache Sizes   ", "WARNING: timed_statistics setting was set to 'FALSE' in the snapshot period: TIMINGS ARE INVALID\n\nCache Sizes   ");
        Assert.Contains(Analyze(warn).Findings, x => x.Title.Contains("Timing statistics were switched off"));
    }

    [Fact]
    public void LowSqlCoverageIsFlagged()
    {
        var text = Text10g.Replace("Captured SQL accounts for   71.3% of Total Buffer Gets", "Captured SQL accounts for    9.3% of Total Buffer Gets");
        Assert.Contains(Analyze(text).Findings, f => f.Category == "Statspack" && f.Title.Contains("covers only"));
        Assert.DoesNotContain(Analyze(Text10g).Findings, f => f.Title.Contains("covers only"));
    }

    [Theory, MemberData(nameof(Layouts))]
    public void EditionAndUsageGuidanceIsPresent(string file)
    {
        var a = Analyze(File.ReadAllText(Path.Combine(Dir, file)));
        var f = a.Findings.First(x => x.Title.Contains("free alternative to AWR and ASH"));
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Contains("Diagnostics Pack", f.Plain);
        Assert.Contains(f.Sql, s => s.Code.Contains("STATSPACK.SNAP"));
        Assert.Contains(f.Sql, s => s.Code.Contains("spreport.sql") && s.Code.Contains("sprepsql.sql") && s.Code.Contains("spauto.sql"));
    }

    // ------------------------------------------------------------------------------------------ Kind-aware text and SQL

    [Theory, MemberData(nameof(Layouts))]
    public void NoAwrOnlyViewsOrSqlAreSuggested(string file)
    {
        var a = Analyze(File.ReadAllText(Path.Combine(Dir, file)));
        foreach (var f in a.Findings)
        {
            Assert.All(f.Sql, s =>
            {
                Assert.DoesNotContain("dba_hist_", s.Code, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("DISPLAY_AWR", s.Code, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("active_sess_history", s.Code, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("DBMS_WORKLOAD_REPOSITORY", s.Code, StringComparison.OrdinalIgnoreCase);
            });
            Assert.All(f.SourceViews, v => Assert.DoesNotContain("DBA_HIST", v, StringComparison.OrdinalIgnoreCase));
            if (f.Category == "Statspack") continue; // the Statspack rules themselves explain what AWR/ASH are
            Assert.DoesNotContain("awrrpt", f.Technical + string.Join(" ", f.Recommendations), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DBA_HIST", f.Technical, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WaitTrendUsesStatsTables()
    {
        var a = Analyze(Text10g);
        var f = a.Findings.First(x => x.Title.Contains("'db file sequential read'"));
        var trend = Assert.Single(f.Sql, s => s.Code.Contains("stats$system_event"));
        Assert.Contains("perfstat.stats$snapshot", trend.Code);
        Assert.Contains("e.event = 'db file sequential read'", trend.Code);
        Assert.Contains("101", trend.Code.Replace("&begin_snap", "101")); // substitution variables are filled
        Assert.Contains(f.SourceViews, v => v.Contains("STATS$SYSTEM_EVENT"));
    }

    [Fact]
    public void SqlFindingUsesHashValueHistory()
    {
        var f = Analyze(Text10g).Findings.First(x => x.Category == "SQL" && x.Title.Contains("3111222333"));
        var hist = f.Sql.First(s => s.Code.Contains("stats$sql_summary"));
        Assert.Contains("old_hash_value = 3111222333", hist.Code);
        Assert.Contains(f.Recommendations, r => r.Contains("not available from Statspack data"));
        Assert.DoesNotContain(f.Sql, s => s.Code.Contains("DBMS_SQLTUNE.CREATE_TUNING_TASK", StringComparison.OrdinalIgnoreCase) && s.Code.Contains("begin_snap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AshOnlyAdviceIsReplacedByStatspackAdvice()
    {
        var a = Analyze(Text19c);
        var f = a.Findings.First(x => x.Title.Contains("enq: TX - row lock contention"));
        Assert.DoesNotContain(f.Sql, s => s.Code.Contains("ash", StringComparison.OrdinalIgnoreCase) && s.Code.Contains("sample", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(f.Recommendations, r => r.Contains("Statspack does not sample sessions"));
    }

    [Fact]
    public void AwrReportsAreUnaffectedByTheAdapter()
    {
        var awr = ReportProviderRegistry.AnalyzeFile(Path.Combine(Dir, "awr-19c-layout.txt")).Analysis;
        Assert.Contains(awr.Findings.SelectMany(f => f.Sql), s => s.Code.Contains("dba_hist", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(awr.Findings, f => f.Category == "Statspack");
        Assert.DoesNotContain("Statspack", string.Join(" ", awr.Health.Paragraphs));
    }
}
