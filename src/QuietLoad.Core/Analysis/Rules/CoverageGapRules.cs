// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using QuietLoad.Core.Model;
using static QuietLoad.Core.Analysis.AnalysisContext;
using static QuietLoad.Core.Analysis.Rules.RuleHelpers;
using D = QuietLoad.Core.Knowledge.OracleDocs;

namespace QuietLoad.Core.Analysis.Rules;

/// <summary>
/// Rules added after the coverage review (docs/coverage-matrix.md): sections the parsers produced that no rule used.
/// Both checks only fire on clear evidence and never throw on missing data.
/// </summary>
public sealed class CoverageGapRules : IRule
{
    public string Name => "Coverage gaps (file I/O, dictionary cache)";

    public void Evaluate(AnalysisContext ctx)
    {
        SlowDataFiles(ctx);
        DictionaryCache(ctx);
        LatencyTail(ctx);
    }

    private static readonly (string Event, double TailPct, string What, string Fix)[] TailEvents =
    [
        ("log file sync", 5, "commits", "Check 'log file parallel write' latency and redo storage; batch commits in the application where possible."),
        ("log file parallel write", 5, "redo log writes", "Place redo logs on the fastest storage, separate from busy data files, and check the storage team's write latency."),
        ("db file sequential read", 10, "single-block disk reads", "Check storage latency spikes (queueing, snapshots, backups running in the window) and reduce physical reads through SQL tuning."),
        ("db file scattered read", 10, "multiblock disk reads", "Check storage throughput and reduce full scans through indexing or partition pruning.")
    ];

    /// <summary>
    /// Wait Event Histogram: an average can look healthy while a slow tail (32 ms or more) hurts users.
    /// Only events that cost at least 1% of DB time are checked, so the tail is material.
    /// </summary>
    private static void LatencyTail(AnalysisContext ctx)
    {
        var hist = ctx.Report.Table("Wait Event Histogram");
        if (hist == null || hist.Headers.Count < 8) return;
        var rows = new List<(string Ev, double Tail, double Waits, double Pct, string What, string Fix, double Limit)>();
        foreach (var (ev, limit, what, fix) in TailEvents)
        {
            var row = hist.RowByLabel(ev);
            var pct = ForegroundEventPct(ctx, ev);
            if (row is not { } r || pct is not >= 1) continue;
            var n = hist.Headers.Count;
            var tail = (r.NumAt(n - 1) ?? 0) + (r.NumAt(n - 2) ?? 0); // the two last buckets: 32 ms - 1 s and over 1 s
            if (tail >= limit && tail <= 100) rows.Add((ev, tail, r.Num("Total Waits") ?? 0, pct.Value, what, fix, limit));
        }
        if (rows.Count == 0) return;
        var worst = rows.OrderByDescending(x => x.Tail * x.Pct).First();
        ctx.Add(new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, rows.Any(x => x.Tail >= 20) ? Severity.Medium : Severity.Low),
            Category = "Wait events",
            Title = $"Some {worst.What} are very slow even if the average looks fine ({P(worst.Tail, 1)} of '{worst.Ev}' waits took 32 ms or more)",
            Plain = "Averages hide spikes. A noticeable share of waits for this event took a third of a second or longer, and users feel those slow outliers.",
            WhyItMatters = "Occasional very slow waits show up as random pauses in the application, which are hard to explain from averages alone.",
            Technical = "Wait Event Histogram (DBA_HIST_EVENT_HISTOGRAM): share of waits in the two slowest buckets (32 ms to 1 s, over 1 s) for events costing at least 1% of DB time. Compare with the average in 'Foreground Wait Events'.",
            Evidence = rows.Select(x => new Evidence(x.Ev, $"{P(x.Tail, 1)} of waits at 32 ms or more", $"{P(x.Pct, 1)} of DB time, {N(x.Waits)} waits")).ToList(),
            Recommendations = rows.Select(x => $"'{x.Ev}': {x.Fix}").Distinct().ToList(),
            Sql = [new("Current latency distribution of the event", $"""
                SELECT event, wait_time_milli, wait_count
                FROM   v$event_histogram
                WHERE  event = '{worst.Ev}'
                ORDER  BY wait_time_milli;
                """)],
            Docs = [D.WaitEvents, D.Measuring],
            SourceViews = ["DBA_HIST_EVENT_HISTOGRAM"],
            TableRefs = [hist.Id],
            ChartRefs = ["wait-histogram"],
            Impact = Math.Min(100, worst.Pct * worst.Tail / 100)
        });
    }

    /// <summary>File IO Stats: the individual files behind a slow-tablespace finding (or slow files in an otherwise fine tablespace).</summary>
    private static void SlowDataFiles(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("File IO Stats");
        var dbTime = ctx.DbTimeSec ?? 0;
        if (t == null || dbTime <= 0) return;
        var slow = t.DataRows
            .Select(r => (File: r.Str("Filename"), Ts: r.Str("Tablespace"), Reads: r.Num("Reads") ?? 0, Ms: r.Num("Av Rd(ms)") ?? 0))
            // at least ~0.5% of DB time spent reading from this one file, and slower than 20 ms per read
            .Where(x => x.Reads >= 1000 && x.Ms > 20 && x.Reads * x.Ms / 1000 / dbTime * 100 >= 0.5)
            .OrderByDescending(x => x.Reads * x.Ms).Take(6).ToList();
        if (slow.Count == 0) return;
        var pct = Math.Min(100, slow.Sum(x => x.Reads * x.Ms) / 1000 / dbTime * 100);
        ctx.Add(new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, slow.Any(x => x.Ms > 50) ? Severity.Medium : Severity.Low),
            Category = "I/O",
            Title = $"{slow.Count} data file(s) answer reads slowly (over 20 ms per read)",
            Plain = "A few individual database files take much longer than normal to return data. Pinpointing the files shows which disks or volumes to look at.",
            WhyItMatters = "Every query that touches these files waits for the slow disk, which stretches response times.",
            Technical = "File IO Stats (DBA_HIST_FILESTATXS): Av Rd(ms) above 20 ms for files whose reads cost at least 0.5% of DB time each. Multiblock reads are slower than single-block reads; compare with Av 1-bk Rd(ms) where present.",
            Evidence = slow.Select(x => new Evidence(string.IsNullOrEmpty(x.File) ? x.Ts : x.File, $"{N(x.Ms, 1)} ms avg read", $"{N(x.Reads)} reads, tablespace {x.Ts}")).ToList(),
            Recommendations =
            [
                "Ask the storage team to check latency and queue depth on the volumes holding these files.",
                "Move or stripe the busiest files across more devices (ASM rebalance, or relocate with ALTER DATABASE MOVE DATAFILE on 12.1+).",
                "Reduce physical reads against these files by tuning the SQL in 'SQL ordered by Reads'."
            ],
            Sql = [new("Current read latency per data file", """
                SELECT f.file_name, i.small_read_reqs,
                       ROUND(i.small_read_servicetime / NULLIF(i.small_read_reqs, 0), 2) AS avg_small_read_ms
                FROM   v$iostat_file i
                       JOIN dba_data_files f ON f.file_id = i.file_no
                WHERE  i.filetype_name = 'Data File'
                ORDER  BY avg_small_read_ms DESC NULLS LAST;
                """, "V$IOSTAT_FILE needs 11.2 or later.")],
            Docs = [D.IoDesign],
            SourceViews = ["DBA_HIST_FILESTATXS"],
            TableRefs = [t.Id],
            ChartRefs = ["file-io-latency"],
            Impact = pct
        });
    }

    /// <summary>Dictionary Cache Stats: a high overall miss rate with many requests means the row cache (shared pool) is under pressure.</summary>
    private static void DictionaryCache(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("Dictionary Cache Stats");
        if (t == null) return;
        var rows = t.DataRows.Select(r => (Cache: r.StrAt(0), Gets: r.NumAt(1) ?? 0, Miss: r.NumAt(2) ?? 0)).Where(x => x.Gets > 0).ToList();
        var total = rows.Sum(x => x.Gets);
        if (total < 100_000) return; // too few requests for a rate to mean anything
        var weighted = rows.Sum(x => x.Gets * x.Miss) / total;
        if (weighted < 5) return;
        var worst = rows.Where(x => x.Gets >= 10_000 && x.Miss >= 10).OrderByDescending(x => x.Gets * x.Miss).Take(5).ToList();
        ctx.Add(new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, weighted >= 15 ? Severity.Medium : Severity.Low),
            Category = "Memory",
            Title = $"The data dictionary cache misses {P(weighted, 1)} of the time",
            Plain = "Oracle keeps a small in-memory copy of its own catalog (table, column and privilege definitions). More than a few percent of lookups missed that copy and had to read from disk.",
            WhyItMatters = "Each miss forces extra recursive SQL during parsing, which slows statement compilation for everyone.",
            Technical = $"Dictionary Cache Stats (V$ROWCACHE / DBA_HIST_ROWCACHE_SUMMARY): request-weighted Pct Miss {P(weighted, 1)} over {N(total)} gets. Oracle's guidance is a miss rate in the low single digits once the instance has warmed up; a cold start (short uptime) inflates it. Check whether the report window starts shortly after startup and whether the shared pool is being shrunk or flushed.",
            Evidence = worst.Select(x => new Evidence(x.Cache, $"{P(x.Miss, 1)} miss", $"{N(x.Gets)} requests")).ToList(),
            Recommendations =
            [
                "If the instance was recently restarted, re-check after the cache has warmed up before changing anything.",
                "Otherwise increase the shared pool (SHARED_POOL_SIZE, or the SGA_TARGET / MEMORY_TARGET that feeds it); see the Shared Pool Advisory.",
                "Reduce hard parsing, which churns the dictionary cache (use bind variables)."
            ],
            Sql = [new("Dictionary cache hit rate right now", """
                SELECT parameter, gets, getmisses,
                       ROUND(getmisses / NULLIF(gets, 0) * 100, 2) AS pct_miss
                FROM   v$rowcache
                WHERE  gets > 10000
                ORDER  BY getmisses DESC;
                """)],
            Docs = [D.SharedPool],
            SourceViews = ["DBA_HIST_ROWCACHE_SUMMARY"],
            TableRefs = [t.Id],
            ChartRefs = ["dictionary-cache-miss"],
            Impact = 0
        });
    }
}
