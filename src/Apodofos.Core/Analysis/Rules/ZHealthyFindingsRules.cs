// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Core.Model;
using static Apodofos.Core.Analysis.AnalysisContext;
using static Apodofos.Core.Analysis.Rules.RuleHelpers;
using D = Apodofos.Core.Knowledge.OracleDocs;

namespace Apodofos.Core.Analysis.Rules;

/// <summary>
/// The good news: checks that passed, so that non-DBAs also see what is healthy. Runs after the problem rules
/// (rules are discovered in name order after the built-in ones) and only reports an area when no problem was found there.
/// </summary>
public sealed class ZHealthyFindingsRules : IRule
{
    public string Name => "Healthy areas";

    private static bool Has(AnalysisContext ctx, string category, params string[] titleFragments) =>
        ctx.Findings.Any(f => f.Category == category && f.Severity <= Severity.Low && (titleFragments.Length == 0 || titleFragments.Any(t => f.Title.Contains(t, StringComparison.OrdinalIgnoreCase))));

    public void Evaluate(AnalysisContext ctx)
    {
        Ratios(ctx);
        Parsing(ctx);
        DiskLatency(ctx);
        CommitsAndRedo(ctx);
        UndoAndLimits(ctx);
        MemorySizing(ctx);
        Checkpointing(ctx);
    }

    private static void Ratios(AnalysisContext ctx)
    {
        if (ctx.Efficiency.Count == 0 || ctx.Findings.Any(f => f.Title.Contains("efficiency indicator"))) return;
        var t = ctx.Report.TableStartingWith("Instance Efficiency");
        ctx.Add(new Finding
        {
            Severity = Severity.Ok,
            Category = "Memory",
            Title = "Memory and statement-reuse indicators are healthy",
            Plain = "The database's internal efficiency indicators (data found in memory, sorts done in memory, statements reused, little waiting for internal locks) are all close to their targets.",
            WhyItMatters = "Memory areas are sized adequately for this workload, so tuning effort is better spent elsewhere.",
            Technical = "All Instance Efficiency Percentages are within normal targets. Oracle advises against tuning these ratios in isolation (a 100% buffer cache hit ratio can coexist with inefficient SQL); they are supporting evidence only.",
            Evidence = LoadAndEfficiencyRules.RatioEvidence(ctx),
            Docs = [D.InstanceTuning],
            SourceViews = ["DBA_HIST_SYSSTAT", "DBA_HIST_LATCH"],
            TableRefs = t != null ? [t.Id] : [],
            ChartRefs = ["efficiency"]
        });
    }

    private static void Parsing(AnalysisContext ctx)
    {
        var hard = ctx.LoadPerSec("Hard parses (SQL)", "Hard parses");
        var soft = ctx.Eff("Soft Parse %");
        if (hard is null || Has(ctx, "Parsing") || hard >= 10) return;
        ctx.Add(new Finding
        {
            Severity = Severity.Ok,
            Category = "Parsing",
            Title = $"SQL statements are reused efficiently ({N(hard, 1)} hard parses per second)",
            Plain = "The application reuses already-compiled SQL statements instead of making the database compile each one again, which keeps CPU use down.",
            WhyItMatters = "Compiling is expensive; a low rate means the application uses placeholders (bind variables) correctly.",
            Technical = $"Hard parses/s {N(hard, 1)} (guideline: low single digits); Soft Parse % {P(soft)}; parses/s {N(ctx.LoadPerSec("Parses (SQL)", "Parses"), 0)}.",
            Evidence = [new("Hard parses per second", N(hard, 1)), new("Soft Parse %", P(soft, 2)), new("Parses per second", N(ctx.LoadPerSec("Parses (SQL)", "Parses"), 1))],
            Docs = [D.CursorSharing, D.SqlProcessing],
            SourceViews = ["DBA_HIST_SYSSTAT"]
        });
    }

    private static void DiskLatency(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("Tablespace IO Stats");
        if (t == null || t.Rows.Count == 0 || Has(ctx, "I/O")) return;
        double reads = 0, rdMs = 0, writes = 0, wrMs = 0;
        foreach (var r in IoAndMemoryRules.TsIo(ctx, t))
        {
            reads += r.Reads; rdMs += r.Reads * r.RdMs;
            writes += r.Writes; wrMs += r.Writes * r.WrMs;
        }
        var avgRd = reads > 0 ? rdMs / reads : (double?)null;
        // Data file writes are asynchronous (DBWR); a value of 0 means the report gave none. Their latency only matters when sessions wait for them.
        var avgWr = writes > 0 && wrMs > 0 ? wrMs / writes : (double?)null;
        var writesSlow = avgWr is > 20;
        if (reads + writes <= 0) return;
        var userIo = RuleHelpers.WaitClassPct(ctx, "User I/O");
        ctx.Add(new Finding
        {
            Severity = Severity.Ok,
            Category = "I/O",
            Title = "Disk read and write times look healthy",
            Plain = (writesSlow ? "Reads from the storage were answered quickly. Background writes of changed data average a longer time, but they are asynchronous and no session waited on them in a measurable way." : "The storage answered quickly: none of the storage areas that were used noticeably had slow reads or writes.")
                    + (userIo is not null ? $" Waiting for data from disk took only {P(userIo)} of the database time." : ""),
            WhyItMatters = "Storage is not a bottleneck in this period, so buying faster disks would not help.",
            Technical = $"Tablespace IO Stats: weighted average read {Ms(avgRd)} over {N(reads)} reads, average write {Ms(avgWr)} over {N(writes)} writes (DBWR writes are asynchronous; judge them with 'free buffer waits' and checkpoint findings); no tablespace above 20 ms for reads with significant volume.",
            Evidence = [new("Reads in the period", N(reads), ctx.ElapsedSec is > 0 ? $"{N(reads / ctx.ElapsedSec.Value, 1)} per second" : null), new("Average read time", Ms(avgRd)), new("Average write time", avgWr is null ? "n/a" : Ms(avgWr), writesSlow ? "asynchronous background writes; high but not user-facing" : null), new("User I/O share of DB time", P(userIo))],
            Docs = [D.IoDesign],
            SourceViews = ["DBA_HIST_FILESTATXS"],
            TableRefs = [t.Id],
            ChartRefs = ["tablespace-io"]
        });
    }

    private static void CommitsAndRedo(AnalysisContext ctx)
    {
        var lgwr = ctx.Report.Table("Background Wait Events")?.RowByLabel("log file parallel write");
        var avg = lgwr.Val("Avg wait (ms)", "Avg wait", "Avg Wait");
        var waits = lgwr.Val("Waits") ?? 0;
        if (avg is null or > 5 || waits < 100 || Has(ctx, "I/O", "Redo", "redo") || ctx.Findings.Any(f => f.Title.Contains("log file", StringComparison.OrdinalIgnoreCase) && f.Severity <= Severity.Low)) return;
        var sync = RuleHelpers.ForegroundEvent(ctx, "log file sync");
        var commits = ctx.LoadPerSec("Transactions");
        ctx.Add(new Finding
        {
            Severity = Severity.Ok,
            Category = "I/O",
            Title = $"Saving transactions (commits) is fast: redo writes average {Ms(avg)}",
            Plain = "Every time an application saves a change, the database writes it to a log first. Those writes were fast, so users do not wait long when they save.",
            WhyItMatters = "Commit speed sets the lower limit for every transaction; this is in good shape.",
            Technical = $"Background 'log file parallel write' average {Ms(avg)} over {N(waits)} writes (healthy: under about 5 ms)" + (sync is { } s ? $"; foreground 'log file sync' average {Ms(s.Val("Avg wait (ms)", "Wait Avg(ms)"))}." : "."),
            Evidence = [new("Redo write time", Ms(avg)), new("Redo writes", N(waits)), new("Transactions per second", N(commits, 1)), new("Redo generated per second", Bytes(ctx.LoadPerSec("Redo size (bytes)", "Redo size")))],
            Docs = [D.RedoLog, D.WaitEvents],
            SourceViews = ["DBA_HIST_BG_EVENT_SUMMARY"]
        });
    }

    private static void UndoAndLimits(AnalysisContext ctx)
    {
        var ut = ctx.Report.Table("Undo Segment Summary");
        if (ut.FirstRow() is not { } r || ctx.Findings.Any(f => f.Category == "Configuration" && (f.Title.Contains("snapshot too old") || f.Title.Contains("Undo that") || f.Title.Contains("Longest query")))) return;
        var sto = r.Txt("STO/ OOS", "STO/OOS");
        if (sto != "0/0") return;
        ctx.Add(new Finding
        {
            Severity = Severity.Ok,
            Category = "Configuration",
            Title = "No 'snapshot too old' or undo space errors",
            Plain = "The database kept enough undo history (the before-images that let long queries see a consistent picture of the data) and did not run out of undo space, so no query failed because of it.",
            WhyItMatters = "Failed reports and jobs (ORA-01555) are user-visible errors; none occurred in this period.",
            Technical = $"Undo Segment Summary: STO/OOS = {sto}; longest query {Dur(r.Val("Max Qry Len (s)"))}; tuned retention (min/max) {r.Txt("Min/Max TR (mins)")} minutes; maximum concurrent transactions {r.Txt("Max Tx Concurcy", "Max Tx Concurrency")}.",
            Evidence = [new("Snapshot-too-old / out-of-space errors", sto), new("Longest query", Dur(r.Val("Max Qry Len (s)"))), new("Undo retention (tuned min/max)", r.Txt("Min/Max TR (mins)") + " minutes")],
            Docs = [D.Undo],
            SourceViews = ["DBA_HIST_UNDOSTAT"],
            TableRefs = [ut!.Id]
        });
    }

    private static void MemorySizing(AnalysisContext ctx)
    {
        var mem = ctx.Report.Table("Memory Statistics")?.RowByLabel("% Host Mem used for SGA+PGA");
        var pct = mem.Val("End") ?? mem?.NumAt(2);
        var pga = ctx.Report.Table("PGA Aggr Summary").FirstRow().Val("PGA Cache Hit %");
        if (pct is null && pga is null) return;
        if (ctx.Findings.Any(f => f.Category == "Memory" && f.Severity <= Severity.Low && !f.Title.Contains("efficiency indicator"))) return;
        var ev = new List<Evidence>();
        if (pct is not null) ev.Add(new("Host memory used by SGA + PGA", P(pct)));
        if (pga is not null) ev.Add(new("PGA work done in memory", P(pga, 1)));
        ctx.Add(new Finding
        {
            Severity = Severity.Ok,
            Category = "Memory",
            Title = "Memory is comfortably sized and the server is not short of RAM",
            Plain = "The database's memory fits comfortably in the server" + (pga is not null ? ", and sorting and joining are almost always done in memory instead of spilling to disk" : "") + ". The advisories do not suggest that adding memory would noticeably help.",
            WhyItMatters = "Adding RAM would not make this system faster, and there is no sign of the server swapping.",
            Technical = (pct is not null ? $"% Host Mem used for SGA+PGA {P(pct)}. " : "") + (pga is not null ? $"PGA Cache Hit % {P(pga, 2)} with no over-allocation. " : "") + "No paging problem, and the buffer cache / SGA / PGA advisories predict no worthwhile gain.",
            Evidence = ev,
            Docs = [D.MemoryAllocation, D.Pga, D.BufferCache],
            SourceViews = ["DBA_HIST_PGASTAT", "DBA_HIST_OSSTAT"],
            TableRefs = new[] { ctx.Report.Table("Memory Statistics")?.Id, ctx.Report.Table("PGA Aggr Summary")?.Id }.OfType<string>().ToList()
        });
    }

    private static void Checkpointing(AnalysisContext ctx)
    {
        var ck = ctx.Report.Table("Checkpoint Activity").FirstRow();
        if (ck is null || ctx.Findings.Any(f => f.Title.Contains("checkpoint", StringComparison.OrdinalIgnoreCase) || f.Title.Contains("free memory buffers") || f.Title.Contains("Redo logs switch"))) return;
        var irs = ctx.Report.Table("Instance Recovery Stats");
        var last = irs is { Rows.Count: > 0 } ? new AwrRow(irs, irs.Rows[^1]) : (AwrRow?)null;
        var est = last.Val("Estd MTTR (s)");
        var target = last.Val("Targt MTTR (s)");
        ctx.Add(new Finding
        {
            Severity = Severity.Ok,
            Category = "I/O",
            Title = "Background writing of changed data keeps up" + (est is not null ? $" (crash recovery estimated at {N(est)} s)" : ""),
            Plain = "The database writer saves changed data to disk steadily and sessions did not have to wait for it, and the redo logs are not forcing extra writes."
                    + (est is not null && target is not null ? $" If the server crashed now, the database would be back in about {N(est)} seconds" + (target > 0 ? $" (goal: {N(target)} seconds)." : " (no recovery-time target is set, FAST_START_MTTR_TARGET=0).") : ""),
            WhyItMatters = "This keeps response times steady and limits the downtime after a crash.",
            Technical = $"Checkpoint Activity: MTTR writes {N(ck.Val("MTTR Writes"))}, log-size writes {N(ck.Val("Log Size Writes"))}, autotune writes {N(ck.Val("Autotune Ckpt Writes"))}; no 'free buffer waits'." + (est is not null ? $" Instance Recovery: estimated MTTR {N(est)} s" + (target > 0 ? $" vs target {N(target)} s." : "; no MTTR target set.") : ""),
            Evidence = [new("Checkpoint writes driven by the recovery-time target", N(ck.Val("MTTR Writes"))), new("Writes forced by redo log size", N(ck.Val("Log Size Writes")))],
            Docs = [D.RedoLog, D.BufferCache],
            SourceViews = ["DBA_HIST_INSTANCE_RECOVERY", "DBA_HIST_BUFFER_POOL_STATISTICS"],
            TableRefs = new[] { ctx.Report.Table("Checkpoint Activity")?.Id, irs?.Id }.OfType<string>().ToList()
        });
    }
}

/// <summary>
/// Safety net that runs last: guarantees that every finding has Plain, WhyItMatters, Technical, at least one recommendation
/// (except Ok/Info) and a documentation reference, so the UI never shows an empty section.
/// </summary>
public sealed class ZzFinalizeFindingsRule : IRule
{
    public string Name => "Finding quality gate";

    private static readonly Dictionary<string, DocRef> CategoryDoc = new()
    {
        ["CPU"] = D.OsResources, ["Parsing"] = D.SqlProcessing, ["SQL"] = D.SqlTuningAdvisor, ["I/O"] = D.IoDesign, ["Memory"] = D.MemoryAllocation,
        ["Configuration"] = D.ConfiguringDb, ["Wait events"] = D.WaitEvents, ["Workload profile"] = D.Measuring, ["Time model"] = D.Measuring,
        ["Application"] = D.DesignForPerformance, ["Connections"] = D.Processes, ["RAC"] = D.Rac, ["ADDM"] = D.Addm, ["Report scope"] = D.Awr
    };

    public void Evaluate(AnalysisContext ctx)
    {
        foreach (var f in ctx.Findings)
        {
            f.Evidence.RemoveAll(e => string.IsNullOrWhiteSpace(e.Value) || e.Value == "n/a");
            if (string.IsNullOrWhiteSpace(f.Plain)) f.Plain = f.Title + ".";
            if (string.IsNullOrWhiteSpace(f.WhyItMatters)) f.WhyItMatters = f.Severity == Severity.Ok ? "This area is in good shape." : "It takes database time away from users and can limit how much work the system can do.";
            if (string.IsNullOrWhiteSpace(f.Technical)) f.Technical = f.Plain;
            if (f.Recommendations.Count == 0 && f.Severity <= Severity.Low)
                f.Recommendations.Add("Review the evidence and the referenced AWR sections, and confirm with ASH (DBA_HIST_ACTIVE_SESS_HISTORY) before changing anything.");
            if (f.Docs.Count == 0 && CategoryDoc.TryGetValue(f.Category, out var d)) f.Docs.Add(d);
            if (f.Docs.Count == 0) f.Docs.Add(D.Measuring);
        }
    }
}
