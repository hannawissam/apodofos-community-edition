// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Core.Analysis;
using Apodofos.Core.Parsing;

namespace Apodofos.Tests;

/// <summary>
/// Round trip: real HTML report -> awrrpt-style text -> text parser -> analysis must agree with the HTML analysis.
/// </summary>
public class TextFormatTests
{
    public static IEnumerable<object[]> Reports() => Samples.AllHtmlData();

    private static (Apodofos.Core.Model.AwrReport Html, Apodofos.Core.Model.AwrReport Text, string TextContent) RoundTrip(string path)
    {
        var html = Samples.Load(path);
        var text = TextReportRenderer.Render(html);
        var parsed = AwrReportLoader.LoadContent(text, "roundtrip.txt");
        return (html, parsed, text);
    }

    [Theory, MemberData(nameof(Reports))]
    public void HeaderInfoSurvivesTextFormat(string path)
    {
        var (h, t, _) = RoundTrip(path);
        Assert.Equal(Apodofos.Core.Model.AwrReportFormat.Text, t.Format);
        Assert.Equal(h.Info.DbName, t.Info.DbName);
        Assert.Equal(h.Info.Release, t.Info.Release);
        Assert.Equal(h.Info.BeginSnapId, t.Info.BeginSnapId);
        Assert.Equal(h.Info.EndSnapId, t.Info.EndSnapId);
        Assert.Equal(h.Info.ElapsedMinutes, t.Info.ElapsedMinutes);
        Assert.Equal(h.Info.DbTimeMinutes, t.Info.DbTimeMinutes);
        Assert.Equal(h.Info.Cpus, t.Info.Cpus);
    }

    [Theory, MemberData(nameof(Reports))]
    public void KeyTablesAreRecognised(string path)
    {
        var (h, t, _) = RoundTrip(path);
        foreach (var title in new[] { "Load Profile", "Foreground Wait Events", "Time Model Statistics", "SQL ordered by Elapsed Time", "Tablespace IO Stats", "init.ora Parameters" })
        {
            var a = h.Table(title);
            var b = t.Table(title);
            if (a == null) continue; // real reports can legitimately lack a section ("No data exists for this section", or only "Modified Parameters"): nothing to compare
            Assert.True(b != null, $"text parser did not find table '{title}'");
            Assert.True(b!.Rows.Count >= a!.Rows.Count * 0.9, $"'{title}': {b.Rows.Count} rows in text vs {a.Rows.Count} in HTML");
        }
    }

    [Theory, MemberData(nameof(Reports))]
    public void FindingsMatchTheHtmlAnalysis(string path)
    {
        var (h, t, _) = RoundTrip(path);
        var fh = AnalysisEngine.Analyze(h).Findings;
        var ft = AnalysisEngine.Analyze(t).Findings;
        Assert.DoesNotContain(AnalysisEngine.Analyze(t).Warnings, w => w.Contains("could not complete"));

        // The most important findings (Critical..Medium) must be the same ones.
        var important = fh.Where(f => f.Severity <= Severity.Medium).Select(f => f.Title).ToHashSet();
        var textTitles = ft.Select(f => f.Title).ToHashSet();
        var missing = important.Where(x => !textTitles.Contains(x)).ToList();
        Assert.True(missing.Count <= Math.Max(1, important.Count / 4),
            "Findings lost in text format: " + string.Join(" | ", missing));
        Assert.InRange(Math.Abs(AnalysisEngine.Analyze(h).Health.Score - AnalysisEngine.Analyze(t).Health.Score), 0, 12);
    }

    [Theory, MemberData(nameof(Reports))]
    public void LoadProfileValuesAreExact(string path)
    {
        var (h, t, _) = RoundTrip(path);
        var a = h.Table("Load Profile")!;
        var b = t.Table("Load Profile")!;
        foreach (var row in a.DataRows)
        {
            var other = b.RowByLabel(row.Label);
            Assert.True(other is not null, $"Load Profile row '{row.Label}' missing in text");
            Assert.Equal(row.Num("Per Second"), other!.Value.Num("Per Second"));
        }
    }
}

public class RealTextLayoutTests
{
    private static Apodofos.Core.Model.AwrReport Load() =>
        AwrReportLoader.LoadFile(Path.Combine(AppContext.BaseDirectory, "TestData", "awr-19c-layout.txt"));

    [Fact]
    public void HeaderIsRead()
    {
        var i = Load().Info;
        Assert.Equal("TESTDB19", i.DbName);
        Assert.Equal("19.0.0.0.0", i.Release);
        Assert.Equal("659", i.BeginSnapId);
        Assert.Equal("667", i.EndSnapId);
        Assert.Equal(121.57, i.ElapsedMinutes);
        Assert.Equal(1942.31, i.DbTimeMinutes);
        Assert.Equal(32, i.Cpus);
        Assert.Equal(235.91, i.MemoryGb);
    }

    [Fact]
    public void LoadProfileUsesTheHeadingOnTheTildeLine()
    {
        var t = Load().Table("Load Profile");
        Assert.NotNull(t);
        Assert.Equal(16.0, t!.RowByLabel("DB Time(s)")?.Num("Per Second"));
        Assert.Equal(6004534.4, t.RowByLabel("Redo size (bytes)")?.Num("Per Second"));
        Assert.Equal(238.9, t.RowByLabel("Transactions")?.Num("Per Second"));
        Assert.Equal(25131.5, t.RowByLabel("Redo size (bytes)")?.Num("Per Transaction"));
    }

    [Fact]
    public void EfficiencyAndCacheGridsAreRead()
    {
        var r = Load();
        var eff = r.EfficiencyPercentages();
        Assert.Equal(99.79, eff["buffer nowait %"]);
        Assert.Equal(76.54, eff["parse cpu to parse elapsd %"]);
        Assert.Equal(99.71, eff["% non parse cpu"]);
        Assert.NotNull(r.Table("Shared Pool Statistics")?.RowByLabel("Memory Usage %"));
    }

    [Fact]
    public void WaitTablesAreRead()
    {
        var r = Load();
        var top = r.TableStartingWith("Top 10 Foreground Events");
        Assert.NotNull(top);
        var bbw = top!.RowByLabel("buffer busy waits");
        Assert.NotNull(bbw);
        Assert.Equal(6633272, bbw!.Value.Num("Waits"));
        Assert.Equal(11.5, bbw.Value.Num("% DB time"));
        Assert.Equal("Concurrency", bbw.Value.Str("Wait Class"));
        Assert.Equal(90100, top.RowByLabel("DB CPU")!.Value.Num("Total Wait Time (sec)", "Time (sec)", "Time"));
        var wc = r.Table("Wait Classes by Total Wait Time");
        Assert.Equal(77.3, wc?.RowByLabel("DB CPU")?.Num("% DB time"));
    }

    [Fact]
    public void NoteLinesAndSqlTextDoNotBreakTables()
    {
        var r = Load();
        var tm = r.Table("Time Model Statistics");
        Assert.NotNull(tm);
        Assert.Equal(99.8, tm!.RowByLabel("sql execute elapsed time")?.Num("% of DB Time"));
        var sql = r.Table("SQL ordered by Elapsed Time");
        Assert.NotNull(sql);
        Assert.Contains(sql!.DataRows, x => x.Str("SQL Id") == "abcd1234efgh5");
        Assert.Equal(26.5, sql.DataRows.First(x => x.Str("SQL Id") == "abcd1234efgh5").Num("%Total"));
    }

    [Fact]
    public void AnalysisWorksOnTextReport()
    {
        var a = AnalysisEngine.Analyze(Load());
        Assert.DoesNotContain(a.Warnings, w => w.Contains("could not complete"));
        Assert.Contains(a.Findings, f => f.Title.Contains("buffer busy waits", StringComparison.OrdinalIgnoreCase));
        Assert.True(a.Health.Score is > 0 and <= 100);
    }

    [Fact]
    public void SqlModuleAndTextAreCaptured()
    {
        var sql = Load().Table("SQL ordered by Elapsed Time")!;
        Assert.Equal(2, sql.Rows.Count);
        var row = sql.DataRows.First();
        Assert.Equal("e:ONT:cp:wsh/WSHPSGL_SUB", row.Str("SQL Module"));
        Assert.StartsWith("BEGIN WSH_PICK_LIST", row.Str("SQL Text"));
        Assert.Equal(82.56, sql.DataRows.Last().Num("Elapsed Time per Exec (s)"));
    }

    [Fact]
    public void StackedTablespaceRowsAreJoined()
    {
        var t = Load().Table("Tablespace IO Stats")!;
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal(120000, t.RowByLabel("APPS_TS_TX_DATA")!.Value.Num("Reads"));
        Assert.Equal(219754, t.RowByLabel("UNDOTBS1")!.Value.Num("Writes"));
    }

    [Fact]
    public void CacheSizesAndHostCpuAreRead()
    {
        var r = Load();
        var cs = r.Table("Cache Sizes")!;
        Assert.Equal("8K", cs.RowByLabel("Buffer Cache")!.Value.Cells[4]);
        Assert.Equal(40.4, r.TableStartingWith("Host CPU")!.DataRows.First().Num("%User"));
        Assert.Equal(57.3, r.TableStartingWith("Host CPU")!.DataRows.First().Num("%Idle"));
    }

    [Fact]
    public void LargeTextReportParsesQuickly()
    {
        var one = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "awr-19c-layout.txt"));
        var sb = new System.Text.StringBuilder();
        while (sb.Length < 5_000_000) sb.Append(one);
        // Best of three: a single run is noisy when other tests (CLI child processes) compete for CPU.
        long best = long.MaxValue; Apodofos.Core.Model.AwrReport rep = null!;
        for (var i = 0; i < 6 && best >= 2000; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            rep = AwrTextParser.Parse(sb.ToString());
            sw.Stop();
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        Assert.True(best < 2000, $"took {best} ms");
        Assert.True(rep.Tables.Count > 100);
    }

    [Theory, InlineData(""), InlineData("~~~~\n----\n"), InlineData("----\n----\n----\n--- ---\nx")]
    public void GarbageNeverThrows(string text) => Assert.NotNull(AwrTextParser.Parse(text));
}
