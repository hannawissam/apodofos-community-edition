// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Globalization;
using System.Text.RegularExpressions;
using Apodofos.Core.Model;
using static Apodofos.Core.Analysis.AnalysisContext;
using static Apodofos.Core.Analysis.Rules.AshHelpers;
using static Apodofos.Core.Analysis.Rules.RuleHelpers;
using D = Apodofos.Core.Knowledge.OracleDocs;

namespace Apodofos.Core.Analysis.Rules;

/// <summary>ASH: how busy the database was (average active sessions vs CPUs), CPU against waiting, the quality of the sample, and the time profile.</summary>
public sealed class AshLoadRules : IRule
{
    public string Name => "ASH load and activity profile";
    public ReportKind[] Kinds => [ReportKind.Ash];

    public void Evaluate(AnalysisContext ctx)
    {
        SampleQuality(ctx);
        ActiveSessionsVsCpu(ctx);
        CpuVersusWaiting(ctx);
        ActivityOverTime(ctx);
    }

    /// <summary>Few samples or a short window make every percentage in an ASH report unreliable.</summary>
    private static void SampleQuality(AnalysisContext ctx)
    {
        var samples = ctx.Info.AshSampleCount;
        var mins = ctx.Info.ElapsedMinutes;
        var parts = new List<string>();
        if (samples is < 100) parts.Add($"only {N(samples)} active-session samples were captured");
        if (mins is < 5) parts.Add($"the window is only {N(mins, 1)} minutes long");
        if (parts.Count > 0)
            ctx.Add(new Finding
            {
                Severity = Severity.Info,
                Category = "Report quality",
                Title = "Small sample: treat the percentages with care",
                Plain = $"This ASH report is based on a small amount of data ({string.Join(" and ", parts)}). A handful of samples can make one event or statement look dominant by chance.",
                WhyItMatters = "Conclusions drawn from a few samples can send tuning effort in the wrong direction. Confirm with a longer window or an AWR report before making changes.",
                Technical = $"Sample count {N(samples)}, elapsed {N(mins, 1)} min, average active sessions {N(ctx.Aas, 2)}. ASH samples every active session once per second in memory (V$ACTIVE_SESSION_HISTORY), so a 1-minute window holds at most 60 samples per active session. Statements or events with less than about 10 samples are anecdotes.",
                Evidence = [new("Samples", N(samples)), new("Window", $"{N(mins, 1)} minutes")],
                Recommendations = ["Re-run the ASH report over the whole period of the problem (start before it, end after it).", "For problems that are not happening right now, use an AWR report or the ASH history view (DBA_HIST_ACTIVE_SESS_HISTORY)."],
                Docs = [D.Transient, D.VActiveSessionHistory],
                SourceViews = ["V$ACTIVE_SESSION_HISTORY"],
                TableRefs = TableIds(ctx, "ASH Sample Window")
            });
        if (ctx.Info.AshFromHistory)
            ctx.Add(new Finding
            {
                Severity = Severity.Info,
                Category = "Report quality",
                Title = "Data comes from the ASH history (one sample in ten)",
                Plain = "This report was built from the data saved in the AWR repository, which keeps only every tenth sample taken in memory. Short spikes (under about 10 seconds) can be missing.",
                WhyItMatters = "Very brief problems may not show up at all, and every count is an estimate of seconds rather than an exact value.",
                Technical = $"Data source {ctx.Info.AshDataSource}: DBA_HIST_ACTIVE_SESS_HISTORY holds 1 of every 10 in-memory samples, so each sample represents about 10 seconds of database time. Sample counts in this report are therefore multiples of ~10 s.",
                Recommendations = ["For events of the last hour or so, generate the report from V$ACTIVE_SESSION_HISTORY (ashrpt.sql on a recent window), which keeps every sample."],
                Docs = [D.DbaHistActiveSessHistory],
                SourceViews = ["DBA_HIST_ACTIVE_SESS_HISTORY"]
            });
    }

    private static void ActiveSessionsVsCpu(AnalysisContext ctx)
    {
        if (ctx.Aas is not { } aas || ctx.Cpus is not > 0) return;
        var cpus = ctx.Cpus.Value;
        var ratio = aas / cpus;
        var users = T(ctx, "Top User Events");
        var cpuAas = users?.DataRows.Where(r => IsCpu(r.Str("Event"))).Sum(r => r.Val("Avg Active Sessions") ?? 0) ?? 0;
        var bg = T(ctx, "Top Background Events")?.DataRows.Where(r => IsCpu(r.Str("Event"))).Sum(r => r.Val("Avg Active Sessions") ?? 0) ?? 0;
        var cpuPressure = (cpuAas + bg) / cpus;
        var ev = new List<Evidence>
        {
            new("Average active sessions", N(aas, 2), $"{N(ratio * 100, 0)}% of the {cpus} CPUs if every session were on CPU"),
            new("Sessions on CPU or queued for CPU", N(cpuAas + bg, 2), $"{N(cpuPressure * 100, 0)}% of the CPU count (includes sessions queued for CPU)")
        };
        if (cpuPressure >= 0.7 || ratio >= 1)
        {
            var sev = cpuPressure >= 1.0 ? Severity.High : cpuPressure >= 0.7 ? Severity.Medium : Severity.Low;
            if (cpuPressure >= 2) sev = Severity.Critical;
            sev = SeverityMath.ForLoad(ctx, sev);
            ctx.Add(new Finding
            {
                Severity = sev,
                Category = "Workload profile",
                Impact = Math.Min(100, Math.Max(cpuPressure, ratio) * 50),
                Title = cpuPressure >= 1
                    ? $"Sessions wanting CPU outnumbered the {cpus} CPUs ({N(cpuAas + bg, 1)} on or waiting for CPU)"
                    : ratio >= 1 ? $"More sessions were active ({N(aas, 1)}) than there are CPUs ({cpus}), but most were waiting rather than computing"
                    : $"Active sessions ({N(aas, 1)}) are close to the number of CPUs ({cpus})",
                Plain = cpuPressure >= 1
                    ? $"On average {N(cpuAas + bg, 1)} sessions wanted the processor at the same time, but the server only has {cpus} CPUs. Sessions had to queue for CPU, so everything ran slower than necessary."
                    : ratio >= 1 ? $"On average {N(aas, 1)} sessions were active at the same time on a server with {cpus} CPUs, but only about {N(cpuAas + bg, 1)} of them were using or waiting for the processor; the rest were waiting for something else (locks, disk, internal contention). The processor is not the main limit, yet any growth in CPU work would soon queue."
                    : $"On average {N(aas, 1)} sessions were active at the same time on a server with {cpus} CPUs. There is little spare capacity: a small increase in work will start to queue.",
                WhyItMatters = "When more sessions want CPU than there are CPUs, each one waits its turn and response times grow even though no single query changed. It also hides real waits behind run-queue time.",
                Technical = $"Average Active Sessions {N(aas, 2)} (Avg. Active Session per CPU {N(ctx.Info.AshAasPerCpu ?? ratio, 2)}). ASH shows 'CPU + Wait for CPU' because it cannot tell running from runnable: the value is an upper bound of CPU use. Check host run queue / load average and the Resource Manager. Value >1 per CPU suggests CPU saturation; 0.7-1 leaves no headroom.",
                Evidence = ev,
                Recommendations =
                [
                    "Find the top SQL on CPU (see the SQL findings below) and cut its logical reads: that reduces CPU without buying hardware.",
                    "Check how many CPUs the database may use (CPU_COUNT, Resource Manager plan, virtualisation limits) and whether another process on the host competes for CPU.",
                    "Limit concurrency if it comes from a connection pool that is too large: more sessions than CPUs only lengthens the queue."
                ],
                Sql = [new("Sessions on CPU per minute in the window (ASH)",
                    $"SELECT TRUNC(sample_time, 'MI') AS minute, COUNT(*) AS cpu_samples\nFROM   {View(ctx)}\nWHERE  {Window(ctx)}\nAND    session_state = 'ON CPU'\nGROUP  BY TRUNC(sample_time, 'MI')\nORDER  BY minute;", HistoryCaution),
                    new("CPU configuration and Resource Manager plan",
                    "SELECT name, value FROM v$parameter WHERE name IN ('cpu_count', 'resource_manager_plan', 'parallel_max_servers');")],
                Docs = [D.ResourceManager, D.OsResources, D.Transient],
                SourceViews = ["V$ACTIVE_SESSION_HISTORY"],
                ChartRefs = ["ash-cpu-vs-wait"],
                TableRefs = TableIds(ctx, "Top User Events")
            });
        }
        else
            ctx.Add(new Finding
            {
                Severity = Severity.Ok,
                Category = "Workload profile",
                Title = $"Active sessions ({N(aas, 1)}) are well below the CPU count ({cpus})",
                Plain = $"On average {N(aas, 1)} sessions were active on a server with {cpus} CPUs, about {N(ratio * 100, 0)}% of capacity. The processors were not a bottleneck in this window.",
                WhyItMatters = "Spare CPU capacity means slowness, if any, comes from waiting (I/O, locks, contention) or from individual statements rather than from a lack of processing power.",
                Technical = $"Average Active Sessions {N(aas, 2)}, CPUs {cpus}, sessions on CPU {N(cpuAas + bg, 2)}.",
                Evidence = ev,
                Docs = [D.Transient]
            });
    }

    private static void CpuVersusWaiting(AnalysisContext ctx)
    {
        var users = T(ctx, "Top User Events");
        if (users == null || users.Rows.Count == 0) return;
        double cpu = 0, total = 0;
        var byClass = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in users.DataRows)
        {
            var p = Pct(r);
            total += p;
            var cls = IsCpu(r.Str("Event")) ? "CPU" : r.Str("Event Class", "Wait Class");
            if (cls.Length == 0) cls = "Other";
            byClass[cls] = byClass.GetValueOrDefault(cls) + p;
            if (IsCpu(r.Str("Event"))) cpu += p;
        }
        if (total <= 0) return;
        var waiting = Math.Max(0, total - cpu);
        var top = byClass.OrderByDescending(k => k.Value).First();
        var bgPct = T(ctx, "Top Background Events")?.DataRows.Sum(Pct) ?? 0;
        var cpuBound = cpu >= 70;
        var waitBound = waiting >= 60;
        var msg = cpuBound
            ? $"Mostly on CPU ({P(cpu, 0)}): the work is processor-bound, so the lever is reducing the amount of work the SQL does."
            : waitBound
                ? $"Mostly waiting ({P(waiting, 0)}), led by '{top.Key}' ({P(top.Value, 0)}): the sessions were held up by something other than the processor."
                : $"A mix of CPU ({P(cpu, 0)}) and waiting ({P(waiting, 0)}).";
        ctx.Add(new Finding
        {
            Severity = Severity.Info,
            Category = "Workload profile",
            Impact = Math.Max(cpu, waiting),
            Title = cpuBound ? $"Active session time was mostly spent on CPU ({P(cpu, 0)})" : waitBound ? $"Active session time was mostly spent waiting ({P(waiting, 0)})" : $"CPU ({P(cpu, 0)}) and waiting ({P(waiting, 0)}) shared the time",
            Plain = msg + " Sampled activity counts only moments when a session was busy; idle sessions are not included.",
            WhyItMatters = cpuBound
                ? "CPU-bound systems get faster by doing less work (better SQL, fewer logical reads), not by tuning storage or locks."
                : "Waiting time points to a specific bottleneck: reading disks, locks, internal contention or the network. Fixing the biggest wait class gives the largest gain.",
            Technical = $"From Top User Events: CPU + Wait for CPU {P(cpu)}; non-CPU {P(waiting)} (" + string.Join(", ", byClass.Where(k => k.Key != "CPU").OrderByDescending(k => k.Value).Take(4).Select(k => $"{k.Key} {P(k.Value)}")) + ")." + (bgPct > 0 ? $" Background processes accounted for another {P(bgPct)} of samples." : "") + " 'CPU + Wait for CPU' includes time spent in the OS run queue.",
            Evidence = byClass.OrderByDescending(k => k.Value).Take(6).Select(k => new Evidence(k.Key, P(k.Value), "share of all sampled activity")).ToList(),
            Recommendations = cpuBound
                ? ["Use the Top SQL sections to find the statements on CPU and reduce their logical reads (indexes, join order, statistics)."]
                : ["Start with the largest wait class below and the SQL listed with it."],
            Docs = [D.Measuring, D.Transient],
            SourceViews = ["V$ACTIVE_SESSION_HISTORY"],
            ChartRefs = ["ash-cpu-vs-wait", "ash-wait-classes"],
            TableRefs = TableIds(ctx, "Top User Events")
        });
    }

    // ------------------------------------------------------------------------------------------ activity over time

    internal sealed record Slot(string Label, double Minutes, double Count, List<(string Event, double Pct)> Events);

    internal static List<Slot> Slots(AnalysisContext ctx)
    {
        var slots = new List<Slot>();
        var t = T(ctx, "Activity Over Time");
        if (t == null) return slots;
        foreach (var r in t.DataRows)
        {
            var label = r.Str("Slot Time (Duration)", "Slot Time");
            var count = r.Val("Slot Count");
            if (label.Length == 0 || count is null) continue;
            var minutes = SlotMinutes(label);
            var s = slots.FirstOrDefault(x => x.Label == label);
            if (s == null) { s = new Slot(label, minutes, count.Value, []); slots.Add(s); }
            if (r.Str("Event").Length > 0) s.Events.Add((r.Str("Event"), r.Val("% Event") ?? 0));
        }
        return slots;
    }

    private static double SlotMinutes(string label)
    {
        var m = Regex.Match(label, @"\(\s*([\d.,]+)\s*(min|sec|hour|hr)", RegexOptions.IgnoreCase);
        if (!m.Success || Apodofos.Core.Parsing.NumberParser.Parse(m.Groups[1].Value) is not { } v) return 0;
        var u = m.Groups[2].Value.ToLowerInvariant();
        return u.StartsWith("sec", StringComparison.Ordinal) ? v / 60 : u.StartsWith('h') ? v * 60 : v;
    }

    private static void ActivityOverTime(AnalysisContext ctx)
    {
        var slots = Slots(ctx).Where(s => s.Minutes >= 0.2).ToList();
        if (slots.Count < 4) return;
        var weight = ctx.Info.AshFromHistory ? 10.0 : 1.0;
        // average active sessions inside each slot: samples x weight / seconds
        var aas = slots.Select(s => s.Count * weight / (s.Minutes * 60)).ToList();
        var ordered = aas.OrderBy(a => a).ToList();
        var median = ordered[ordered.Count / 2];
        var peakIdx = aas.IndexOf(aas.Max());
        var peak = slots[peakIdx];
        var peakAas = aas[peakIdx];
        var table = TableIds(ctx, "Activity Over Time");
        var peakMix = string.Join(", ", peak.Events.OrderByDescending(e => e.Pct).Take(3).Select(e => $"{e.Event} ({N(e.Pct, 1)}%)"));
        var peakTime = peak.Label.Split('(')[0].Trim();

        // saturation: a short peak above the CPU count even if the average is fine
        if (ctx.Cpus is > 0 && peakAas >= ctx.Cpus && (ctx.Aas ?? 0) < ctx.Cpus)
            ctx.Add(new Finding
            {
                Severity = SeverityMath.ForLoad(ctx, Severity.Medium),
                Category = "Workload profile",
                Impact = 20,
                Title = $"A peak at {peakTime} pushed active sessions above the CPU count ({N(peakAas, 1)} vs {ctx.Cpus})",
                Plain = $"The average load over the whole window looks fine, but in the slot starting at {peakTime} about {N(peakAas, 1)} sessions were active at once on {ctx.Cpus} CPUs. Short peaks like this are felt by users even when the average looks calm.",
                WhyItMatters = "Overall averages hide bursts. During the peak, sessions queued for CPU or contended for resources, which is exactly when users complain.",
                Technical = $"Slot {peak.Label}: {N(peak.Count)} samples ~ {N(peakAas, 2)} average active sessions (median slot {N(median, 2)}). Top events in the slot: {peakMix}.",
                Evidence = [new("Peak slot", peak.Label), new("Peak active sessions", N(peakAas, 1)), new("Median slot", N(median, 1)), new("Top events in the peak", peakMix)],
                Recommendations = ["Find what started at that time (batch job, report, backup, connection storm) and move it or throttle it.", "Compare the SQL of the peak slot with the rest of the window using the SQL below."],
                Sql = [new("SQL active in the peak slot (ASH)", $"SELECT sql_id, session_state, event, {Weight(ctx)} AS approx_db_seconds\nFROM   {View(ctx)}\nWHERE  sample_time BETWEEN TO_TIMESTAMP('&peak_begin', 'HH24:MI:SS') AND TO_TIMESTAMP('&peak_end', 'HH24:MI:SS')\nGROUP  BY sql_id, session_state, event\nORDER  BY approx_db_seconds DESC;", $"Replace &peak_begin / &peak_end by the slot boundaries ({peakTime}); use a full TIMESTAMP literal with the date if the window spans midnight.")],
                Docs = [D.Transient],
                SourceViews = ["V$ACTIVE_SESSION_HISTORY"],
                ChartRefs = ["ash-activity"],
                TableRefs = table
            });
        else if (median > 0 && peakAas >= 2.5 * median && peak.Count * weight / Math.Max(1, slots.Sum(s => s.Count * weight)) >= 0.2)
            ctx.Add(new Finding
            {
                Severity = Severity.Info,
                Category = "Workload profile",
                Impact = 10,
                Title = $"Activity peaked at {peakTime} ({N(peakAas / Math.Max(median, 0.01), 1)}x a typical slot)",
                Plain = $"Most of the activity in this report happened around {peakTime}, about {N(peakAas / Math.Max(median, 0.01), 1)} times busier than a typical period. The averages elsewhere in the report understate how busy that moment was.",
                WhyItMatters = "A problem that lasts a few minutes is easy to miss in whole-period averages. Knowing when it happened lets you match it to jobs, deployments or user activity.",
                Technical = $"Slot {peak.Label}: {N(peak.Count)} samples (~{N(peakAas, 2)} AAS) against a median of {N(median, 2)} AAS. Events in the slot: {peakMix}.",
                Evidence = [new("Peak slot", peak.Label), new("Events in the peak", peakMix)],
                Recommendations = ["Ask what ran at that time and check whether it can be moved or spread out."],
                Docs = [D.Transient],
                SourceViews = ["V$ACTIVE_SESSION_HISTORY"],
                ChartRefs = ["ash-activity"],
                TableRefs = table
            });

        // ramp: load rising slot after slot to the end of the window
        var interior = aas.Skip(1).ToList();
        if (interior.Count >= 5)
        {
            var tail = interior.TakeLast(4).ToList();
            var rising = tail.Zip(tail.Skip(1), (a, b) => b >= a * 0.98).All(x => x);
            if (rising && tail[^1] >= 2 * Math.Max(interior.Take(2).Average(), 0.05) && tail[^1] >= Math.Max(1.0, (ctx.Aas ?? 0) * 1.3))
                ctx.Add(new Finding
                {
                    Severity = SeverityMath.ForLoad(ctx, Severity.Low),
                    Category = "Workload profile",
                    Impact = 8,
                    Title = $"Load was still rising at the end of the window ({N(interior.Take(2).Average(), 1)} to {N(tail[^1], 1)} active sessions)",
                    Plain = "The number of active sessions grew steadily towards the end of the report period and had not come back down. The problem may be getting worse, or be bigger than this window shows.",
                    WhyItMatters = "A load that keeps climbing usually ends in a stall (sessions piling up behind a slow statement or a lock), so an extended report is worthwhile.",
                    Technical = $"Per-slot average active sessions at the end: {string.Join(" -> ", tail.Select(a => N(a, 1)))}; first interior slots {N(interior.Take(2).Average(), 2)}. Pile-ups behind one blocker or a slow resource show as growing sessions in one event.",
                    Evidence = tail.Select((a, i) => new Evidence($"Slot {slots.Count - tail.Count + i + 1}", N(a, 1), slots[slots.Count - tail.Count + i].Label)).ToList(),
                    Recommendations = ["Generate a second ASH report for the following period to see whether the load peaked or kept climbing.", "Check the Top Blocking Sessions and the top event in the final slots for a single cause."],
                    Docs = [D.Transient],
                    SourceViews = ["V$ACTIVE_SESSION_HISTORY"],
                    ChartRefs = ["ash-activity"],
                    TableRefs = table
                });
        }
    }

    internal static List<string> TableIds(AnalysisContext ctx, params string[] titles) =>
        titles.Select(t => T(ctx, t)?.Id).Where(i => i != null).Select(i => i!).ToList();
}
