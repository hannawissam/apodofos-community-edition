// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Core.Model;
using Apodofos.Core.Parsing;
using static Apodofos.Core.Analysis.AnalysisContext;
using static Apodofos.Core.Analysis.Rules.RuleHelpers;
using D = Apodofos.Core.Knowledge.OracleDocs;

namespace Apodofos.Core.Analysis.Rules;

/// <summary>Tablespace/file I/O latency, and memory sizing based on Oracle's advisories (buffer cache, PGA, shared pool, SGA).</summary>
public sealed class IoAndMemoryRules : IRule
{
    public string Name => "I/O and memory";

    public void Evaluate(AnalysisContext ctx)
    {
        TablespaceLatency(ctx);
        TempUsage(ctx);
        BufferCacheAdvisory(ctx);
        PgaAdvisory(ctx);
        SgaAdvisory(ctx);
        SharedPool(ctx);
        HostMemory(ctx);
    }

    private static void TablespaceLatency(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("Tablespace IO Stats");
        if (t == null || ctx.ElapsedSec is not > 0) return;
        // 12c+ reports give the single-block latency separately ("Av 1-bk Rd(ms)"); multiblock reads naturally take longer.
        var dbTime = ctx.DbTimeSec ?? 0;
        var slowReads = TsIo(ctx, t)
            .Select(r => (Ts: r.Ts, Reads: r.Reads, Rd: (double?)r.RdMs, Writes: r.Writes, Wr: (double?)r.WrMs))
            // significant = at least ~0.5% of DB time spent waiting for these reads (a few thousand reads in hours is noise)
            .Where(x => x.Reads >= 1000 && x.Rd is > 20 && dbTime > 0 && x.Reads * x.Rd!.Value / 1000 / dbTime * 100 >= 0.5)
            .OrderByDescending(x => x.Reads * x.Rd)
            .Take(6).ToList();
        var slowWrites = TsIo(ctx, t)
            .Select(r => (Ts: r.Ts, Writes: r.Writes, Wr: (double?)r.WrMs))
            .Where(x => x.Writes >= 1000 && x.Wr is > 20 && dbTime > 0 && x.Writes * x.Wr!.Value / 1000 / dbTime * 100 >= 0.5)
            .Take(6).ToList();

        if (slowReads.Count > 0)
        {
            ctx.Add(new Finding
            {
                Severity = SeverityMath.ForLoad(ctx, slowReads.Any(x => x.Rd > 50) ? Severity.High : Severity.Medium),
                Category = "I/O",
                Title = $"Disk reads are slow for {slowReads.Count} tablespace(s) (over 20 ms per read)",
                Plain = "Reading data from disk takes longer than it should for some storage areas. Modern storage normally answers in under 10 ms, and flash storage in 1-2 ms.",
                WhyItMatters = "Slow storage stretches every query that needs data not already in memory.",
                Technical = "Tablespace IO Stats (DBA_HIST_FILESTATXS): average single-block read time (Av 1-bk Rd(ms), or Av Rd(ms) in older layouts) above 20 ms for tablespaces whose reads cost at least 0.5% of DB time. Multiblock reads (Av Blks/Rd > 1) naturally take longer than single-block reads; 12c+ reports list them separately.",
                Evidence = slowReads.Select(x => new Evidence(x.Ts, $"{N(x.Rd, 1)} ms avg read", $"{N(x.Reads)} reads")).ToList(),
                Recommendations =
                [
                    "Ask the storage/system team to check latency and queue depth for the volumes holding these tablespaces.",
                    "Spread hot data files across more disks/LUNs (or ASM disks).",
                    "Reduce physical reads by tuning the SQL in 'SQL ordered by Reads' and checking the Buffer Pool Advisory."
                ],
                Sql = [new("Current single-block read latency per data file", """
                    SELECT f.file_name, i.small_read_reqs,
                           ROUND(i.small_read_servicetime / NULLIF(i.small_read_reqs, 0), 2) AS avg_small_read_ms,
                           i.large_read_reqs,
                           ROUND(i.large_read_servicetime / NULLIF(i.large_read_reqs, 0), 2) AS avg_large_read_ms
                    FROM   v$iostat_file i
                           JOIN dba_data_files f ON f.file_id = i.file_no
                    WHERE  i.filetype_name = 'Data File'
                    ORDER  BY avg_small_read_ms DESC NULLS LAST;
                    """, "V$IOSTAT_FILE needs 11.2 or later. Add FETCH FIRST 20 ROWS ONLY (12c and later) to limit the output.")],
                Docs = [D.IoDesign],
                SourceViews = ["DBA_HIST_FILESTATXS", "DBA_HIST_IOSTAT_FILETYPE"],
                TableRefs = [t.Id],
                ChartRefs = ["tablespace-io"]
            });
        }
        // the all-clear is produced by the healthy-findings rule (with real numbers)

        if (slowWrites.Count > 0)
        {
            ctx.Add(new Finding
            {
                Severity = SeverityMath.ForLoad(ctx, Severity.Low),
                Category = "I/O",
                Title = $"Disk writes are slow for {slowWrites.Count} tablespace(s)",
                Plain = "Saving changed data back to disk is slower than expected for some storage areas.",
                WhyItMatters = "Writes are done in the background, but if they fall behind, users eventually wait (free buffer waits, checkpoints).",
                Technical = "Av Writes(ms) > 20 for tablespaces with significant writes.",
                Evidence = slowWrites.Select(x => new Evidence(x.Ts, $"{N(x.Wr, 1)} ms avg write", $"{N(x.Writes)} writes")).ToList(),
                Recommendations = ["Check write latency at the storage layer and whether asynchronous I/O is enabled (FILESYSTEMIO_OPTIONS=SETALL on file systems)."],
                Docs = [D.IoDesign, D.Parameter("FILESYSTEMIO_OPTIONS")],
                SourceViews = ["DBA_HIST_FILESTATXS"],
                TableRefs = [t.Id]
            });
        }
    }

    /// <summary>
    /// One normalised row per tablespace. The 11.2.0.x layout of 'Tablespace IO Stats' prints its columns shifted: 'Writes' holds
    /// blocks per write, 'Writes avg/s' the write rate, and '1-bk Rds/s' the total of single-block reads. Detected by a write rate
    /// that is larger than the write count, and rebuilt as totals.
    /// </summary>
    internal static List<(string Ts, double Reads, double RdMs, double Writes, double WrMs)> TsIo(AnalysisContext ctx, AwrTable t)
    {
        var rows = new List<(string, double, double, double, double)>();
        var hasRate = t.Col("Writes avg/s", "Av Writes/s") >= 0;
        var data = t.DataRows.ToList();
        var shifted = hasRate && t.Col("1-bk Rds/s") >= 0 && data.Count(r => (r.Num("Writes avg/s", "Av Writes/s") ?? 0) > (r.Num("Writes") ?? 0) && (r.Num("Writes avg/s") ?? 0) > 5) >= Math.Max(1, data.Count / 3);
        foreach (var r in data)
        {
            var ts = r.Str("Tablespace");
            double reads, rd, writes;
            if (shifted)
            {
                var multi = r.Num("Reads") ?? 0;
                var single = r.Num("1-bk Rds/s") ?? 0;
                reads = multi + single;
                rd = reads > 0 ? (multi * (r.Num("Av Rd(ms)") ?? 0) + single * (r.Num("Av 1-bk Rd(ms)") ?? 0)) / reads : 0;
                writes = (r.Num("Writes avg/s") ?? 0) * (ctx.ElapsedSec ?? 0);
            }
            else
            {
                reads = r.Num("Reads") ?? 0;
                rd = r.Num("Av 1-bk Rd(ms)", "Av Rd(ms)", "Av Rd (ms)", "Av Rd") ?? 0;
                writes = r.Num("Writes") ?? 0;
            }
            var wr = r.Num("Av Writes(ms)", "Av Wt(ms)") ?? 0;
            rows.Add((ts, reads, rd, writes, wr));
        }
        return rows;
    }

    private static void TempUsage(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("Tablespace IO Stats");
        if (t == null) return;
        var norm = TsIo(ctx, t);
        var tempRows = norm.Where(r => r.Ts.StartsWith("TEMP", StringComparison.OrdinalIgnoreCase)).ToList();
        var total = norm.Sum(r => r.Reads + r.Writes);
        if (tempRows.Count == 0 || total <= 0) return;
        var tempIo = tempRows.Sum(r => r.Reads + r.Writes);
        var tempFile = ctx.Report.Table("IOStat by Filetype summary")?.RowByLabel("Temp File");
        var tempVolume = tempFile is { } tf ? $"{tf.Str("Reads: Data")} read, {tf.Str("Writes: Data")} written" : null;
        var tempRate = ctx.ElapsedSec is > 0 ? tempIo / ctx.ElapsedSec.Value : 0;
        if (tempIo / total < 0.2 || tempIo < 10000 || tempRate < 5) return;
        var tempWaitPct = (RuleHelpers.ForegroundEventPct(ctx, "direct path read temp") ?? 0) + (RuleHelpers.ForegroundEventPct(ctx, "direct path write temp") ?? 0);
        ctx.Add(new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, tempWaitPct >= 5 ? Severity.Medium : Severity.Low),
            Category = "Memory",
            Title = $"{P(tempIo / total * 100, 0)} of tablespace I/O goes to TEMP: sorts and joins are spilling to disk",
            Plain = "A large share of disk activity is temporary work data. Operations that should happen in memory (sorting, joining) are overflowing to disk.",
            WhyItMatters = "Disk-based sorts and hash joins can be many times slower than in-memory ones.",
            Technical = "High TEMP I/O indicates one-pass/multi-pass work areas (sorts, hash joins, hash group-by), but also global temporary tables, index builds and parallel query buffering, none of which are counted in 'In-memory Sort %'. Check PGA Memory Advisory, PGA Aggr Target Histogram and SQL with large sorts or hash joins.",
            Evidence = [new("TEMP reads+writes", N(tempIo), $"{N(tempRate, 1)} per second"), new("Temp file data volume", tempVolume ?? "n/a", "IOStat by Filetype summary"), new("All tablespaces reads+writes", N(total)), new("Temp direct path waits", P(tempWaitPct), "share of DB time")],
            Sql = [new("Statements that used the most temp space (ASH history)", "SELECT sql_id, ROUND(MAX(temp_space_allocated) / 1024 / 1024) AS max_temp_mb, ROUND(MAX(pga_allocated) / 1024 / 1024) AS max_pga_mb, COUNT(*) * 10 AS approx_seconds\nFROM   dba_hist_active_sess_history\nWHERE  snap_id BETWEEN &begin_snap AND &end_snap AND temp_space_allocated > 0\nGROUP  BY sql_id\nORDER  BY max_temp_mb DESC;", "Requires the Diagnostics Pack license. Add FETCH FIRST 15 ROWS ONLY (12c and later) to limit the output. The temp_space_allocated column exists from 11.2.")],
            Recommendations = ["Review the PGA Memory Advisory and increase PGA_AGGREGATE_TARGET if it predicts fewer disk spills.", "Tune SQL with large sorts / hash joins."],
            Docs = [D.Pga],
            SourceViews = ["DBA_HIST_TEMPSTATXS"],
            TableRefs = [t.Id],
            ChartRefs = ["tablespace-io"]
        });
    }

    private static AwrRow? Nearest(AwrTable t, string factorCol, double factor) =>
        t.DataRows.Where(r => r.Num(factorCol) is not null)
                  .OrderBy(r => Math.Abs(r.Num(factorCol)!.Value - factor))
                  .Cast<AwrRow?>().FirstOrDefault();

    private static void BufferCacheAdvisory(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("Buffer Pool Advisory");
        if (t == null || t.Rows.Count == 0) return;
        // Only the DEFAULT pool ("D") of the standard block size.
        var rows = t.DataRows.Where(r => r.Str("P") is "D" or "").ToList();
        if (rows.Count == 0) return;
        var cur = rows.OrderBy(r => Math.Abs((r.Num("Size Factor", "Size Factr") ?? 0) - 1)).First();
        var big = rows.Where(r => r.Num("Size Factor", "Size Factr") is >= 1.4 and <= 2.1).OrderByDescending(r => r.Num("Size Factor", "Size Factr")).FirstOrDefault();
        var curFactor = cur.Num("Est Phys Read Factor", "Estd Phys Read Factor") ?? 1;
        var physReadsPerSec = ctx.LoadPerSec("Physical read (blocks)", "Physical reads");
        var userIoPct = ctx.Report.Table("Foreground Wait Class", "Wait Classes by Total Wait Time")?.RowByLabel("User I/O")?.Num("% DB time", "%DB time") ?? 0;
        if (big.Cells == null) return;
        var bigFactor = big.Num("Est Phys Read Factor", "Estd Phys Read Factor") ?? curFactor;
        var reduction = curFactor > 0 ? (1 - bigFactor / curFactor) * 100 : 0;
        if (reduction >= 25 && userIoPct >= 10)
        {
            ctx.Add(new Finding
            {
                Severity = userIoPct >= 25 ? Severity.Medium : Severity.Low,
                Category = "Memory",
                Title = $"A larger buffer cache would cut disk reads by about {P(reduction, 0)}",
                Plain = $"The database's own simulation predicts that giving the data cache more memory ({big.Str("Size for Est (M)")} MB instead of {cur.Str("Size for Est (M)")} MB) would avoid about {P(reduction, 0)} of disk reads.",
                WhyItMatters = $"Disk reads account for {P(userIoPct)} of database time here, so fewer reads would directly speed up users.",
                Technical = "Buffer Pool Advisory (V$DB_CACHE_ADVICE / DBA_HIST_DB_CACHE_ADVICE). Est Phys Read Factor at the larger size compared with the current size. Make sure the host has free memory first.",
                Evidence = [new("Current size", cur.Str("Size for Est (M)") + " MB"), new("Suggested size", big.Str("Size for Est (M)") + " MB"), new("Estimated physical reads reduction", P(reduction, 0)), new("Physical reads per second now", N(physReadsPerSec, 0))],
                Recommendations = ["Increase SGA_TARGET (with ASMM) or DB_CACHE_SIZE, provided the server has free memory (see Memory Statistics).", "Tune the SQL with the highest physical reads first. It is often cheaper than adding memory."],
                Sql = [new("Cache advice now", "SELECT size_for_estimate AS size_mb, size_factor, estd_physical_read_factor, estd_physical_reads\nFROM   v$db_cache_advice\nWHERE  name = 'DEFAULT' AND advice_status = 'ON'\nORDER  BY size_for_estimate;")],
                Docs = [D.BufferCache, D.Sga],
                SourceViews = ["DBA_HIST_DB_CACHE_ADVICE"],
                TableRefs = [t.Id],
                ChartRefs = ["advisory-buffer"]
            });
        }
    }

    /// <summary>The advised size for ALTER SYSTEM ("2048M"): only digits from the report, else a substitution variable.</summary>
    private static string PgaSize(AwrRow? best)
    {
        if (best is not { } row) return "&new_size";
        var mb = row.Str("PGA Target Est (MB)").Replace(",", "", StringComparison.Ordinal);
        if (mb.Length == 0 || !mb.All(char.IsAsciiDigit)) // other number layouts ("2.048")
            mb = row.Num("PGA Target Est (MB)") is { } v && v is >= 1 and < 1e12 && v == Math.Floor(v)
                ? v.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "";
        return mb.Length > 0 ? mb + "M" : "&new_size";
    }

    private static void PgaAdvisory(AnalysisContext ctx)
    {
        var summary = ctx.Report.Table("PGA Aggr Summary");
        var hit = summary.FirstRow().Val("PGA Cache Hit %");
        var adv = ctx.Report.Table("PGA Memory Advisory");
        var hist = ctx.Report.Table("PGA Aggr Target Histogram");
        var onePass = hist?.DataRows.Sum(r => r.Num("1-Pass Execs") ?? 0) ?? 0;
        var multiPass = hist?.DataRows.Sum(r => r.Num("M-Pass Execs") ?? 0) ?? 0;

        AwrRow? cur = adv != null ? Nearest(adv, "Size Factr", 1.0) : null;
        var overalloc = cur?.Num("Estd PGA Overalloc Count") ?? 0;
        AwrRow? best = adv?.DataRows.Where(r => (r.Num("Estd PGA Overalloc Count") ?? 0) == 0 && r.Num("Size Factr") >= 1)
                         .OrderBy(r => r.Num("Size Factr")).Cast<AwrRow?>().FirstOrDefault();
        var curHit = cur?.Num("Estd PGA Cache Hit %");
        var twice = adv != null ? Nearest(adv, "Size Factr", 2.0) : null;
        var twiceHit = twice?.Num("Estd PGA Cache Hit %");

        if (hit is < 90 || overalloc > 0 || multiPass >= 10 || (twiceHit - curHit) >= 5)
        {
            var current = ctx.Param("pga_aggregate_target");
            ctx.Add(new Finding
            {
                Severity = SeverityMath.ForLoad(ctx, overalloc > 0 ? Severity.High : multiPass >= 10 || hit < 80 ? Severity.Medium : Severity.Low),
                Category = "Memory",
                Title = overalloc > 0 ? "PGA memory target is too small (Oracle had to over-allocate)" : $"Session work memory (PGA) is undersized: {P(hit)} of work done in memory",
                Plain = "Each session has private working memory (PGA) for sorting and joining. It is too small for this workload, so some operations spill to disk.",
                WhyItMatters = "Operations that spill to disk are slower and add I/O load for everyone.",
                Technical = $"PGA Cache Hit % {P(hit)}; Estd PGA Overalloc Count at current target {N(overalloc)}; one-pass executions {N(onePass)}, multi-pass {N(multiPass)}. Current PGA_AGGREGATE_TARGET = {current ?? "n/a"}.",
                Evidence =
                [
                    new("PGA cache hit %", P(hit)),
                    new("Over-allocation count at current size", N(overalloc)),
                    new("One-pass / multi-pass executions", $"{N(onePass)} / {N(multiPass)}"),
                    new("Smallest target without over-allocation", best is { } b ? b.Str("PGA Target Est (MB)") + " MB" : "n/a"),
                    new("Estimated hit % at 2x target", P(twiceHit))
                ],
                Recommendations = ["Increase PGA_AGGREGATE_TARGET to at least the smallest size the PGA Memory Advisory shows without over-allocation, provided host memory allows.", "Keep PGA_AGGREGATE_LIMIT comfortably above the target (default: max(2 GB, 2x target, 3 MB x PROCESSES))."],
                Sql = [new("Resize PGA (example)", $"ALTER SYSTEM SET pga_aggregate_target = {PgaSize(best)} SCOPE = BOTH;")],
                Docs = [D.Pga, D.Parameter("PGA_AGGREGATE_TARGET"), D.Parameter("PGA_AGGREGATE_LIMIT")],
                SourceViews = ["DBA_HIST_PGASTAT", "DBA_HIST_PGA_TARGET_ADVICE"],
                TableRefs = new[] { summary?.Id, adv?.Id, hist?.Id }.OfType<string>().ToList(),
                ChartRefs = ["advisory-pga"]
            });
        }
    }

    private static void SgaAdvisory(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("SGA Target Advisory");
        if (t == null || t.Rows.Count == 0) return;
        var cur = Nearest(t, "SGA Size Factor", 1.0);
        var bigger = t.DataRows.Where(r => r.Num("SGA Size Factor") is >= 1.4 and <= 1.6).Cast<AwrRow?>().FirstOrDefault() ?? Nearest(t, "SGA Size Factor", 1.5);
        if (cur is null || bigger is null) return;
        var curTime = cur.Value.Num("Est DB Time (s)");
        var bigTime = bigger.Value.Num("Est DB Time (s)");
        if (curTime is not > 0 || bigTime is null) return;
        var gain = (1 - bigTime.Value / curTime.Value) * 100;
        if (gain < 10) return;
        ctx.Add(new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, gain >= 25 ? Severity.Medium : Severity.Low),
            Category = "Memory",
            Title = $"Growing the SGA by 50% is estimated to reduce DB time by {P(gain, 0)}",
            Plain = "Oracle's built-in memory simulation predicts that giving the shared memory area more room would noticeably reduce the total time users spend waiting.",
            WhyItMatters = "Memory is often the cheapest performance improvement, if the server has it available.",
            Technical = $"SGA Target Advisory: Est DB Time {N(curTime)} s at factor 1.0 vs {N(bigTime)} s at factor {bigger.Value.Str("SGA Size Factor")} ({bigger.Value.Str("SGA Target Size (M)")} MB).",
            Evidence = [new("Current SGA target", cur.Value.Str("SGA Target Size (M)") + " MB"), new("Larger SGA target", bigger.Value.Str("SGA Target Size (M)") + " MB"), new("Estimated DB time reduction", P(gain, 0))],
            Recommendations = ["Increase SGA_TARGET (up to SGA_MAX_SIZE dynamically; beyond requires restart) if the host has free memory and HugePages are configured accordingly."],
            Sql = [new("SGA advice now", "SELECT sga_size, sga_size_factor, estd_db_time, estd_physical_reads FROM v$sga_target_advice ORDER BY sga_size;")],
            Docs = [D.Sga, D.MemoryAllocation, D.Parameter("SGA_TARGET")],
            SourceViews = ["DBA_HIST_SGA_TARGET_ADVICE"],
            TableRefs = [t.Id],
            ChartRefs = ["advisory-sga"]
        });
    }

    private static void SharedPool(AnalysisContext ctx)
    {
        var sp = ctx.Report.Table("Shared Pool Statistics");
        if (sp == null) return;
        var sqlExec = sp.RowByLabel("% SQL with executions>1");
        var pctReuse = sqlExec?.Num("End") ?? sqlExec?.NumAt(2);
        if (pctReuse is < 70)
        {
            ctx.Add(new Finding
            {
                Severity = SeverityMath.ForLoad(ctx, pctReuse < 40 ? Severity.Medium : Severity.Low),
                Category = "Parsing",
                Title = $"Only {P(pctReuse)} of cached SQL statements were run more than once",
                Plain = "Most statements in the SQL cache were used only once. This is typical of applications that put values directly into SQL text instead of using bind variables.",
                WhyItMatters = "Single-use statements waste shared memory and CPU (each one must be compiled).",
                Technical = "Shared Pool Statistics '% SQL with executions>1'. Corroborates literal SQL / hard parsing.",
                Evidence = [new("% SQL with executions>1", P(pctReuse))],
                Recommendations = ["Use bind variables in the application (see the parsing findings)."],
                Docs = [D.CursorSharing, D.SharedPool],
                SourceViews = ["DBA_HIST_SGASTAT", "DBA_HIST_SQLSTAT"],
                TableRefs = [sp.Id]
            });
        }

        var lib = ctx.Report.Table("Library Cache Activity");
        var sqlArea = lib?.RowByLabel("SQL AREA");
        // rule of thumb: reloads / pins should stay below 1%
        if (lib != null && sqlArea is { } sa && (sa.Num("Reloads") ?? 0) > 1000 && (sa.Num("Reloads") ?? 0) / Math.Max(1, sa.Num("Pin Requests") ?? 1) * 100 >= 1)
        {
            var reloads = sa.Num("Reloads");
            var inval = sa.Num("Invali- dations", "Invalidations");
            ctx.Add(new Finding
            {
                Severity = Severity.Low,
                Category = "Parsing",
                Title = $"Compiled SQL is being thrown out of memory and reloaded ({N(reloads)} reloads)",
                Plain = "Compiled statements had to be rebuilt because they were pushed out of memory or invalidated.",
                WhyItMatters = "Reloading costs CPU and can cause contention during busy periods.",
                Technical = $"Library Cache Activity SQL AREA: reloads {N(reloads)}, invalidations {N(inval)}. Reloads point to shared pool pressure; invalidations to DDL/statistics gathering.",
                Evidence = [new("SQL AREA reloads", N(reloads)), new("SQL AREA invalidations", N(inval))],
                Recommendations = ["Check the Shared Pool Advisory for sizing.", "Avoid DDL and statistics gathering on busy objects during peak hours."],
                Docs = [D.SharedPool],
                SourceViews = ["DBA_HIST_LIBRARYCACHE"],
                TableRefs = [lib.Id],
                ChartRefs = ["advisory-shared-pool"]
            });
        }
    }

    private static void HostMemory(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("Memory Statistics");
        var used = t?.RowByLabel("% Host Mem used for SGA+PGA");
        var pct = used?.Num("End") ?? used?.NumAt(2);
        var os = ctx.Report.Table("Operating System Statistics");
        var vmOut = os?.RowByLabel("VM_OUT_BYTES")?.NumAt(1);
        var vmIn = os?.RowByLabel("VM_IN_BYTES")?.NumAt(1);
        // VM_IN/OUT_BYTES are totals for the window; judge by rate. A trickle of swap-out is common (the OS parks idle pages).
        var pagingRate = ctx.ElapsedSec is > 0 ? ((vmOut ?? 0) + (vmIn ?? 0)) / ctx.ElapsedSec.Value : 0;
        var paging = pagingRate >= 0.1e6;

        if (pct is > 90 || paging)
        {
            ctx.Add(new Finding
            {
                Severity = SeverityMath.ForLoad(ctx, pagingRate >= 5e6 || pct > 95 ? Severity.High : pagingRate >= 1e6 || pct > 90 ? Severity.Medium : pct is < 60 ? Severity.Info : Severity.Low),
                Category = "Memory",
                Title = paging ? $"The server moved memory to and from disk (swap) at about {Bytes(pagingRate)}/s" : $"Database memory uses {P(pct)} of the server's RAM",
                Plain = paging
                    ? (pct is < 60 && pagingRate < 1e6
                        ? $"The operating system moved some memory pages to and from disk (swap) at a low rate. The database's own memory is only {P(pct, 0)} of the server's RAM, so this is probably idle pages being parked or another program; it matters only if database memory is being swapped out, which makes everything dramatically slower."
                        : "The operating system ran short of memory and moved some of it to disk (swap). When the database's own memory is swapped out, everything becomes dramatically slower.")
                    : "The database's memory areas take up almost all of the server's RAM, leaving little for the operating system and other processes.",
                WhyItMatters = "Memory pressure on the host can cause severe, unpredictable slowdowns and even instance crashes.",
                Technical = $"% Host Mem used for SGA+PGA = {P(pct)}; VM_IN_BYTES {Bytes(vmIn)}, VM_OUT_BYTES {Bytes(vmOut)} (DBA_HIST_OSSTAT). Confirm with vmstat si/so on the server, because swap activity can also come from other processes. On Linux use HugePages for the SGA (USE_LARGE_PAGES=ONLY) and avoid MEMORY_TARGET with large SGAs.",
                Evidence = [new("% host memory used by SGA+PGA", P(pct)), new("Paged in (total)", Bytes(vmIn)), new("Paged out (total)", Bytes(vmOut)), new("Paging rate", Bytes(pagingRate) + "/s")],
                Recommendations = ["Reduce SGA/PGA or add RAM so that SGA+PGA stay well below physical memory (leave room for the OS and processes).", "Configure HugePages for the SGA on Linux.", "Set PGA_AGGREGATE_LIMIT to cap runaway PGA usage."],
                Docs = [D.MemoryAllocation, D.OsResources, D.Parameter("USE_LARGE_PAGES")],
                SourceViews = ["DBA_HIST_OSSTAT", "DBA_HIST_PGASTAT"],
                TableRefs = new[] { t?.Id, os?.Id }.OfType<string>().ToList()
            });
        }
    }
}
