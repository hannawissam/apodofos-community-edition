// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using QuietLoad.Core.Model;
using static QuietLoad.Core.Analysis.AnalysisContext;
using static QuietLoad.Core.Analysis.Rules.AshHelpers;
using static QuietLoad.Core.Analysis.Rules.RuleHelpers;

namespace QuietLoad.Core.Analysis;

/// <summary>Health summary, KPI tiles and SQL summaries for ASH reports (the AWR versions read AWR-only tables).</summary>
internal static class AshSummary
{
    public static HealthSummary BuildHealth(AnalysisContext ctx, List<Finding> findings)
    {
        var counts = Enum.GetValues<Severity>().ToDictionary(s => s.ToString(), s => findings.Count(f => f.Severity == s));
        var score = Math.Clamp(100 - findings.Sum(f => f.Severity switch { Severity.Critical => 25, Severity.High => 10, Severity.Medium => 4, Severity.Low => 1, _ => 0 }), 0, 100);
        var grade = score switch { >= 90 => "A", >= 75 => "B", >= 60 => "C", >= 40 => "D", _ => "F" };
        var actionable = findings.Where(f => f.Severity <= Severity.Low).ToList();
        var headline = score switch
        {
            >= 90 => "Nothing alarming in the sampled activity of this period.",
            >= 75 => "The sampled activity shows a few things worth improving.",
            >= 60 => "The sampled activity shows several performance problems worth fixing.",
            >= 40 => "The sampled activity shows significant performance problems.",
            _ => "The sampled activity shows serious performance problems that need urgent attention."
        };
        var i = ctx.Info;
        var paras = new List<string>
        {
            $"This ASH report samples what active sessions were doing over {Dur(ctx.ElapsedSec)} on database {i.DbName} (Oracle {i.Release}) on host {i.HostName}, from {i.BeginSnapTime} to {i.EndSnapTime}" +
            (i.AshSampleCount is { } sc ? $" ({N(sc)} samples)." : ".")
        };
        if (ctx.Aas is not null)
            paras.Add(ctx.ActiveSessionsSentence("sessions"));
        var mix = findings.FirstOrDefault(f => f.Title.StartsWith("Active session time was mostly") || f.Title.Contains("shared the time"));
        if (mix != null) paras.Add(mix.Plain);
        paras.Add(actionable.Count == 0
            ? "No issues requiring action were found. ASH covers only the sampled window, so problems outside it are not visible here."
            : $"We found {actionable.Count} issue(s) to address: {counts["Critical"]} critical, {counts["High"]} high, {counts["Medium"]} medium and {counts["Low"]} low priority.");
        if (actionable.FirstOrDefault() is { } top) paras.Add($"The biggest opportunity: {top.Title}.");
        return new HealthSummary { Score = score, Grade = grade, Headline = headline, Paragraphs = paras, Counts = counts };
    }

    public static List<Kpi> BuildKpis(AnalysisContext ctx)
    {
        var k = new List<Kpi>();
        var ratio = ctx.Aas is { } a && ctx.Cpus is > 0 ? a / ctx.Cpus.Value : (double?)null;
        k.Add(new("aas", "Avg active sessions", N(ctx.Aas, 2), null, ratio >= 1 ? "bad" : ratio >= 0.7 ? "warn" : "good", ctx.Cpus is > 0 ? $"vs {ctx.Cpus} CPUs" : "sessions active on average"));
        k.Add(new("aas-cpu", "Active sessions per CPU", N(ctx.Info.AshAasPerCpu ?? ratio, 2), null, ratio >= 1 ? "bad" : ratio >= 0.7 ? "warn" : "good", "1.0 means every CPU busy"));
        k.Add(new("samples", "ASH samples", Big(ctx.Info.AshSampleCount), null, "neutral", $"over {Dur(ctx.ElapsedSec)}"));
        var users = T(ctx, "Top User Events");
        double? cpu = users?.DataRows.Where(r => IsCpu(r.Str("Event"))).Sum(Pct);
        double? total = users?.DataRows.Sum(Pct);
        k.Add(new("cpu-share", "On CPU", P(cpu), null, "neutral", "of sampled user activity"));
        k.Add(new("wait-share", "Waiting", total is > 0 ? P(Math.Max(0, total.Value - (cpu ?? 0))) : "n/a", null, "neutral", "non-CPU wait events"));
        var topWait = users?.DataRows.Where(r => !IsCpu(r.Str("Event"))).OrderByDescending(Pct).Cast<AwrRow?>().FirstOrDefault();
        k.Add(new("top-event", "Top wait", topWait is { } w ? w.Str("Event") : "none", null, "neutral", topWait is { } w2 ? P(Pct(w2)) + " of activity" : "no wait event stands out"));
        var sql = T(ctx, "Top SQL with Top Events", "Top SQL Statements");
        var top = sql == null ? (AwrRow?)null : Distinct(sql, "SQL ID").OrderByDescending(r => r.Val("% Activity") ?? 0).Cast<AwrRow?>().FirstOrDefault();
        k.Add(new("top-sql", "Top SQL", top is { } t ? t.Str("SQL ID") : "n/a", null, "neutral", top is { } t2 ? P(t2.Val("% Activity")) + " of activity" : "no SQL section"));
        var slots = Rules.AshLoadRules.Slots(ctx).Where(s => s.Minutes >= 0.2).ToList();
        if (slots.Count > 0)
        {
            var w8 = ctx.Info.AshFromHistory ? 10.0 : 1.0;
            var peak = slots.Max(s => s.Count * w8 / (s.Minutes * 60));
            k.Add(new("peak-aas", "Peak active sessions", N(peak, 1), null, ctx.Cpus is > 0 && peak >= ctx.Cpus ? "bad" : "neutral", "busiest time slot"));
        }
        return k;
    }

    public static List<SqlSummary> BuildTopSql(AnalysisContext ctx)
    {
        var list = new List<SqlSummary>();
        var t = T(ctx, "Top SQL with Top Events", "Top SQL Statements");
        if (t == null) return list;
        foreach (var r in Distinct(t, "SQL ID").OrderByDescending(r => r.Val("% Activity") ?? 0).Take(15))
        {
            var id = r.Str("SQL ID");
            var pct = r.Val("% Activity");
            var cpuPct = t.DataRows.Where(x => x.Str("SQL ID") == id && IsCpu(x.Str("Event"))).Sum(Pct);
            list.Add(new SqlSummary
            {
                SqlId = id,
                PctDbTime = pct,
                ElapsedSec = pct / 100 * ctx.DbTimeSec,
                CpuSec = cpuPct / 100 * ctx.DbTimeSec,
                PctCpu = cpuPct,
                Executions = r.Val("Sampled # of Executions"),
                Text = SqlText(ctx, id, r.Str("SQL Text")),
                AppearsIn = ["Top SQL with Top Events"]
            });
        }
        return list;
    }
}
