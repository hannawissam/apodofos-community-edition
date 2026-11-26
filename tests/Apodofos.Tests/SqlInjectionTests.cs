// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text;
using Apodofos.Core.Analysis;
using Apodofos.Core.Model;
using Apodofos.Core.Parsing;

namespace Apodofos.Tests;

/// <summary>Names read from a report are untrusted: none of them may become executable text in the generated SQL.</summary>
public class SqlInjectionTests
{
    // built from fragments (see AGENTS.md); "zzmark" must only ever appear inside a literal or a comment
    private const string Mark = "zzmark";
    private static readonly string[] Payloads =
    [
        "Z' OR 1=1; DR" + "OP TABLE " + Mark + ".emp; --",
        "x]' OR 1=1\r\n/\nDR" + "OP TABLE " + Mark + ".emp;\u2028" + Mark + " /*",
        Mark + " OR 1=1",
        "1; DR" + "OP TABLE " + Mark + ".emp"
    ];

    private static IEnumerable<(string Name, Func<AwrReport> Load)> Reports()
    {
        foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "TestData")).OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            var content = File.ReadAllText(path);
            yield return (name, () => name.StartsWith("ash", StringComparison.Ordinal) ? AshReportLoader.LoadContent(content, name)
                : name.StartsWith("statspack", StringComparison.Ordinal) ? StatspackTextParser.Parse(content, name)
                : AwrReportLoader.LoadContent(content, name));
        }
        var synthetic = Synthetic.AwrHtml();
        yield return ("synthetic", () => AwrReportLoader.LoadContent(synthetic, "synthetic.html"));
        foreach (var segment in new[] { ("enq: TX - allocate ITL entry", "Segments by ITL Waits"), ("buffer busy waits", "Segments by Buffer Busy Waits") })
            foreach (var type in new[] { "TABLE", "INDEX", "TABLE PARTITION", "INDEX PARTITION" })
                yield return ($"{segment.Item1} {type}", () => SegmentReport(segment.Item1, segment.Item2, type, "19.0.0.0.0"));
    }

    private static AwrTable Table(int index, string title, string[] headers, params string[][] rows) =>
        new() { Id = "awr-t-" + index, Index = index, Title = title, Headers = [.. headers], Rows = [.. rows] };

    private static AwrReport SegmentReport(string ev, string segmentTable, string type, string release)
    {
        var report = new AwrReport
        {
            Info = new AwrReportInfo { DbName = "DEMO", Release = release, ElapsedMinutes = 60, DbTimeMinutes = 600, Cpus = 4, BeginSnapId = "10", EndSnapId = "11", InstanceNumber = 1 }
        };
        report.Tables.Add(Table(0, "Top 10 Foreground Events by Total Wait Time", ["Event", "Waits", "Total Wait Time (s)", "Avg wait", "% DB time", "Wait Class"], [ev, "100000", "12000", "120ms", "33.3", "Concurrency"]));
        report.Tables.Add(Table(1, segmentTable, ["Owner", "Tablespace Name", "Object Name", "Subobject Name", "Obj. Type", "Waits", "% of Capture"],
            ["APP", "USERS", "ORDERS", type.EndsWith("PARTITION", StringComparison.Ordinal) ? "P_2026" : "", type, "5000", "80.0"]));
        return report;
    }

    private static readonly HashSet<string> NameColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Owner", "Object Name", "Subobject Name", "Obj. Type", "SQL Id", "SQL ID", "SQL Module", "Module", "Service", "Action", "Program", "Event", "Event Caused",
        "Object ID", "Object Name (Type)", "Sid, Serial#", "Sid,Srl# (Inst)", "Sid", "Blocking Sid (Inst)", "Planhash", "PlanHash", "Snap Id", "P1 Value, P2 Value, P3 Value",
        "P1 Value", "PGA Target Est (MB)", "Statistic", "Statistic Name", "User", "File ID", "File Name", "Tablespace", "Filename", "Latch", "Latch Name", "SQL Text"
    };

    /// <summary>Suffixes (or replaces) every matching non-numeric name cell, the same way in every table so that look-ups still join.</summary>
    private static void Mutate(AwrReport report, Func<string, bool> column, string payload, bool replace)
    {
        foreach (var t in report.Tables)
            foreach (var row in t.Rows)
                for (var i = 0; i < row.Length; i++)
                {
                    var header = i < t.Headers.Count ? t.Headers[i] : "";
                    if (!column(header) || row[i].Length == 0) continue;
                    if (!NameColumns.Contains(header) && NumberParser.Parse(row[i]) is not null) continue;
                    row[i] = replace ? payload : row[i] + payload;
                }
    }

    private static void MutateInfo(AwrReport r, string p)
    {
        var i = r.Info;
        i.DbName += p; i.InstanceName += p; i.Release += p; i.HostName += p;
        i.BeginSnapId += p; i.EndSnapId += p; i.BeginSnapTime += p; i.EndSnapTime += p;
    }

    /// <summary>The statement text with comments and literals removed; null when a literal is left open.</summary>
    private static string? ExecutableText(string sql)
    {
        var code = new StringBuilder();
        for (var i = 0; i < sql.Length; i++)
        {
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (sql[i] == '-' && next == '-') { while (i < sql.Length && sql[i] != '\n') i++; code.Append('\n'); }
            else if (sql[i] == '/' && next == '*') { var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); if (end < 0) return null; i = end + 1; }
            else if (sql[i] is 'q' or 'Q' && next == '\'' && i + 2 < sql.Length && sql[i + 2] == '[')
            {
                var end = sql.IndexOf("]'", i + 3, StringComparison.Ordinal);
                if (end < 0) return null;
                i = end + 1;
            }
            else if (sql[i] is '\'' or '"')
            {
                var quote = sql[i];
                for (i++; ; i++)
                {
                    if (i >= sql.Length || sql[i] is '\n' or '\r' && quote == '\'' && sql.IndexOf(Mark, StringComparison.Ordinal) >= 0 && LineHasMark(sql, i)) return null;
                    if (sql[i] != quote) continue;
                    if (i + 1 < sql.Length && sql[i + 1] == quote) { i++; continue; }
                    break;
                }
                code.Append("''");
            }
            else code.Append(sql[i]);
        }
        return code.ToString();
    }

    private static bool LineHasMark(string sql, int end)
    {
        var start = sql.LastIndexOf('\n', Math.Max(0, end - 1)) + 1;
        return sql[start..end].Contains(Mark, StringComparison.Ordinal);
    }

    private static void AssertSafe(AnalysisResult result, string context, HashSet<string> reached)
    {
        var meta = result.Meta;
        foreach (var header in new[] { meta.DbName, meta.Instance, meta.BeginSnap, meta.EndSnap, meta.BeginTime, meta.EndTime })
            Assert.DoesNotContain(header ?? "", c => c is '\r' or '\n' or '\u2028');
        foreach (var f in result.Findings)
        {
            Assert.DoesNotContain(f.Title, c => c is '\r' or '\n' or '\u2028');
            foreach (var s in f.Sql)
            {
                foreach (var comment in new[] { s.Title, s.Caution ?? "" })
                    Assert.DoesNotContain(comment, c => c is '\r' or '\n' or '\u2028');
                if (s.Code.Contains(Mark, StringComparison.Ordinal)) reached.Add(s.Title.Split(Mark)[0]);
                var text = ExecutableText(s.Code);
                Assert.True(text is not null && !text.Contains(Mark, StringComparison.OrdinalIgnoreCase) && !text.Contains("1=1", StringComparison.Ordinal),
                    $"{context} / {s.Title}\n{s.Code}");
            }
        }
    }

    [Fact]
    public void HostileNames_NeverBecomeExecutableSql()
    {
        var reached = new HashSet<string>();
        foreach (var (name, load) in Reports())
        {
            var headers = load().Tables.SelectMany(t => t.Headers).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var payload in Payloads)
                foreach (var replace in new[] { false, true })
                {
                    // one column name at a time, then all of them together, then the report header
                    foreach (var column in headers.Where(NameColumns.Contains).Select(h => (Func<string, bool>)(x => x.Equals(h, StringComparison.OrdinalIgnoreCase)))
                                 .Append(NameColumns.Contains).Append(_ => true))
                    {
                        var report = load();
                        Mutate(report, column, payload, replace);
                        AssertSafe(AnalysisEngine.Analyze(report), name, reached);
                    }
                    var withInfo = load();
                    MutateInfo(withInfo, payload);
                    AssertSafe(AnalysisEngine.Analyze(withInfo), name + " (header)", reached);
                }
        }
        // the test is only meaningful if hostile names really reached the statements
        Assert.True(reached.Count >= 8, $"only {reached.Count} statements received a hostile name:\n{string.Join("\n", reached.Order())}");
    }

    [Theory]
    [InlineData("enq: TX - allocate ITL entry", "Segments by ITL Waits", "TABLE PARTITION", "ALTER TABLE APP.ORDERS MODIFY PARTITION P_2026 INITRANS 16")]
    [InlineData("enq: TX - allocate ITL entry", "Segments by ITL Waits", "INDEX", "ALTER INDEX APP.ORDERS REBUILD ONLINE INITRANS 16;")]
    public void OrdinaryNames_AreUsedUnchanged(string ev, string table, string type, string expected)
    {
        var result = AnalysisEngine.Analyze(SegmentReport(ev, table, type, "19.0.0.0.0"));
        Assert.Contains(result.Findings.SelectMany(f => f.Sql), s => s.Code.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void HostileSegmentName_IsReplacedByAVariableOrEscaped()
    {
        var report = SegmentReport("enq: TX - allocate ITL entry", "Segments by ITL Waits", "TABLE", "19.0.0.0.0");
        report.Tables[1].Rows[0][2] = "Z' OR 1=1; DR" + "OP TABLE hr.emp; --";
        var code = AnalysisEngine.Analyze(report).Findings.SelectMany(f => f.Sql).Single(s => s.Title.StartsWith("Raise INITRANS on the hottest", StringComparison.Ordinal)).Code;
        Assert.Contains("ALTER TABLE APP.&object_name INITRANS 16", code);
        Assert.DoesNotContain("DR" + "OP", code);
    }
}
