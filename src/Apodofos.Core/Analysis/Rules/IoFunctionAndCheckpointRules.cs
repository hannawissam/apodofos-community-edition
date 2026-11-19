// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Core.Model;
using static Apodofos.Core.Analysis.AnalysisContext;
using D = Apodofos.Core.Knowledge.OracleDocs;

namespace Apodofos.Core.Analysis.Rules;

/// <summary>
/// "IOStat by Function summary" shows which database component (LGWR, DBWR, buffer cache reads, direct I/O) issues the I/O
/// and how long each wait takes; and the checkpoint / instance-recovery sections show whether DBWR work is driven by redo size.
/// </summary>
public sealed class IoFunctionAndCheckpointRules : IRule
{
    public string Name => "I/O by function and checkpoints";

    public void Evaluate(AnalysisContext ctx)
    {
        IoFunctions(ctx);
        Checkpoints(ctx);
    }

    private static AwrRow? LastRow(AwrTable? t) => t is { Rows.Count: > 0 } ? new AwrRow(t, t.Rows[^1]) : null;

    private static void IoFunctions(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("IOStat by Function summary");
        if (t == null || t.Rows.Count == 0) return;
        // (function, healthy limit ms, minimum waits to be meaningful, plain-language name)
        var checks = new (string Fn, double LimitMs, double MinWaits, string Friendly, string Fix)[]
        {
            ("LGWR", 10, 1000, "the redo log writer (every commit waits for it)", "Place redo logs on the fastest, lowest-latency storage and check for contention with other I/O on the same device."),
            ("Buffer Cache Reads", 20, 5000, "reads of single data blocks into the cache", "Check storage latency for the busiest data files and reduce physical reads in the top SQL."),
            ("DBWR", 30, 5000, "the database writer (background, but it must keep up)", "Check write latency of the data files and that asynchronous I/O is enabled."),
            ("Direct Reads", 30, 1000, "large direct reads that bypass the cache", "Check storage throughput; reduce full scans of large tables."),
            ("Direct Writes", 30, 1000, "bulk direct writes", "Check write throughput of the target files and the batch window.")
        };
        var bad = new List<Evidence>();
        string? worstFix = null;
        foreach (var c in checks)
        {
            var r = t.RowByLabel(c.Fn);
            if (r is not { } row || !row.Label.StartsWith(c.Fn, StringComparison.OrdinalIgnoreCase)) continue;
            var avg = row.Num("Avg Tm(ms)", "Avg Tm (ms)");
            var waits = row.Num("Waits: Count", "Waits Count") ?? 0;
            if (avg is > 0 && avg > c.LimitMs && waits >= c.MinWaits)
            {
                bad.Add(new(c.Fn, $"{Ms(avg)} average wait", $"{N(waits)} waits; {c.Friendly}; usually under {N(c.LimitMs)} ms"));
                worstFix ??= c.Fix;
            }
        }
        // the redo-write case is already reported with more detail by the wait-event rules
        if (bad.Count == 0 || (bad.Count == 1 && bad[0].Label == "LGWR" && ctx.Findings.Any(f => f.Title.StartsWith("Redo log writes are slow")))) return;
        ctx.Add(new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, bad.Any(b => b.Label is "LGWR" or "Buffer Cache Reads") ? Severity.Medium : Severity.Low),
            Category = "I/O",
            Title = $"I/O is slow for {string.Join(", ", bad.Select(b => b.Label))}",
            Plain = "The database tracks how long its different components wait for the storage. For the components listed below the average wait is longer than expected.",
            WhyItMatters = "Slow commits, slow reads and slow background writes all end up as waiting time for users.",
            Technical = "IOStat by Function summary (DBA_HIST_IOSTAT_FUNCTION): Avg Tm(ms) is the average wait per request of that function in milliseconds.",
            Evidence = bad,
            Recommendations = [worstFix!, "Compare with the operating system view of the same devices (iostat) to see whether the latency is added in the database server, the network or the storage array."],
            Docs = [D.IoDesign, D.WaitEvents],
            SourceViews = ["DBA_HIST_IOSTAT_FUNCTION"],
            TableRefs = [t.Id],
            ChartRefs = ["iostat-function"]
        });
    }

    private static void Checkpoints(AnalysisContext ctx)
    {
        var ck = ctx.Report.Table("Checkpoint Activity").FirstRow();
        var parts = new[] { "MTTR Writes", "Log Size Writes", "Log Ckpt Writes", "Other Settings Writes", "Autotune Ckpt Writes", "Thread Ckpt Writes" };
        var total = parts.Sum(p => ck.Val(p) ?? 0);
        var logSize = ck.Val("Log Size Writes") ?? 0;
        var freeBuf = ctx.Report.Table("Buffer Pool Statistics").FirstRow().Val("Free Buff Wait") ?? 0;
        var freeWaitPct = RuleHelpers.ForegroundEventPct(ctx, "free buffer waits") ?? 0;

        if (total >= 1000 && logSize / total >= 0.5)
        {
            var optMb = LastRow(ctx.Report.Table("Instance Recovery Stats")).Val("Opt Log Sz(M)");
            ctx.Add(new Finding
            {
                Severity = SeverityMath.ForLoad(ctx, Severity.Low),
                Category = "Configuration",
                Title = $"Most checkpoint writes ({P(logSize / total * 100, 0)}) are forced by the size of the redo logs",
                Plain = "The database writer must save changed data to disk before a redo log can be reused. Here most of that writing is triggered because the redo logs are too small, not by the normal schedule.",
                WhyItMatters = "Forced checkpoints make the database writer work in bursts, which can delay sessions ('log file switch (checkpoint incomplete)' and 'free buffer waits').",
                Technical = $"Checkpoint Activity: Log Size Writes {N(logSize)} of {N(total)} checkpoint writes." + (optMb is > 0 ? $" Instance Recovery Stats suggest an optimal redo log size of about {N(optMb)} MB (OPTIMAL_LOGFILE_SIZE, available when FAST_START_MTTR_TARGET is set)." : ""),
                Evidence = [new("Log Size Writes", N(logSize)), new("All checkpoint writes", N(total))],
                Recommendations = ["Enlarge the online redo logs (see the redo log finding for a size) so that log size no longer drives checkpoints.", "Set FAST_START_MTTR_TARGET to let Oracle pace checkpoints by the recovery-time goal."],
                Docs = [D.RedoLog, D.Parameter("FAST_START_MTTR_TARGET")],
                SourceViews = ["DBA_HIST_INSTANCE_RECOVERY", "DBA_HIST_SYSSTAT"],
                TableRefs = new[] { ctx.Report.Table("Checkpoint Activity")?.Id }.OfType<string>().ToList()
            });
        }

        if (freeBuf >= 1000 || freeWaitPct >= 1)
        {
            ctx.Add(new Finding
            {
                Severity = SeverityMath.ForLoad(ctx, freeWaitPct >= 5 ? Severity.Medium : Severity.Low),
                Category = "I/O",
                Title = $"Sessions waited for free memory buffers ({N(freeBuf)} 'free buffer waits')",
                Plain = "Sessions needed room in the data cache to read a block but every buffer held changed data not yet written to disk, so they had to wait for the database writer.",
                WhyItMatters = "It means the database writer cannot keep up with the volume of changes, and users wait for it.",
                Technical = $"Buffer Pool Statistics: Free Buff Wait {N(freeBuf)}; 'free buffer waits' event {P(freeWaitPct)} of DB time. Check DBWR write latency (IOStat by Function), DB_WRITER_PROCESSES, asynchronous I/O and cache size.",
                Evidence = [new("Free buffer waits (count)", N(freeBuf)), new("Event share of DB time", P(freeWaitPct))],
                Recommendations = ["Verify asynchronous I/O (FILESYSTEMIO_OPTIONS=SETALL on file systems).", "Check data file write latency and consider more DBWR processes (DB_WRITER_PROCESSES).", "Check the Buffer Pool Advisory before enlarging the cache."],
                Docs = [D.BufferCache, D.IoDesign, D.Parameter("DB_WRITER_PROCESSES")],
                SourceViews = ["DBA_HIST_BUFFER_POOL_STATISTICS", "DBA_HIST_SYSTEM_EVENT"],
                TableRefs = new[] { ctx.Report.Table("Buffer Pool Statistics")?.Id }.OfType<string>().ToList()
            });
        }

        var rec = LastRow(ctx.Report.Table("Instance Recovery Stats"));
        var target = rec.Val("Targt MTTR (s)");
        var est = rec.Val("Estd MTTR (s)");
        if (target is > 0 && est is > 0 && est > target * 1.5 && est > 30)
        {
            ctx.Add(new Finding
            {
                Severity = Severity.Low,
                Category = "Configuration",
                Title = $"Crash recovery would take longer ({N(est)} s) than the target ({N(target)} s)",
                Plain = "If the server crashed now, the database would need longer to restart than the recovery goal that was configured.",
                WhyItMatters = "The restart after a failure would take longer than planned, extending an outage.",
                Technical = $"Instance Recovery Stats: Estd MTTR {N(est)} s vs Targt MTTR {N(target)} s (FAST_START_MTTR_TARGET).",
                Evidence = [new("Estimated recovery time", N(est) + " s"), new("Target", N(target) + " s")],
                Recommendations = ["Check that DBWR can write fast enough (see I/O findings), and review FAST_START_MTTR_TARGET."],
                Docs = [D.Parameter("FAST_START_MTTR_TARGET"), D.RedoLog],
                SourceViews = ["DBA_HIST_INSTANCE_RECOVERY"]
            });
        }
    }
}
