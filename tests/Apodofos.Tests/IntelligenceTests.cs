// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text.Json;
using Apodofos.Core.Analysis;
using Apodofos.Core.Export;
using Apodofos.Core.Parsing;

namespace Apodofos.Tests;

/// <summary>Confidence, data quality, cross-checks, root-cause groups and the "Fix first" plan.</summary>
public class IntelligenceTests
{
    private static AnalysisResult Fresh(Action<Apodofos.Core.Model.AwrReport>? mutate = null)
    {
        var report = AwrReportLoader.LoadFile(Samples.Syn19); // not the cached copy: tests mutate it
        mutate?.Invoke(report);
        return AnalysisEngine.Analyze(report);
    }

    [Fact]
    public void EveryFindingHasConfidenceAndReducedConfidenceExplainsWhy()
    {
        var a = Fresh();
        Assert.All(a.Findings, f =>
        {
            Assert.True(Enum.IsDefined(f.Confidence));
            if (f.Confidence != Confidence.High) Assert.False(string.IsNullOrWhiteSpace(f.DataQuality), f.Title);
        });
        Assert.NotEmpty(a.DataQuality.Notes);
        Assert.Contains(a.DataQuality.Level, new[] { "Good", "Fair", "Poor" });
    }

    [Fact]
    public void ShortWindowLowersConfidenceOfMeasuredFindings()
    {
        var a = Fresh(r => r.Info.ElapsedMinutes = 3);
        Assert.Equal("Poor", a.DataQuality.Level);
        Assert.Contains(a.DataQuality.Notes, n => n.Contains("short window", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(a.Findings, f => f.Category == "Wait events" && f.Confidence == Confidence.Low);
        // configuration findings do not depend on the window length
        Assert.All(a.Findings.Where(f => f.Category == "Configuration"), f => Assert.NotEqual(Confidence.Low, f.Confidence));
    }

    [Fact]
    public void InconsistentTimeModelBecomesALowConfidenceFindingNotACrash()
    {
        var a = Fresh(r =>
        {
            var tm = r.Table("Time Model Statistics")!;
            var row = tm.Rows.First(x => x[0].Trim().Equals("DB time", StringComparison.OrdinalIgnoreCase));
            row[tm.Col("Time (s)")] = "10";
        });
        var f = Assert.Single(a.Findings, x => x.Title.StartsWith("Some numbers in this report do not agree"));
        Assert.Equal(Confidence.Low, f.Confidence);
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Contains(a.DataQuality.Notes, n => n.Contains("cross-check", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(a.Findings, x => x.Impact > 0 && x.Confidence != Confidence.High);
    }

    [Fact]
    public void ImpossibleCpuAndWaitClassNumbersAreFlagged()
    {
        var a = Fresh(r =>
        {
            // only the foreground table must add up to 100%: the "Wait Classes by Total Wait Time" table includes background waits
            var fg = r.Table("Foreground Wait Class") ?? r.Table("Wait Classes by Total Wait Time")!;
            foreach (var row in fg.Rows) row[fg.Col("% DB time", "%DB time")] = "60";
        });
        Assert.Contains(a.Findings, x => x.Title.StartsWith("Some numbers in this report do not agree") && x.Evidence.Any(e => e.Label.Contains("wait-class", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void MissingSectionsNeverThrowAndAreReported()
    {
        var a = Fresh(r => r.Tables.RemoveAll(t => t.TitleStartsWith("Load Profile") || t.TitleStartsWith("Time Model") || t.TitleStartsWith("SQL ordered by Elapsed")));
        Assert.Contains(a.DataQuality.Notes, n => n.Contains("missing", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(a.Warnings, w => w.Contains("could not complete"));
    }

    [Fact]
    public void RootCauseGroupsAreConsistent()
    {
        var a = Fresh();
        var ids = a.Findings.Select(f => f.Id).ToHashSet();
        foreach (var g in a.RootCauses)
        {
            Assert.True(g.FindingIds.Count >= 2, g.Title);
            Assert.All(g.FindingIds, id => Assert.Contains(id, ids));
            foreach (var id in g.FindingIds)
            {
                var f = a.Findings.First(x => x.Id == id);
                Assert.Equal(g.Title, f.RootCauseGroup);
                Assert.DoesNotContain(id, f.RelatedIds);
                Assert.Equal(g.FindingIds.Count - 1, f.RelatedIds.Count);
            }
        }
        Assert.All(a.Findings.Where(f => f.RootCauseGroup == ""), f => Assert.Empty(f.RelatedIds));
    }

    [Fact]
    public void RowLockSymptomIsLinkedToTheDmlThatCausesIt()
    {
        // synthetic: the grouping must connect a lock wait finding with a DML statement finding
        var report = AwrReportLoader.LoadFile(Samples.Syn19);
        var ctx = new AnalysisContext(report);
        var wait = new Finding { Id = "F001", Severity = Severity.Medium, Category = "Wait events", Title = "Waiting for another user's locked row: 'enq: TX - row lock contention' took 12% of database time", Impact = 12 };
        var sql = new Finding { Id = "F002", Severity = Severity.Medium, Category = "SQL", Title = "SQL abcdefghijklm used 15% of all database time (module X)", Impact = 15 };
        ctx.SqlTexts["abcdefghijklm"] = "UPDATE orders SET status = 'X' WHERE id = :1";
        var groups = RootCauseAnalyzer.Link(ctx, [wait, sql], []);
        var g = Assert.Single(groups);
        Assert.Equal("Row lock contention", g.Title);
        Assert.Equal(["F002"], wait.RelatedIds);
        Assert.Equal(["F001"], sql.RelatedIds);
    }

    [Fact]
    public void ActionPlanIsRankedBoundedAndTraceable()
    {
        var a = Fresh();
        Assert.InRange(a.ActionPlan.Count, 1, 5);
        var ids = a.Findings.Select(f => f.Id).ToHashSet();
        for (var i = 0; i < a.ActionPlan.Count; i++)
        {
            var x = a.ActionPlan[i];
            Assert.Equal(i + 1, x.Rank);
            Assert.False(string.IsNullOrWhiteSpace(x.Action));
            Assert.Contains(x.Effort, new[] { "Low", "Medium", "High" });
            Assert.Contains(x.Risk, new[] { "Low", "Medium", "High" });
            Assert.InRange(x.ExpectedImpactPct, 0, 100);
            Assert.NotEmpty(x.FindingIds);
            Assert.All(x.FindingIds, id => Assert.Contains(id, ids));
        }
        Assert.Equal(a.ActionPlan.Count, a.ActionPlan.Select(x => x.Action).Distinct().Count());
    }

    [Fact]
    public void NewFieldsSerializeToJsonWithoutNaN()
    {
        var json = DashboardBuilder.ToJson(Fresh(), null, DashboardMode.Export);
        using var doc = JsonDocument.Parse(json);
        var an = doc.RootElement.GetProperty("analysis");
        Assert.True(an.TryGetProperty("dataQuality", out _));
        Assert.True(an.TryGetProperty("actionPlan", out _));
        Assert.True(an.TryGetProperty("rootCauses", out _));
        Assert.True(an.GetProperty("findings")[0].TryGetProperty("confidence", out _));
    }
}
