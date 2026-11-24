// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text.RegularExpressions;
using Apodofos.Core.Analysis;
using Apodofos.Core.Model;
using Apodofos.Core.Parsing;
using Apodofos.Core.Providers;

namespace Apodofos.Tests;

/// <summary>Regression tests for the trust defects found by the independent audit (all on synthetic fixtures).</summary>
public class ReliabilityTests
{
    private static string TestData(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));
    private static AnalysisResult Awr(Action<Synthetic.AwrOptions>? o = null) => ReportProviderRegistry.AnalyzeContent(Synthetic.AwrHtml(o), "synthetic.html").Analysis;
    private static AnalysisResult Run(string content, string name) => ReportProviderRegistry.AnalyzeContent(content, name).Analysis;

    // ---------------------------------------------------------------------------------------- 1. impossible values

    [Fact]
    public void ImpossibleSharesAreNeverPrintedNorEscalatedAndAWarningLeadsTheReport()
    {
        var a = Awr(o => { o.SqlShare = "1,419.0"; o.CpuShare = "773.0"; });
        Assert.Equal("Poor", a.DataQuality.Level);
        Assert.Contains(a.DataQuality.Alerts, x => x.Contains("cannot be true", StringComparison.OrdinalIgnoreCase));
        Assert.False(a.Health.Reliable);
        Assert.Equal("n/a", a.Health.Grade);
        var text = string.Join("\n", a.Findings.SelectMany(f => new[] { f.Title, f.Plain, f.Technical }.Concat(f.Evidence.Select(e => e.Value + " " + e.Note))));
        Assert.DoesNotContain("1,419", text);
        Assert.DoesNotContain("773", text);
        Assert.All(a.Findings, f => Assert.InRange(f.Impact, 0, 100));
        // the statement whose share is impossible is not ranked on it
        Assert.DoesNotContain(a.ActionPlan, p => p.ExpectedImpactPct > 100);
        Assert.Contains(a.DataQuality.Alerts, x => x.Contains("not reliable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DbCpuSlightlyAbove100PercentOfDbTimeIsARealReportValueNotAnImpossibleOne()
    {
        // A real AWR printed DB CPU = 101.13% of DB time ("Captured Time accounts for 103.6%"): DB CPU and DB time are measured separately.
        var a = Awr(o => o.CpuShare = "101.13");
        Assert.DoesNotContain(a.DataQuality.Alerts, x => x.Contains("cannot be true", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(a.DataQuality.Alerts, x => x.Contains("not reliable", StringComparison.OrdinalIgnoreCase) && x.Contains("impossible", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(a.DataQuality.Notes, n => n.Contains("Impossible share", StringComparison.OrdinalIgnoreCase));
        Assert.True(a.Health.Reliable);
        Assert.DoesNotContain(a.Findings, f => f.DataQuality.Contains("impossible value", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DbCpuUpTo150PercentOfDbTimeIsAccepted()
    {
        var a = Awr(o => o.CpuShare = "149.0");
        Assert.DoesNotContain(a.DataQuality.Alerts, x => x.Contains("cannot be true", StringComparison.OrdinalIgnoreCase));
        Assert.True(a.Health.Reliable);
    }

    [Fact]
    public void DbCpuFarAbove100PercentIsStillImpossible()
    {
        var a = Awr(o => o.CpuShare = "160.0");
        Assert.Contains(a.DataQuality.Alerts, x => x.Contains("cannot be true", StringComparison.OrdinalIgnoreCase));
        Assert.False(a.Health.Reliable);
    }

    [Fact]
    public void ImpossibleValuesDoNotEscalateSeverity()
    {
        var ok = Awr();
        var bad = Awr(o => o.SqlShare = "1,419.0");
        var sqlOk = ok.Findings.Where(f => f.Category == "SQL").Min(f => f.Severity);
        var sqlBad = bad.Findings.Where(f => f.Category == "SQL" && f.Impact > 0).Select(f => f.Severity).DefaultIfEmpty(Severity.Info).Min();
        Assert.True(sqlBad >= sqlOk || sqlBad >= Severity.Medium, $"{sqlBad} vs {sqlOk}");
    }

    // ---------------------------------------------------------------------------------------- 6. data quality

    [Fact]
    public void BackgroundWaitsMakingWaitClassesExceed100PercentIsNotAnInconsistency()
    {
        var a = Awr(); // "Wait Classes by Total Wait Time" adds up to 112.8% because it includes background waits
        Assert.DoesNotContain(a.Findings, f => f.Title.StartsWith("Some numbers in this report do not agree", StringComparison.Ordinal));
        Assert.DoesNotContain(a.DataQuality.Notes, n => n.Contains("cross-check", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForegroundWaitClassesAbove100PercentStillFlagTheInconsistency()
    {
        var html = Synthetic.AwrHtml().Replace("<td align=\"right\" class=\"awrc\">32.0</td></tr>\n<tr><td align=\"left\" class=\"awrc\">Commit</td><td align=\"right\" class=\"awrc\">345,678</td><td align=\"right\" class=\"awrc\">0.0</td>",
            "<td align=\"right\" class=\"awrc\">92.0</td></tr>\n<tr><td align=\"left\" class=\"awrc\">Commit</td><td align=\"right\" class=\"awrc\">345,678</td><td align=\"right\" class=\"awrc\">0.0</td>");
        Assert.NotEqual(Synthetic.AwrHtml(), html);
        var a = Run(html, "x.html");
        Assert.Contains(a.Findings, f => f.Title.StartsWith("Some numbers in this report do not agree", StringComparison.Ordinal) && f.Evidence.Any(e => e.Label.Contains("wait-class", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void WindowUnderFiveMinutesCapsSeverityAndShowsTheHealthAsNotReliable()
    {
        var a = Awr(o => { o.ElapsedText = "3.00 (mins)"; o.DbTimeText = "6.00 (mins)"; o.EndTime = "10-Mar-26 10:03:00"; });
        Assert.False(a.Health.Reliable);
        Assert.Equal("n/a", a.Health.Grade);
        Assert.Contains("not reliable", a.Health.ReliabilityNote);
        Assert.Equal("Poor", a.DataQuality.Level);
        Assert.All(a.Findings.Where(f => f.Category is not ("Report quality" or "Report scope" or "Configuration")), f => Assert.True(f.Severity >= Severity.Medium, $"{f.Severity} {f.Title}"));
        Assert.Contains(a.DataQuality.Alerts, x => x.Contains("3 minutes", StringComparison.OrdinalIgnoreCase) || x.Contains("3.0 minutes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ANormalWindowKeepsAReliableGrade()
    {
        var a = Awr();
        Assert.True(a.Health.Reliable);
        Assert.NotEqual("n/a", a.Health.Grade);
        Assert.Empty(a.DataQuality.Alerts);
    }

    // ---------------------------------------------------------------------------------------- 8. truncated and garbled reports

    [Fact]
    public void ATruncatedReportIsPoorWithAWarningAndNoGrade()
    {
        var a = Awr(o => o.Truncate = true);
        Assert.Equal("Poor", a.DataQuality.Level);
        Assert.Contains(a.DataQuality.Alerts, x => x.Contains("End of Report", StringComparison.OrdinalIgnoreCase));
        Assert.False(a.Health.Reliable);
        Assert.Equal("n/a", a.Health.Grade);
    }

    [Fact]
    public void AMissingEndMarkerIsDetectedInEveryReportKind()
    {
        foreach (var (name, content) in new[] { ("a.html", Synthetic.AwrHtml()), ("ash.html", TestData("ash-19c-hot-event.html")), ("ash.txt", TestData("ash-19c-hot-event.txt")), ("awr.txt", TestData("awr-19c-layout.txt")) })
        {
            var cut = Regex.Replace(content, "End of Report", "", RegexOptions.IgnoreCase);
            var a = Run(cut, name);
            Assert.Equal("Poor", a.DataQuality.Level);
            Assert.False(a.Health.Reliable);
            Assert.Contains(a.DataQuality.Alerts, x => x.Contains("End of Report", StringComparison.OrdinalIgnoreCase));
            Assert.True(Run(content, name).Health.Reliable || name.StartsWith("ash", StringComparison.Ordinal), name + " complete report must stay reliable");
        }
    }

    [Theory]
    [InlineData("release", "12.3.0.1.0")]
    [InlineData("snaps", "reversed")]
    [InlineData("span", "mismatch")]
    [InlineData("year", "1970")]
    public void InconsistentHeadersGiveWarnings(string kind, string _)
    {
        var a = Awr(o =>
        {
            switch (kind)
            {
                case "release": o.Release = "12.3.0.1.0"; break;
                case "snaps": o.BeginSnap = "101"; o.EndSnap = "100"; break;
                case "span": o.EndTime = "10-Mar-26 10:30:00"; break;
                case "year": o.BeginTime = "10-Mar-70 10:00:00"; o.EndTime = "10-Mar-70 11:00:00"; break;
            }
        });
        Assert.NotEqual("Good", a.DataQuality.Level);
        Assert.Contains(a.Warnings, w => kind switch
        {
            "release" => w.Contains("does not exist", StringComparison.OrdinalIgnoreCase),
            "snaps" => w.Contains("snapshot", StringComparison.OrdinalIgnoreCase),
            "span" => w.Contains("span", StringComparison.OrdinalIgnoreCase),
            _ => w.Contains("plausible", StringComparison.OrdinalIgnoreCase)
        });
    }

    // ---------------------------------------------------------------------------------------- 7. licence caveats

    [Theory]
    [InlineData("SELECT * FROM dba_hist_sqlstat", true)]
    [InlineData("SELECT * FROM TABLE(DBMS_XPLAN.DISPLAY_AWR('abc'))", true)]
    [InlineData("SELECT * FROM v$active_session_history", true)]
    [InlineData("EXEC DBMS_WORKLOAD_REPOSITORY.CREATE_SNAPSHOT", true)]
    [InlineData("BEGIN DBMS_SQLTUNE.CREATE_TUNING_TASK(sql_id => 'x'); END;", true)]
    [InlineData("SELECT * FROM v$sql", false)]
    [InlineData("SELECT * FROM v$sysstat -- not dba_hist", false)]
    public void TheLicenceHelperKnowsWhichSqlNeedsAPack(string sql, bool needs)
    {
        Assert.Equal(needs, LicenceCaveats.Needs(sql, out _, out _));
        var s = LicenceCaveats.Apply(new SqlSnippet("t", sql, "Careful."));
        Assert.True(LicenceCaveats.IsCovered(s.Code, s.Caution));
        if (needs) Assert.Contains("Requires the Oracle", s.Caution);
        else Assert.Equal("Careful.", s.Caution);
        Assert.Equal(s, LicenceCaveats.Apply(s)); // idempotent
    }

    [Fact]
    public void TuningAdvisorSqlGetsTheTuningPackCaveat()
    {
        var s = LicenceCaveats.Apply(new SqlSnippet("t", "BEGIN DBMS_SQLTUNE.EXECUTE_TUNING_TASK('x'); END;", "Needs the Oracle Tuning Pack license; run off-peak."));
        Assert.Contains("Requires the Oracle Tuning Pack", s.Caution);
        Assert.DoesNotContain("Needs the Oracle", s.Caution);
        Assert.Contains("run off-peak", s.Caution);
    }

    /// <summary>Every SQL snippet emitted for every fixture that touches AWR/ASH views or the advisors must carry the uniform caveat.</summary>
    [Fact]
    public void NoEmittedSnippetNeedingAPackLacksTheCaveat()
    {
        var checkedSnippets = 0;
        var missing = new List<string>();
        foreach (var (name, content) in Fixtures())
        {
            AnalysisResult a;
            try { a = Run(content, name); } catch (ReportFormatException) { continue; }
            foreach (var f in a.Findings)
            {
                foreach (var s in f.Sql)
                {
                    checkedSnippets++;
                    if (!LicenceCaveats.IsCovered(s.Code, s.Caution)) missing.Add($"{name}: {f.Title} / {s.Title}");
                }
                foreach (var r in f.Recommendations.Where(r => Regex.IsMatch(r, "SQL Tuning Advisor|DISPLAY_AWR|DBA_HIST_", RegexOptions.IgnoreCase)))
                    if (!Regex.IsMatch(r, "Diagnostics Pack|Tuning Pack")) missing.Add($"{name}: recommendation '{r}'");
            }
        }
        Assert.True(checkedSnippets > 20, $"only {checkedSnippets} snippets were scanned");
        Assert.Empty(missing);
    }

    private static IEnumerable<(string Name, string Content)> Fixtures()
    {
        yield return ("synthetic-awr.html", Synthetic.AwrHtml());
        foreach (var n in new[] { "ash-19c-hot-event.html", "ash-19c-hot-event.txt", "awr-19c-layout.txt", "small-awr.txt", "awr-11g-synthetic.html", "awr-19c-synthetic.html", "statspack-10g-layout.txt", "statspack-19c-layout.txt" })
            yield return (n, TestData(n));
        foreach (var dir in new[] { "EXAMPLES", "EXAMPLES-local" })
        {
            var root = FindUp(dir);
            if (root == null) continue; // third-party reports are never committed: this part skips when they are absent
            foreach (var f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).Where(p => p.EndsWith(".html", StringComparison.Ordinal) || p.EndsWith(".txt", StringComparison.Ordinal)).Take(40))
                yield return (Path.GetRelativePath(root, f), File.ReadAllText(f));
        }
    }

    internal static string? FindUp(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, name)) && File.Exists(Path.Combine(d.FullName, "Apodofos.slnx"))) return Path.Combine(d.FullName, name);
        return null;
    }

    // ---------------------------------------------------------------------------------------- 2/3. root causes and the Fix first plan

    private static Finding F(string id, Severity sev, string title, double impact, string cat = "Wait events", params string[] recs) =>
        new() { Id = id, Severity = sev, Category = cat, Title = title, Plain = title, Impact = impact, Recommendations = [.. recs] };

    private static AnalysisContext Ctx() => new(AwrReportLoader.LoadContent(Synthetic.AwrHtml(), "s.html"));

    [Fact]
    public void ACpuBoundUpdateIsNotGroupedUnderATinyRowLockWait()
    {
        var lockWait = F("F001", Severity.Low, "Waiting for another user's locked row: 'enq: TX - row lock contention' took 1.9% of database time", 1.9, "Wait events", "Find and fix the blocking session.");
        var sql = F("F002", Severity.High, "SQL wxyz9876stuv1 used 40% of all database time (module SQL*Plus)", 40, "SQL", "Reduce logical reads: check the plan.");
        sql.Plain = "The statement is mostly on CPU.";
        var ctx = Ctx();
        ctx.SqlTexts["wxyz9876stuv1"] = "UPDATE accounts SET balance = balance - :1 WHERE id = :2";
        var top = new List<SqlSummary> { new() { SqlId = "wxyz9876stuv1", PctCpu = 97, PctIo = 1, PctDbTime = 40, Text = ctx.SqlTexts["wxyz9876stuv1"] } };
        var groups = RootCauseAnalyzer.Link(ctx, [lockWait, sql], top);
        Assert.Empty(groups);
        Assert.Equal("", sql.RootCauseGroup);
    }

    [Fact]
    public void AWaitBoundUpdateJoinsALargeRowLockWaitAndTheGroupImpactIsTheEvents()
    {
        var lockWait = F("F001", Severity.High, "Waiting for another user's locked row: 'enq: TX - row lock contention' took 18.5% of database time", 18.5, "Wait events", "Find and fix the blocking session.");
        var sql = F("F002", Severity.High, "SQL wxyz9876stuv1 used 60% of all database time (module SQL*Plus)", 60, "SQL", "Shorten the transaction.");
        var ctx = Ctx();
        ctx.SqlTexts["wxyz9876stuv1"] = "UPDATE accounts SET balance = balance - :1 WHERE id = :2";
        var top = new List<SqlSummary> { new() { SqlId = "wxyz9876stuv1", PctCpu = 5, PctIo = 2, PctDbTime = 60, Text = ctx.SqlTexts["wxyz9876stuv1"] } };
        var groups = RootCauseAnalyzer.Link(ctx, [lockWait, sql], top);
        var g = Assert.Single(groups);
        Assert.Equal("Row lock contention", g.Title);
        Assert.Equal(18.5, g.ImpactPct); // the event's share, not the 60% of the statement
        Assert.Equal("F001", g.LeadFindingId);
        var plan = ActionPlanBuilder.Build([lockWait, sql], groups);
        var item = Assert.Single(plan);
        Assert.StartsWith("Row lock contention: Find and fix the blocking session", item.Action);
        Assert.Equal(18.5, item.ExpectedImpactPct);
    }

    [Fact]
    public void FixFirstLabelsAlwaysMatchTheirExplanation()
    {
        var a = Awr();
        Assert.NotEmpty(a.ActionPlan);
        Assert.All(a.ActionPlan, item =>
        {
            var members = item.FindingIds.Select(id => a.Findings.First(f => f.Id == id)).ToList();
            var group = a.RootCauses.FirstOrDefault(g => g.FindingIds.Intersect(item.FindingIds).Any());
            string label;
            if (group != null) label = group.Title;
            else
            {
                var t = members[0].Title.TrimEnd('.', ':', ' ');
                label = t.Length <= 110 ? t : t[..107].TrimEnd() + "...";
            }
            Assert.StartsWith(label + ": ", item.Action); // never an empty label, never another cause's label
            var rec = item.Action[(label.Length + 2)..];
            var owner = group != null ? members.First(m => m.Id == group.LeadFindingId) : members[0];
            Assert.True(owner.Recommendations.Contains(rec) || owner.Title == rec, $"'{rec}' is not a recommendation of {owner.Title}");
        });
    }

    // ---------------------------------------------------------------------------------------- 4. hot index SQL

    private const string SegmentsTable = "</p><p>Segments by Buffer Busy Waits</p><p>\n<table border=\"1\"><tbody><tr><th>Owner</th><th>Tablespace Name</th><th>Object Name</th><th>Subobject Name</th><th>Obj. Type</th><th>Buffer Busy Waits</th><th>% of Capture</th></tr>\n"
        + "<tr><td>{OWNER}</td><td>TS</td><td>{OBJ}</td><td></td><td>INDEX</td><td>12,345</td><td>88.0</td></tr></tbody></table>\n";

    private static AnalysisResult BufferBusyOn(string owner, string obj)
    {
        var html = Synthetic.AwrHtml().Replace("enq: TX - row lock contention", "buffer busy waits")
            .Replace("<p>End of Report</p>", SegmentsTable.Replace("{OWNER}", owner).Replace("{OBJ}", obj) + "<p>End of Report</p>");
        return Run(html, "bb.html");
    }

    private static string AllSql(AnalysisResult a) => string.Join("\n", a.Findings.SelectMany(f => f.Sql).Select(s => s.Code));
    private static string AllAdvice(AnalysisResult a) => string.Join("\n", a.Findings.SelectMany(f => f.Recommendations.Concat(f.Sql.Select(s => s.Caution ?? ""))));

    [Theory]
    [InlineData("APPOWNER", "SYS_IOT_TOP_81234")]
    [InlineData("ONT", "OE_ORDER_LINES_ALL_N1")]
    [InlineData("APPS", "MTL_MATERIAL_TRANSACTIONS_N2")]
    public void NoReverseKeyOrHashDdlForIotKeysOrEbsSeededIndexes(string owner, string index)
    {
        var a = BufferBusyOn(owner, index);
        var sql = AllSql(a);
        Assert.Contains($"{owner}.{index}", sql + AllAdvice(a)); // the finding is about this object
        Assert.DoesNotContain("REBUILD REVERSE", sql.Replace("REBUILD REVERSE ONLINE;", "REBUILD REVERSE ONLINE;"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"ALTER INDEX {owner}.{index} REBUILD REVERSE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PARTITION BY HASH (&", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(owner is "APPOWNER" ? "(?i)index-organized|IOT" : "(?i)service request|Oracle Support", AllAdvice(a));
    }

    [Fact]
    public void ACustomIndexGetsOptionsWithConsistentPlaceholdersNotAMixOfRealNamesAndPlaceholders()
    {
        var a = BufferBusyOn("APPOWNER", "ORDERS_PK");
        var option = a.Findings.SelectMany(f => f.Sql).First(s => s.Title.StartsWith("Options to spread inserts", StringComparison.Ordinal));
        Assert.Contains("&index_owner..&index_name", option.Code);
        Assert.DoesNotContain("APPOWNER.ORDERS_PK_HP", option.Code); // no real name next to &placeholders
        Assert.DoesNotContain("APPOWNER", option.Code);
        Assert.Contains("REBUILD REVERSE", option.Code);
        Assert.Contains("columns", string.Join(" ", a.Findings.SelectMany(f => f.Sql).Select(s => s.Title)), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------- 5. ASH calibration

    private static string AshHtml() => TestData("ash-19c-hot-event.html");

    [Fact]
    public void AshSeverityIsCappedWhenAverageActiveSessionsAreBelowTheCpuCount()
    {
        var baseline = Run(AshHtml(), "ash.html");
        Assert.Contains(baseline.Findings, f => f.Severity == Severity.Critical); // 5.2 sessions on 4 CPUs: genuinely loaded
        var wide = AshHtml().Replace("<td align=\"right\" class='awrnc'>4</td><td align=\"right\" class='awrnc'>4,096M", "<td align=\"right\" class='awrnc'>32</td><td align=\"right\" class='awrnc'>4,096M");
        Assert.NotEqual(AshHtml(), wide);
        var a = Run(wide, "ash.html");
        Assert.DoesNotContain(a.Findings, f => f.Severity == Severity.Critical && f.Category is "Wait events" or "SQL" or "Hot objects");
        Assert.All(a.Findings.Where(f => f.Category is "Wait events" or "SQL"), f => Assert.True(f.Severity >= Severity.Medium, $"{f.Severity} {f.Title}"));
    }

    [Fact]
    public void AshWindowUnderFiveMinutesCapsSeverityAndMarksTheHealthUnreliable()
    {
        var a = Run(AshHtml().Replace("30.0 (mins)", "3.0 (mins)").Replace("09:30:00", "09:03:00"), "ash.html");
        Assert.False(a.Health.Reliable);
        Assert.Equal("n/a", a.Health.Grade);
        Assert.All(a.Findings.Where(f => f.Category is not ("Report quality" or "Report scope")), f => Assert.True(f.Severity >= Severity.Medium, $"{f.Severity} {f.Title}"));
    }

    [Fact]
    public void PxNsqOnANearIdleSystemIsInfoWithCorrectAdvice()
    {
        var html = AshHtml().Replace("enq: TX - row lock contention</td><td class='awrc'>Application", "PX Nsq: PQ load info query</td><td class='awrc'>Other")
            .Replace("<td align=\"right\" class='awrc'>5.20</td>", "<td align=\"right\" class='awrc'>0.20</td>").Replace("<td align=\"right\" class='awrc'>1.30</td>", "<td align=\"right\" class='awrc'>0.05</td>");
        var a = Run(html, "ash.html");
        var px = Assert.Single(a.Findings, f => f.Category == "Parallel execution");
        Assert.Equal(Severity.Info, px.Severity);
        var advice = string.Join(" ", px.Recommendations);
        Assert.DoesNotContain("which object", advice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a wait on a database object", advice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("monitoring", advice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActiveSessionsAreNotDescribedAsCpuCapacity()
    {
        var ash = Run(AshHtml(), "ash.html");
        var awr = Awr();
        foreach (var a in new[] { ash, awr })
        {
            var text = string.Join(" ", a.Health.Paragraphs);
            Assert.DoesNotContain("of CPU capacity", text);
            Assert.Contains("of the CPU count", text);
            Assert.Contains("not CPU use", text);
        }
        Assert.Contains("CPU actually used was about 30%", string.Join(" ", awr.Health.Paragraphs)); // Host CPU: 70.1% idle
    }

    // ---------------------------------------------------------------------------------------- 9. minor

    [Fact]
    public void LogFileSyncNoteNeverSaysHealthyForAValueAboveTheLimit()
    {
        var a = Awr();
        var ev = a.Findings.SelectMany(f => f.Evidence).First(e => e.Label == "Average wait" && e.Note is not null && e.Note.Contains("5 ms"));
        Assert.DoesNotContain("healthy", ev.Note!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(a.Findings.SelectMany(f => f.Evidence), e => e.Note is { } n && n.StartsWith("healthy: under", StringComparison.Ordinal));
    }

    [Fact]
    public void NoStaleClaimsAboutTheCurrentRelease()
    {
        var text = string.Join(" ", Fixtures().Take(8).SelectMany(x =>
        {
            try { return Run(x.Content, x.Name).Findings.SelectMany(f => new[] { f.Plain, f.Technical, f.WhyItMatters }.Concat(f.Recommendations)); }
            catch (ReportFormatException) { return []; }
        }));
        Assert.DoesNotContain("19c is the current", text);
        Assert.DoesNotContain("23ai", text);
        var old = Awr(o => o.Release = "11.2.0.4.0");
        Assert.DoesNotContain("current long-term", string.Join(" ", old.Findings.Select(f => f.Plain + f.Technical)));
    }

    [Fact]
    public void TheOlderThan19cNoteIsNotAnIssueInCountsOrHealth()
    {
        var old = Awr(o => o.Release = "11.2.0.4.0");
        var note = Assert.Single(old.Findings, f => f.Title.Contains("older than 19c"));
        Assert.Equal(Severity.Info, note.Severity);
        var current = Awr();
        Assert.Equal(current.Health.Score, old.Health.Score);
        Assert.Equal(current.Health.Counts["Medium"], old.Health.Counts["Medium"]);
        Assert.Equal(current.Health.Counts["Low"], old.Health.Counts["Low"]);
    }

    [Fact]
    public void AnEmptyInstanceNameIsNotPrintedAsEmptyParentheses()
    {
        var a = Awr(o => o.Instance = "");
        Assert.Equal("", a.Meta.Instance);
        Assert.Equal("SYNTH", a.Meta.DbName);
    }

    [Fact]
    public void ABlockerIsNotStatedAsIdleInItsTransactionWithoutTheEvidenceWording()
    {
        var a = Run(AshHtml(), "ash.html");
        var text = string.Join(" ", a.Findings.SelectMany(f => new[] { f.Plain, f.Technical }));
        Assert.DoesNotContain("so it is idle in its transaction", text);
        Assert.DoesNotContain("One connection held a lock while it was doing nothing itself", text);
    }
}
