// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Core.Knowledge;
using Apodofos.Core.Model;
using static Apodofos.Core.Analysis.AnalysisContext;
using static Apodofos.Core.Analysis.Rules.RuleHelpers;
using D = Apodofos.Core.Knowledge.OracleDocs;

namespace Apodofos.Core.Analysis.Rules;

/// <summary>
/// What a Statspack report can say about itself: how much the snapshots captured (level 5 stores SQL, level 7 adds segment
/// statistics), whether timings were valid and whether the interesting SQL fell below the capture thresholds.
/// </summary>
public sealed class StatspackSnapshotRules : IRule
{
    public string Name => "Statspack snapshot quality";
    public ReportKind[] Kinds => [ReportKind.Statspack];

    private static readonly SqlSnippet SetLevel = new("Take the next snapshot at level 7 and make 7 the default",
        """
        -- Run as PERFSTAT (or a user with EXECUTE on PERFSTAT.STATSPACK)
        EXEC STATSPACK.SNAP(i_snap_level => 7);
        EXEC STATSPACK.MODIFY_STATSPACK_PARAMETER(i_snap_level => 7, i_modify_parameter => 'true');
        SELECT snap_id, snap_level, TO_CHAR(snap_time, 'YYYY-MM-DD HH24:MI') AS snap_time
        FROM   perfstat.stats$snapshot
        ORDER  BY snap_id DESC;
        """, "Level 5 is the default and stores SQL; level 7 adds segment statistics. Higher levels cost slightly more per snapshot but are normally negligible.");

    public void Evaluate(AnalysisContext ctx)
    {
        var r = ctx.Report;
        var capture = r.Table("SQL Capture Summary");
        var sqlTables = r.Tables.Where(t => t.TitleStartsWith("SQL ordered by") && t.Rows.Count > 0).ToList();
        var light = SeverityMath.LightLoad(ctx);

        // 1. No SQL sections at all: snapshot level below 5 (or a report generated without them).
        if (capture == null && sqlTables.Count == 0)
        {
            ctx.Add(new Finding
            {
                Severity = light ? Severity.Low : Severity.Medium,
                Category = "Statspack",
                Title = "This Statspack report has no SQL sections: the snapshots were taken at a level that does not store SQL",
                Plain = "The report shows the totals (waits, load, memory) but not which SQL statements caused them. Statspack only stores SQL when the snapshot level is 5 or higher, and these snapshots were taken at a lower level.",
                WhyItMatters = "Most performance problems end in a handful of SQL statements. Without them the analysis can say what the database is waiting for, but not who is responsible, so fixes are guesswork.",
                Technical = "None of the 'SQL ordered by ...' sections (STATS$SQL_SUMMARY, STATS$SQLTEXT) is present. Snapshot level 0 stores general statistics only, level 5 adds SQL statements above the thresholds, level 6 adds SQL plans and level 7 adds segment statistics.",
                Evidence = [new("SQL sections found", "none"), new("Elapsed time analysed", Dur(ctx.ElapsedSec))],
                Recommendations =
                [
                    "Take snapshots at level 5 or better level 7 (STATSPACK.SNAP with i_snap_level), and make that the default with MODIFY_STATSPACK_PARAMETER.",
                    "Re-run the workload (or wait for the next busy period), then generate a new report with spreport.sql between two level 7 snapshots.",
                    "If the problem is happening now, take a level 7 snapshot before and after a 10 to 15 minute window."
                ],
                Sql = [SetLevel],
                Docs = [D.StatspackGuide],
                SourceViews = ["STATS$SNAPSHOT", "STATS$SQL_SUMMARY"],
                TableRefs = []
            });
        }

        // 2. SQL was captured but covers little of the load: the thresholds hide the statements that matter.
        if (capture != null)
        {
            var worst = capture.DataRows
                .Select(row => (Section: row.Str("Section"), Pct: row.Num("Captured SQL %")))
                .Where(x => x.Pct is not null && (x.Section.Contains("Elapsed", StringComparison.OrdinalIgnoreCase) || x.Section.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                                                    || x.Section.Contains("Gets", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => x.Pct).FirstOrDefault();
            if (worst.Pct is < 50 && !light)
            {
                ctx.Add(new Finding
                {
                    Severity = Severity.Low,
                    Category = "Statspack",
                    Title = $"The SQL listed covers only {P(worst.Pct, 0)} of the work ('{worst.Section}')",
                    Plain = $"The statements printed in this report add up to just {P(worst.Pct, 0)} of the total. The rest is spread over statements that were too small, individually, to be stored, or that left the shared pool before a snapshot was taken.",
                    WhyItMatters = "If the load comes from thousands of similar statements (typically SQL with literals instead of bind variables), no single statement stands out and tuning the listed ones will not help much.",
                    Technical = $"'Captured SQL accounts for' {P(worst.Pct)} in '{worst.Section}'. Statspack stores a statement only when it exceeds a snapshot threshold (executions, parse calls, disk reads, buffer gets, sharable memory, version count). Compare with the hard-parse ratio and the literal-SQL finding.",
                    Evidence = [new("Section", worst.Section), new("Captured SQL", P(worst.Pct, 0))],
                    Recommendations =
                    [
                        "Check for statements that differ only in literals (the Parse Calls and Hard Parses figures in the Load Profile point at this).",
                        "If the workload is a few heavy statements, lower the thresholds (i_buffer_gets_th, i_disk_reads_th, i_executions_th) so more of them are stored, and compare the next report.",
                        "Take snapshots more often (every 15 to 30 minutes) so statements are captured before they age out of the shared pool."
                    ],
                    Sql =
                    [
                        new("Lower the SQL capture thresholds",
                            """
                            -- Defaults: executions 100, parse calls 1000, disk reads 1000, buffer gets 10000
                            EXEC STATSPACK.MODIFY_STATSPACK_PARAMETER(i_executions_th => 50, i_disk_reads_th => 500, i_buffer_gets_th => 5000, i_modify_parameter => 'true');
                            SELECT executions_th, parse_calls_th, disk_reads_th, buffer_gets_th, snap_level
                            FROM   perfstat.stats$statspack_parameter;
                            """, "Lower thresholds store more rows per snapshot, so the PERFSTAT tablespace grows faster. Purge old snapshots with sppurge.sql.")
                    ],
                    Docs = [D.StatspackGuide],
                    SourceViews = ["STATS$STATSPACK_PARAMETER", "STATS$SQL_SUMMARY"],
                    TableRefs = [capture.Id]
                });
            }
        }

        // 3. Segment sections missing while the waits are about segments.
        if (!r.Tables.Any(t => t.TitleStartsWith("Segments by")))
        {
            var segWaits = new[] { "buffer busy waits", "read by other session", "latch: cache buffers chains", "enq: TX - row lock contention", "enq: TX - index contention", "latch free" };
            var fg = r.Table("Foreground Wait Events");
            var hits = fg?.DataRows
                .Select(row => (Event: row.Str("Event"), Pct: row.Num("% DB time") ?? 0))
                .Where(x => x.Pct >= 3 && segWaits.Any(w => x.Event.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(x => x.Pct).ToList() ?? [];
            if (hits.Count > 0)
            {
                ctx.Add(new Finding
                {
                    Severity = light ? Severity.Info : Severity.Low,
                    Category = "Statspack",
                    Title = "These waits point at hot objects, but the report has no segment statistics (snapshot level below 7)",
                    Plain = $"The database lost time on {string.Join(", ", hits.Select(h => "'" + h.Event + "'"))}. These waits are usually caused by one or two tables or indexes, and Statspack can name them, but only for snapshots taken at level 7.",
                    WhyItMatters = "Knowing the object turns a vague 'contention' into a concrete fix (a different index, partitioning, a hash-partitioned sequence key).",
                    Technical = "No 'Segments by ...' sections (STATS$SEG_STAT) in this report. They are stored from snapshot level 7. Until then use V$SEGMENT_STATISTICS right now, while the problem is happening.",
                    Evidence = hits.Select(h => new Evidence(h.Event, P(h.Pct))).ToList(),
                    Recommendations =
                    [
                        "Take snapshots at level 7 for the next busy period and regenerate the report.",
                        "To look at the current situation immediately, query V$SEGMENT_STATISTICS (below)."
                    ],
                    Sql =
                    [
                        SetLevel,
                        new("Hottest segments since instance startup (current, not from the report)",
                            """
                            SELECT *
                            FROM  (SELECT owner, object_name, statistic_name, value
                                   FROM   v$segment_statistics
                                   WHERE  statistic_name IN ('logical reads', 'buffer busy waits', 'physical reads', 'row lock waits', 'ITL waits')
                                   ORDER  BY value DESC)
                            WHERE ROWNUM <= 20;
                            """, "Values are cumulative since startup; run it twice a few minutes apart to see the current rate.")
                    ],
                    Docs = [D.StatspackGuide],
                    SourceViews = ["STATS$SEG_STAT"],
                    TableRefs = fg != null ? [fg.Id] : []
                });
            }
        }

        // 4. timed_statistics off: all times are zero or meaningless.
        var timedOff = (Param(ctx, "timed_statistics") ?? "").Equals("FALSE", StringComparison.OrdinalIgnoreCase)
                       || r.Table("Statspack Warnings")?.Rows.Any(w => w.Length > 0 && w[0].Contains("timed_statistics", StringComparison.OrdinalIgnoreCase) && w[0].Contains("FALSE", StringComparison.OrdinalIgnoreCase)) == true;
        if (timedOff)
        {
            ctx.Add(new Finding
            {
                Severity = Severity.High,
                Category = "Statspack",
                Title = "Timing statistics were switched off, so the wait times in this report cannot be trusted",
                Plain = "The database was not measuring time while these snapshots were taken. The report can count how often things happened, but every 'time waited' figure is missing or wrong, so it cannot say where time went.",
                WhyItMatters = "Performance tuning is about time. Without timings the analysis would blame the most frequent events, which are often harmless.",
                Technical = "TIMED_STATISTICS=FALSE (or Statspack printed its 'TIMINGS ARE INVALID' warning). Waits are then ranked by count, and CPU, DB time and elapsed columns are zero. The parameter has been TRUE by default since STATISTICS_LEVEL=TYPICAL (10g).",
                Evidence = [new("timed_statistics", Param(ctx, "timed_statistics") ?? "warning in report")],
                Recommendations = ["Set TIMED_STATISTICS=TRUE (dynamic) and make sure STATISTICS_LEVEL is TYPICAL, then take new snapshots; old ones cannot be repaired.", "Do not make decisions from this report's times; use the counts and ratios only."],
                Sql = [new("Enable timing statistics", "ALTER SYSTEM SET timed_statistics = TRUE;\nSELECT name, value FROM v$parameter WHERE name IN ('timed_statistics', 'statistics_level');")],
                Docs = [D.Parameter("TIMED_STATISTICS"), D.StatspackGuide],
                SourceViews = ["STATS$PARAMETER"],
                TableRefs = []
            });
        }

        // 5. Idle events in the Top 5 (releases before 11g count some idle waits).
        var idle = r.Table("Top 5 Idle Events (excluded)");
        if (idle is { Rows.Count: > 0 })
        {
            var names = string.Join(", ", idle.DataRows.Select(x => "'" + x.Str("Event") + "'"));
            ctx.Add(new Finding
            {
                Severity = Severity.Info,
                Category = "Statspack",
                Title = "The report's Top 5 list contains idle waits, which were ignored",
                Plain = $"Statspack listed {names} among its top events. These are waits where a background process or an idle connection is simply sleeping or waiting for work; they are not a problem.",
                WhyItMatters = "It is a common mistake to chase an idle event at the top of an older Statspack report. Ignoring it keeps the analysis on the real consumers of time.",
                Technical = "Statspack 9i/10g compare event time with elapsed time and may include events that STATS$IDLE_EVENT does not cover (for example dispatcher or Streams waits on a quiet system). They were moved to the 'Top 5 Idle Events (excluded)' table.",
                Evidence = idle.DataRows.Select(x => new Evidence(x.Str("Event"), N(x.Num("Time (s)")) + " s")).ToList(),
                Recommendations = ["Read the next non-idle events instead; the findings above already do."],
                Docs = [D.WaitEvents],
                SourceViews = ["STATS$SYSTEM_EVENT", "STATS$IDLE_EVENT"],
                TableRefs = [idle.Id]
            });
        }
    }
}

/// <summary>Events whose Statspack name is coarser than in AWR (latch free, buffer busy waits, enqueue) and a short "read the Top 5" guide.</summary>
public sealed class StatspackWaitRules : IRule
{
    public string Name => "Statspack wait events";
    public ReportKind[] Kinds => [ReportKind.Statspack];

    public void Evaluate(AnalysisContext ctx)
    {
        var fg = ctx.Report.Table("Foreground Wait Events");
        if (fg == null) return;
        LegacyLatch(ctx, fg);
        LegacyBufferBusy(ctx, fg);
        LegacyEnqueue(ctx, fg);
        TopFiveGuide(ctx);
    }

    private static double Pct(AwrTable fg, string eventName) =>
        fg.RowByLabel(eventName).Val("% DB time") ?? 0;

        /// <summary>Adds the finding, or folds its extra detail into the shared rule's finding for the same event (one card per event).</summary>
    private static void AddOrMerge(AnalysisContext ctx, string eventName, Finding f)
    {
        var existing = ctx.Findings.FirstOrDefault(x => x.Category == "Wait events" && x.Title.Contains($"'{eventName}'", StringComparison.OrdinalIgnoreCase));
        if (existing == null) { ctx.Add(f); return; }
        existing.Evidence.AddRange(f.Evidence.Skip(1));
        existing.Technical = (existing.Technical + " " + f.Technical).Trim();
        existing.Recommendations.InsertRange(0, f.Recommendations.Where(r => !existing.Recommendations.Contains(r)));
        existing.Sql.AddRange(f.Sql.Select(s => s with { Code = ctx.Sub(s.Code).Trim() }));
        existing.Docs.AddRange(f.Docs.Where(d => existing.Docs.All(x => x.Url != d.Url)));
        existing.TableRefs.AddRange(f.TableRefs.Except(existing.TableRefs));
        existing.ChartRefs.AddRange(f.ChartRefs.Except(existing.ChartRefs));
        existing.SourceViews.AddRange(f.SourceViews.Except(existing.SourceViews));
    }

private static void LegacyLatch(AnalysisContext ctx, AwrTable fg)
    {
        var pct = Pct(fg, "latch free");
        if (pct < 2) return;
        var sleeps = ctx.Report.Table("Latch Sleep Breakdown");
        var top = sleeps?.DataRows.OrderByDescending(r => r.Num("Sleeps") ?? 0).Take(3).Where(r => (r.Num("Sleeps") ?? 0) > 0).ToList() ?? [];
        var names = top.Select(r => r.Str("Latch Name")).ToList();
        AddOrMerge(ctx, "latch free", new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, SeverityMath.FromDbTimePct(pct)),
            Category = "Statspack",
            Impact = pct,
            Title = $"'latch free' took {P(pct)} of database time" + (names.Count > 0 ? $", mostly the '{names[0]}' latch" : ""),
            Plain = "Sessions queued for an internal memory lock (a latch) that protects a shared structure. Releases before Oracle 10g do not say which latch in the wait event name; the 'Latch Sleep Breakdown' section does.",
            WhyItMatters = "Latch queues grow quickly under load: the more sessions wait, the more CPU they burn spinning, and response time degrades faster than the load increases.",
            Technical = "Wait event 'latch free' (the pre-10g name; 10g and later report 'latch: <name>'). Rank the latches by sleeps in 'Latch Sleep Breakdown' (V$LATCH) and use 'Latch Miss Sources' for the code path. Typical culprits: cache buffers chains (hot blocks, excessive logical reads), shared pool and library cache (hard parsing, no bind variables), redo allocation (very small commits).",
            Evidence = top.Select(r => new Evidence("Latch " + r.Str("Latch Name"), N(r.Num("Sleeps")) + " sleeps", $"{N(r.Num("Misses"))} misses of {N(r.Num("Get Requests"))} requests")).Prepend(new Evidence("Share of database time", P(pct))).ToList(),
            Recommendations =
            [
                names.Count > 0 && names[0].Contains("cache buffers chains", StringComparison.OrdinalIgnoreCase) ? "Reduce logical reads of the hottest statements ('SQL ordered by Gets'); hot index root/leaf blocks and small, frequently scanned tables are typical." :
                names.Count > 0 && (names[0].Contains("shared pool", StringComparison.OrdinalIgnoreCase) || names[0].Contains("library cache", StringComparison.OrdinalIgnoreCase)) ? "Use bind variables and avoid hard parsing (see the parse findings); consider CURSOR_SHARING=FORCE as a stop-gap." :
                "Find which latch dominates in 'Latch Sleep Breakdown' and attack the workload that drives it (logical reads, parsing, commits).",
                "Compare 'Latch Hit %' in the Instance Efficiency section and the sleeps per miss; many sleeps per miss means long holders, not just frequent requests."
            ],
            Sql =
            [
                new("Latches ranked by sleeps (current instance)",
                    """
                    SELECT name, gets, misses, sleeps, spin_gets
                    FROM   v$latch
                    WHERE  sleeps > 0
                    ORDER  BY sleeps DESC;
                    """, "Cumulative since startup. For history use STATS$LATCH joined to STATS$SNAPSHOT.")
            ],
            Docs = [D.VLatch, D.WaitEvents],
            SourceViews = ["STATS$SYSTEM_EVENT", "STATS$LATCH"],
            TableRefs = new[] { fg.Id, sleeps?.Id }.OfType<string>().ToList(),
            ChartRefs = ["fg-events", "latch-sleeps"]
        });
    }

    private static void LegacyBufferBusy(AnalysisContext ctx, AwrTable fg)
    {
        var pct = Pct(fg, "buffer busy waits");
        if (pct < 2) return;
        var ws = ctx.Report.Table("Buffer Wait Statistics");
        var top = ws?.DataRows.OrderByDescending(r => r.Num("Total Wait Time (s)", "Waits") ?? 0).FirstOrDefault();
        var cls = top?.Label ?? "";
        var hasClass = cls.Length > 0;
        AddOrMerge(ctx, "buffer busy waits", new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, SeverityMath.FromDbTimePct(pct)),
            Category = "Statspack",
            Impact = pct,
            Title = $"'buffer busy waits' took {P(pct)} of database time" + (hasClass ? $" (block class '{cls}')" : ""),
            Plain = "Sessions waited because another session was using the same data block. In this Statspack release the wait event does not say what kind of block it was, so the 'Buffer Wait Statistics' section is needed to tell.",
            WhyItMatters = "Everybody touching the same hot block is serialised behind one session, which caps throughput no matter how powerful the server is.",
            Technical = "Buffer busy waits (the single wait event before 10g; later releases split read waits into 'read by other session'). The class with the most wait time is the clue: data block (hot table or index leaf, freelists, missing ASSM), segment header (extent allocation, freelist contention), undo header or undo block (too few undo segments, long transactions).",
            Evidence = new[] { new Evidence("Share of database time", P(pct)) }
                .Concat(ws?.DataRows.Take(3).Select(r => new Evidence("Class " + r.Label, N(r.Num("Waits")) + " waits", $"{N(r.Num("Total Wait Time (s)"))} s")) ?? []).ToList(),
            Recommendations =
            [
                !hasClass ? "Look at 'Buffer Wait Statistics' to see which block class is involved." :
                cls.Contains("data block", StringComparison.OrdinalIgnoreCase) ? "Find the hot segment (Segments by Buffer Busy Waits needs snapshot level 7); for inserts use ASSM tablespaces, hash-partition the table or reverse/hash the index, and cache sequence values." :
                cls.Contains("undo", StringComparison.OrdinalIgnoreCase) ? "Use automatic undo management and enough undo; shorten large transactions." :
                "Check extent sizes and free list settings of the busiest segments; move to locally managed ASSM tablespaces.",
                "Reduce concurrent access to the same blocks (batch window, smaller commits, spreading rows)."
            ],
            Sql = [new("Buffer waits by class (current instance)", "SELECT class, count, time FROM v$waitstat ORDER BY time DESC;", "Cumulative since startup; time is in centiseconds.")],
            Docs = [D.VWaitStat, D.WaitEvents],
            SourceViews = ["STATS$SYSTEM_EVENT", "STATS$WAITSTAT"],
            TableRefs = new[] { fg.Id, ws?.Id }.OfType<string>().ToList(),
            ChartRefs = ["fg-events", "buffer-waits"]
        });
    }

    private static void LegacyEnqueue(AnalysisContext ctx, AwrTable fg)
    {
        var pct = Pct(fg, "enqueue");
        if (pct < 2) return;
        var eq = ctx.Report.Table("Enqueue Activity");
        var top = eq?.DataRows.OrderByDescending(r => r.Num("Wt Time (s)") ?? 0).FirstOrDefault();
        var name = top?.Label ?? "";
        AddOrMerge(ctx, "enqueue", new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, SeverityMath.FromDbTimePct(pct)),
            Category = "Statspack",
            Impact = pct,
            Title = $"'enqueue' (lock) waits took {P(pct)} of database time" + (name.Length > 0 ? $", mostly type {name}" : ""),
            Plain = "Sessions waited for a lock held by another session. Before Oracle 10g all lock waits are reported under one name, 'enqueue'; the 'Enqueue Activity' section shows which kind of lock it was.",
            WhyItMatters = "A lock wait means a user is blocked until someone else commits or rolls back, which feels like the application hanging.",
            Technical = "Wait event 'enqueue' (10g+ names them 'enq: XX - reason'). Common types: TX (row lock, ITL, index/bitmap contention), TM (table lock from unindexed foreign keys), ST (space transactions in dictionary-managed tablespaces), HW (high-water mark), SQ (sequences with small CACHE).",
            Evidence = new[] { new Evidence("Share of database time", P(pct)) }.Concat(eq?.DataRows.OrderByDescending(r => r.Num("Wt Time (s)") ?? 0).Take(3).Select(r => new Evidence("Enqueue " + r.Label, N(r.Num("Wt Time (s)")) + " s", $"{N(r.Num("Waits"))} waits")) ?? []).ToList(),
            Recommendations =
            [
                name.StartsWith("TX", StringComparison.OrdinalIgnoreCase) ? "Row-lock waits come from the application: find the blocking sessions (query below) and shorten the transactions or lock in a consistent order." :
                name.StartsWith("TM", StringComparison.OrdinalIgnoreCase) ? "Index the foreign key columns of the child tables involved." :
                name.StartsWith("SQ", StringComparison.OrdinalIgnoreCase) ? "Raise the CACHE size of the busy sequences." :
                "Identify the enqueue type in 'Enqueue Activity' and check the matching cause in the Database Reference description of that lock.",
                "Check for sessions that stay in a transaction without committing."
            ],
            Sql =
            [
                new("Who is blocking whom right now",
                    """
                    SELECT s.sid, s.serial#, s.username, s.program, s.blocking_session, s.event, s.seconds_in_wait
                    FROM   v$session s
                    WHERE  s.blocking_session IS NOT NULL
                    ORDER  BY s.seconds_in_wait DESC;
                    """, "Shows the present state only; it will be empty if the blocking has already ended.")
            ],
            Docs = [D.Concurrency, D.WaitEvents],
            SourceViews = ["STATS$SYSTEM_EVENT", "STATS$ENQUEUE_STATISTICS"],
            TableRefs = new[] { fg.Id, eq?.Id }.OfType<string>().ToList(),
            ChartRefs = ["fg-events", "enqueues"]
        });
    }

    /// <summary>The Statspack habit: read the Top 5 events first, then go to the section they point at.</summary>
    private static void TopFiveGuide(AnalysisContext ctx)
    {
        var top = ctx.Report.TableStartingWith("Top 5 Timed");
        if (top == null || top.Rows.Count == 0) return;
        var rows = top.DataRows.Select(r => (Name: r.Str("Event"), Pct: r.Num("% DB time") ?? 0)).Where(x => x.Name.Length > 0).ToList();
        if (rows.Count == 0) return;
        string Next(string e) => e.ToLowerInvariant() switch
        {
            var x when x.StartsWith("db file scattered") => "full table or index fast-full scans: read 'SQL ordered by Reads' and 'Gets'",
            var x when x.StartsWith("db file sequential") => "index and single-block access: read 'SQL ordered by Reads' and 'Tablespace IO Stats' for slow files",
            "db cpu" => "CPU-heavy SQL: read 'SQL ordered by CPU' and 'Gets'",
            var x when x.StartsWith("latch") => "'Latch Sleep Breakdown' and 'Latch Miss Sources'",
            var x when x.StartsWith("log file") => "redo: 'Instance Activity Stats' (redo size, redo writes) and the redo log findings",
            var x when x.StartsWith("buffer busy") || x.StartsWith("read by other") => "'Buffer Wait Statistics' and the segment sections",
            var x when x.StartsWith("enq") || x == "enqueue" => "'Enqueue Activity'",
            var x when x.StartsWith("direct path") => "sorts and parallel scans: 'PGA Aggr Target Histogram' and temp usage",
            var x when x.StartsWith("free buffer") => "the DBWR / buffer cache findings",
            _ => "the matching event finding"
        };
        var parts = rows.Take(3).Select(x => $"{x.Name} ({P(x.Pct, 0)}) leads to {Next(x.Name)}").ToList();
        ctx.Add(new Finding
        {
            Severity = Severity.Info,
            Category = "Statspack",
            Title = "How to read this report: the top events and the section each one points to",
            Plain = "A Statspack report is read from the top five timed events downwards: whatever takes the most time decides which section to study next. " + string.Join("; ", parts) + ".",
            WhyItMatters = "Following the biggest time consumer first avoids spending days on tuning that cannot change the response time users see.",
            Technical = "Top 5 Timed Events are ranked by time (CPU time, wait time) as a share of total call time; DB time, when printed, is the 100% reference. Events that are idle waits are removed before the ranking.",
            Evidence = rows.Select(x => new Evidence(x.Name, P(x.Pct, 1))).ToList(),
            Recommendations = ["Start with the first non-idle event and its section; revisit the Top 5 after each change."],
            Docs = [D.StatspackGuide, D.PerformanceMethod],
            SourceViews = ["STATS$SYSTEM_EVENT"],
            TableRefs = [top.Id],
            ChartRefs = ["top-events"]
        });
    }
}

/// <summary>Licensing and housekeeping notes that only make sense for Statspack (free alternative to AWR/ASH on Standard Edition).</summary>
public sealed class StatspackEditionRules : IRule
{
    public string Name => "Statspack usage and licensing";
    public ReportKind[] Kinds => [ReportKind.Statspack];

    public void Evaluate(AnalysisContext ctx)
    {
        var rel = ctx.Info.Release;
        ctx.Add(new Finding
        {
            Severity = Severity.Info,
            Category = "Statspack",
            Title = "Statspack is the free alternative to AWR and ASH: how to get the most from it",
            Plain = "This report comes from Statspack. AWR and ASH reports need the paid Diagnostics Pack on Enterprise Edition and do not exist on Standard Edition. Statspack needs no extra licence and works on every edition, so it is the right tool here; it just needs a little setup to be as informative.",
            WhyItMatters = "Good snapshots taken before a problem happens are what make a report useful. Reports from badly configured snapshots (wrong level, intervals of many hours, no history) show averages that hide the real incident.",
            Technical = "AWR/ASH/ADDM (DBA_HIST_*, V$ACTIVE_SESSION_HISTORY) require the Oracle Diagnostics Pack and are Enterprise Edition features; setting CONTROL_MANAGEMENT_PACK_ACCESS=NONE disables them. Statspack stores its data in the PERFSTAT schema (STATS$* tables) and has no ASH sampling, so intervals should be short around incidents. " +
                        (rel.Length > 0 ? $"This report is from release {rel}." : ""),
            Evidence = [new("Snapshot interval", Dur(ctx.ElapsedSec)), new("Snapshots", $"{ctx.Info.BeginSnapId} to {ctx.Info.EndSnapId}")],
            Recommendations =
            [
                "Take snapshots every 15 to 30 minutes at level 7 (spauto.sql schedules this with DBMS_JOB), and keep at least two weeks of history.",
                "Compare the report of a bad period with the same period on a normal day (spreport.sql with different snapshot pairs).",
                "For one statement, run sprepsql.sql with its hash value to see its plan and per-snapshot statistics.",
                "Purge old snapshots regularly (sppurge.sql, or STATSPACK.PURGE) to keep the PERFSTAT tablespace small.",
                "If this database is Enterprise Edition and you own the Diagnostics Pack, AWR/ASH give more detail, but only use them if licensed."
            ],
            Sql =
            [
                new("Take a level 7 snapshot now",
                    """
                    EXEC STATSPACK.SNAP(i_snap_level => 7);
                    """, "Run as PERFSTAT. Take one before and one after the period you want to analyse."),
                new("List snapshots and their levels",
                    """
                    SELECT snap_id, snap_level, TO_CHAR(snap_time, 'YYYY-MM-DD HH24:MI:SS') AS snap_time
                    FROM   perfstat.stats$snapshot
                    ORDER  BY snap_id DESC;
                    """),
                new("Generate the reports (SQL*Plus)",
                    """
                    -- Instance report between two snapshots
                    @?/rdbms/admin/spreport.sql
                    -- One SQL statement (asks for the hash value)
                    @?/rdbms/admin/sprepsql.sql
                    -- Schedule hourly snapshots
                    @?/rdbms/admin/spauto.sql
                    """, "spauto.sql uses DBMS_JOB, so JOB_QUEUE_PROCESSES must be greater than 0.")
            ],
            Docs = [D.StatspackGuide, D.Licensing, D.ControlManagementPackAccess],
            SourceViews = ["STATS$SNAPSHOT", "STATS$STATSPACK_PARAMETER"],
            TableRefs = []
        });

        if (ctx.Info.MajorVersion is { } v && v is > 0 and < 12)
        {
            ctx.Add(new Finding
            {
                Severity = Severity.Info,
                Category = "Statspack",
                Title = $"Oracle {rel} is a very old release: some sections and advice are limited",
                Plain = $"The database runs Oracle {rel}, which is several releases older than 19c, the release this analyzer is calibrated for. Older releases print fewer details in Statspack (for example no wait classes before 11g and no time model before 10g) and no longer receive fixes.",
                WhyItMatters = "Unsupported releases do not get security or bug fixes, and newer Oracle versions include many performance improvements that no tuning of the old one can match.",
                Technical = "Statspack layouts differ by release; this analysis maps older names (CPU time, latch free, buffer busy waits, Old Hash Value) to their modern equivalents and skips what the release does not print. Check Oracle's support lifecycle for the exact dates.",
                Evidence = [new("Release", rel)],
                Recommendations = ["Plan an upgrade to a supported release (19c or later; check Oracle's published support lifecycle)."],
                Docs = [D.Upgrade],
                SourceViews = [],
                TableRefs = []
            });
        }
    }
}
