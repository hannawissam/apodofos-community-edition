// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using QuietLoad.Core.Knowledge;
using QuietLoad.Core.Model;
using static QuietLoad.Core.Analysis.AnalysisContext;
using static QuietLoad.Core.Analysis.Rules.RuleHelpers;
using D = QuietLoad.Core.Knowledge.OracleDocs;

namespace QuietLoad.Core.Analysis.Rules;

/// <summary>
/// Oracle Performance Method: start with what consumes the most DB time. Evaluates the "Top Foreground Events"
/// (DBA_HIST_SYSTEM_EVENT + time model), the wait-class breakdown and background I/O latency.
/// </summary>
public sealed class WaitEventRules : IRule
{
    public string Name => "Wait events";

    // Which segment-statistics section explains which wait (DBA_HIST_SEG_STAT).
    private static readonly (string Event, string SegmentTable)[] SegmentMap =
    [
        ("buffer busy waits", "Segments by Buffer Busy Waits"),
        ("read by other session", "Segments by Physical Reads"),
        ("enq: TX - row lock contention", "Segments by Row Lock Waits"),
        ("enq: TX - allocate ITL entry", "Segments by ITL Waits"),
        ("enq: TX - index contention", "Segments by Buffer Busy Waits"),
        ("latch: cache buffers chains", "Segments by Logical Reads"),
        ("db file sequential read", "Segments by Physical Read Requests"),
        ("db file scattered read", "Segments by Table Scans"),
        ("direct path read", "Segments by Direct Physical Reads"),
        ("direct path write", "Segments by Direct Physical Writes"),
        ("gc buffer busy", "Segments by Global Cache Buffer Busy"),
        ("gc cr", "Segments by CR Blocks Received"),
        ("gc current", "Segments by Current Blocks Received"),
    ];

    public void Evaluate(AnalysisContext ctx)
    {
        var top = ctx.Report.TableStartingWith("Top 10 Foreground Events", "Top 5 Timed Foreground Events", "Top 5 Timed Events", "Top Timed Events");
        if (top != null) EvaluateTopEvents(ctx, top);
        EvaluateWaitClasses(ctx);
        EvaluateBackground(ctx);
    }

    private static void EvaluateTopEvents(AnalysisContext ctx, AwrTable top)
    {
        foreach (var row in top.DataRows)
        {
            var name = row.Str("Event");
            var pct = row.Num("% DB time", "%DB time", "% DB Time") ?? 0;
            // Column headers differ by release: 19c "Wait Avg(ms)", 11g/12c "Avg wait (ms)"; all are milliseconds.
            var avgMs = row.Num("Wait Avg(ms)", "Avg wait (ms)", "Avg Wait(ms)", "Avg Wait", "Avg wait");
            var waits = row.Num("Waits");
            var seconds = row.Num("Total Wait Time (sec)", "Total Wait Time (s)", "Time(s)", "Time (s)");
            var waitClass = row.Str("Wait Class");
            if (string.IsNullOrWhiteSpace(name)) continue;
            // Older reports round the average to whole milliseconds (0 for fast waits): recompute from total time and waits.
            avgMs = AvgMs(avgMs, seconds, waits);

            if (name.Equals("DB CPU", StringComparison.OrdinalIgnoreCase))
            {
                EvaluateCpu(ctx, pct, seconds, top.Id);
                continue;
            }
            // Resource Manager throttling is explained together with the host CPU picture in the platform rules.
            if (name.StartsWith("resmgr:", StringComparison.OrdinalIgnoreCase)) continue;

            var kb = WaitEventKnowledge.Find(name);
            var latencyBad = kb?.HealthyAvgMs is { } healthy && avgMs is { } a && a > healthy * 2;
            var contention = waitClass is "Concurrency" or "Application" or "Configuration";
            var segShare = TopSegmentShare(ctx, name);

            // Reporting threshold: 5% of DB time, or 1% when the wait is slow or is a contention event
            // (contention is usually fixable by design), or 0.5% for configuration waits / waits concentrated on one segment.
            double min = 5;
            if (latencyBad || contention) min = 1;
            if (waitClass == "Configuration" || segShare >= 50) min = Math.Min(min, 0.5);
            if (pct < min) continue;

            var sev = SeverityMath.FromDbTimePct(pct);
            if (latencyBad) sev = SeverityMath.Raise(sev);
            // Each wait for a log switch that cannot proceed stalls every writer for seconds, which a 1-3% share understates.
            var slowSwitch = name.StartsWith("log file switch (checkpoint incomplete", StringComparison.OrdinalIgnoreCase) && avgMs >= 1000;
            if (slowSwitch) sev = SeverityMath.Raise(sev);
            sev = SeverityMath.ForLoad(ctx, sev);

            var f = new Finding
            {
                Severity = sev,
                Category = "Wait events",
                Impact = pct,
                SourceViews = ["DBA_HIST_SYSTEM_EVENT", "DBA_HIST_ACTIVE_SESS_HISTORY"],
                TableRefs = [top.Id],
                ChartRefs = ["top-events", "db-time-breakdown"],
                Evidence =
                [
                    new("Event", name),
                    new("Share of database time", P(pct)),
                    new("Time waited", Dur(seconds)),
                    new("Number of waits", N(waits)),
                    new("Average wait", Ms(avgMs), kb?.HealthyAvgMs is { } h ? (avgMs is > 0 && avgMs > h ? $"above the usual {N(h)} ms" : $"within the usual under {N(h)} ms") : null),
                    new("Wait class", string.IsNullOrEmpty(waitClass) ? "n/a" : waitClass)
                ]
            };

            if (kb != null)
            {
                f.Title = $"{kb.Friendly}: '{name}' took {P(pct)} of database time";
                f.Plain = kb.Plain + (latencyBad ? $" On top of that, each wait took {Ms(avgMs)} on average, well above the healthy range (under {N(kb.HealthyAvgMs)} ms), which points to slow storage or an overloaded resource." : "");
                f.WhyItMatters = kb.Why;
                f.Technical = kb.Technical;
                f.Recommendations = [.. kb.Actions];
                f.Sql = kb.Sql.Select(s => s with { Code = s.Code.Replace("&event", name.Replace("'", "''")) }).ToList();
                f.Sql.Add(new("Trend of this wait across snapshots (DBA_HIST_SYSTEM_EVENT)",
                    $"""
                    SELECT s.snap_id,
                           TO_CHAR(s.end_interval_time, 'YYYY-MM-DD HH24:MI') AS snap_end,
                           e.total_waits - LAG(e.total_waits) OVER (ORDER BY s.snap_id) AS waits,
                           ROUND((e.time_waited_micro - LAG(e.time_waited_micro) OVER (ORDER BY s.snap_id)) / 1e6, 1) AS seconds_waited
                    FROM   dba_hist_system_event e
                           JOIN dba_hist_snapshot s
                             ON s.snap_id = e.snap_id AND s.dbid = e.dbid AND s.instance_number = e.instance_number
                    WHERE  e.event_name = '{name.Replace("'", "''")}'
                    AND    e.instance_number = &inst_num
                    AND    s.snap_id BETWEEN &begin_snap - 1 AND &end_snap
                    ORDER  BY s.snap_id;
                    """, "These counters are cumulative since instance startup; the LAG() difference gives the activity per snapshot interval."));
                f.Docs = [.. kb.Docs];
            }
            else
            {
                var (plain, advice) = WaitEventKnowledge.WaitClass(waitClass);
                f.Title = $"'{name}' took {P(pct)} of database time";
                f.Plain = $"Sessions spent {P(pct)} of their time on the wait event '{name}'. {plain}";
                f.WhyItMatters = "Any single wait that consumes a large share of database time is directly slowing users down.";
                f.Technical = $"Wait class '{waitClass}'. Average wait {Ms(avgMs)} across {N(waits)} waits. Check the Oracle Database Reference for this event's parameters (P1/P2/P3) and ASH for the sessions and SQL involved.";
                f.Recommendations = [advice, "Use ASH (DBA_HIST_ACTIVE_SESS_HISTORY) to identify the SQL and sessions that waited on this event."];
                f.Sql = [new("Sessions and SQL that waited on this event (ASH)",
                    $"""
                    SELECT sql_id, session_id, program, COUNT(*) * 10 AS approx_seconds
                    FROM   dba_hist_active_sess_history
                    WHERE  event = '{name.Replace("'", "''")}'
                    AND    snap_id BETWEEN &begin_snap AND &end_snap
                    GROUP  BY sql_id, session_id, program
                    ORDER  BY approx_seconds DESC;
                    """, "Requires the Diagnostics Pack license. DBA_HIST ASH keeps one sample in ten, so each row represents about 10 seconds. Add 'FETCH FIRST 20 ROWS ONLY' (12c and later) to limit the output.")];
                f.Docs = [D.WaitEvents, D.WaitClasses];
            }

            if (slowSwitch)
                f.Plain += $" Each wait lasted {Dur(avgMs / 1000)} on average, during which no session could write.";
            if (name.Equals("log file sync", StringComparison.OrdinalIgnoreCase) && !latencyBad && waits is > 0 && ctx.ElapsedSec is > 0)
                f.Plain += $" The individual waits are short ({Ms(avgMs)}); the cost comes from how many commits there are ({N(waits / ctx.ElapsedSec, 0)} per second). Committing less often (grouping rows per commit) helps more than faster storage.";
            AttachSegments(ctx, f, name);
            AttachBufferClasses(ctx, f, name);
            AttachEnqueue(ctx, f, name);
            AttachAshContext(ctx, f, name);
            ctx.Add(f);
        }
    }

    // ---------- segments ----------

    private static AwrTable? SegmentTableFor(AnalysisContext ctx, string eventName)
    {
        var map = SegmentMap.FirstOrDefault(m => eventName.StartsWith(m.Event, StringComparison.OrdinalIgnoreCase));
        return map.SegmentTable == null ? null : ctx.Report.Table(map.SegmentTable);
    }

    private static double TopSegmentShare(AnalysisContext ctx, string eventName)
    {
        var seg = SegmentTableFor(ctx, eventName);
        if (seg == null || seg.Rows.Count == 0) return 0;
        return seg.DataRows.First().Num("%Total", "% of Capture") ?? 0;
    }

    private static void AttachSegments(AnalysisContext ctx, Finding f, string eventName)
    {
        var map = SegmentMap.FirstOrDefault(m => eventName.StartsWith(m.Event, StringComparison.OrdinalIgnoreCase));
        var seg = SegmentTableFor(ctx, eventName);
        if (seg == null || seg.Rows.Count == 0) return;
        f.TableRefs.Add(seg.Id);
        var valueIdx = RuleHelpers.SegmentValueColumn(seg);
        var valueCol = valueIdx >= 0 ? seg.Headers[valueIdx] : "value";
        var objects = new List<string>();
        foreach (var r in seg.DataRows.Take(5))
        {
            var type = r.Str("Obj. Type", "Object Type");
            var share = r.Str("%Total", "% of Capture");
            var full = RuleHelpers.SegmentName(r);
            objects.Add(full);
            f.Evidence.Add(new($"Hot object ({type})", full, $"{r.StrAt(valueIdx)} {valueCol.ToLowerInvariant()}" + (share.Length > 0 ? $", {share}% of total" : "")));
        }
        if (objects.Count > 0)
            f.Recommendations.Insert(0, $"Start with the objects most affected: {string.Join(", ", objects.Take(3))} (from '{map.SegmentTable}').");

        var first = seg.DataRows.First();
        var firstShare = first.Num("%Total", "% of Capture") ?? 0;
        if (firstShare >= 50)
            f.Recommendations.Insert(1, $"One segment ({RuleHelpers.SegmentName(first)}) accounts for {P(firstShare, 0)} of this activity, so a fix aimed at that single object removes most of the problem.");
        TailorSql(ctx, f, eventName, first);
        if (!f.SourceViews.Contains("DBA_HIST_SEG_STAT")) f.SourceViews.Add("DBA_HIST_SEG_STAT");
    }

    /// <summary>Replaces the generic placeholder snippets by statements for the actual hottest segment (table / partition / index).</summary>
    private static void TailorSql(AnalysisContext ctx, Finding f, string eventName, AwrRow first)
    {
        var owner = first.Str("Owner");
        var obj = first.Str("Object Name");
        var sub = first.Str("Subobject Name");
        var type = first.Str("Obj. Type", "Object Type").ToUpperInvariant();
        if (owner.Length == 0 || obj.Length == 0) return;
        var isIndex = type.Contains("INDEX");
        var isPart = type.Contains("PARTITION") && sub.Length > 0;
        var q = $"{owner}.{obj}";
        var online = AtLeast(ctx, 12, 2);
        var oc = online ? " ONLINE" : "";
        var onlineNote = online ? "" : "  (this release cannot move a table online: the table is locked while it moves)";

        // Index-organized tables keep their data in the primary-key index (SYS_IOT_TOP_<n>) and Oracle E-Business Suite owns
        // its seeded objects: reverse-key, hash-partitioned or INITRANS changes to them are not supported or not possible.
        var isIot = isIndex && RuleHelpers.IsIotIndexName(obj);
        var isEbs = RuleHelpers.IsEbsSeededOwner(ctx, owner);
        var contention = eventName.StartsWith("enq: TX - allocate ITL", StringComparison.OrdinalIgnoreCase)
                         || eventName.StartsWith("buffer busy waits", StringComparison.OrdinalIgnoreCase)
                         || eventName.StartsWith("enq: TX - index contention", StringComparison.OrdinalIgnoreCase);
        if (contention && (isIot || isEbs))
        {
            f.Sql.RemoveAll(s => s.Title.StartsWith("Raise INITRANS", StringComparison.OrdinalIgnoreCase)
                                 || s.Title.StartsWith("Spread a hot right-growing index", StringComparison.OrdinalIgnoreCase));
            var why = isIot && isEbs ? "an index-organized table of an Oracle E-Business Suite seeded schema"
                    : isIot ? "the primary-key structure of an index-organized table (IOT)"
                    : "an Oracle E-Business Suite seeded object";
            f.Recommendations.Insert(0, isEbs
                ? $"{q} is {why}: do not reverse, hash-partition or change INITRANS on it yourself. Raise a service request with Oracle Support (or the application vendor) with this report, and look at the module or concurrent program that drives the contention (for example reduce the number of parallel workers)."
                : $"{q} is {why}: it cannot be rebuilt as a reverse-key index and a reverse-key or INITRANS change would apply to the whole table. Hash partitioning is possible only by re-creating the IOT with PARTITION BY HASH (Partitioning option, Enterprise Edition) or redefining it online with DBMS_REDEFINITION; otherwise reduce the concurrency of the inserting sessions and enlarge the cache of the sequence that feeds the key.");
            f.Sql.Insert(0, new($"What is {q}? (read-only)",
                $"SELECT i.owner, i.index_name, i.index_type, i.table_name, t.iot_type, t.partitioned\nFROM   dba_indexes i JOIN dba_tables t ON t.owner = i.table_owner AND t.table_name = i.table_name\nWHERE  i.owner = '{owner}' AND i.index_name = '{obj}';",
                isEbs ? "Seeded Oracle E-Business Suite objects must be changed through Oracle-supplied patches or an Oracle Support service request, not with your own DDL."
                      : "Do not run ALTER INDEX ... REBUILD REVERSE on an IOT primary-key index: it is not supported for index-organized tables."));
            return;
        }

        if (eventName.StartsWith("enq: TX - allocate ITL", StringComparison.OrdinalIgnoreCase))
        {
            f.Sql.RemoveAll(s => s.Title.StartsWith("Raise INITRANS", StringComparison.OrdinalIgnoreCase));
            string code, caution;
            if (isIndex && isPart)
            {
                code = $"ALTER INDEX {q} MODIFY PARTITION {sub} INITRANS 16;   -- only new blocks get the new value\nALTER INDEX {q} REBUILD PARTITION {sub} ONLINE;          -- rebuild so existing blocks get it too";
                caution = "Choose INITRANS from the number of transactions that touch one block at the same time (typically 8-20); every extra slot costs 24 bytes per block.";
            }
            else if (isIndex)
            {
                code = $"ALTER INDEX {q} REBUILD ONLINE INITRANS 16;";
                caution = "Choose INITRANS from the number of transactions that touch one block at the same time (typically 8-20); every extra slot costs 24 bytes per block.";
            }
            else if (isPart)
            {
                code = $"ALTER TABLE {q} MODIFY PARTITION {sub} INITRANS 16;   -- only new blocks get the new value\nALTER TABLE {q} MOVE PARTITION {sub}{oc} UPDATE INDEXES;   -- rebuilds existing blocks{onlineNote}";
                caution = "On releases before 12.2 use ALTER TABLE ... MOVE PARTITION without ONLINE (it locks the partition and needs UPDATE INDEXES) or DBMS_REDEFINITION; the ONLINE keyword is only used in the statements above when the report is from 12.2 or later. Typical INITRANS: 8-20; every slot costs 24 bytes per block.";
            }
            else
            {
                code = $"ALTER TABLE {q} INITRANS 16 PCTFREE 20;   -- only new blocks get the new values\nALTER TABLE {q} MOVE{oc};                -- rebuilds existing blocks{onlineNote}; its indexes become UNUSABLE after a non-online move and must be rebuilt";
                caution = "Plan this for a quiet period: moves generate redo and need free space. Before 12.2 a table cannot be moved online: use DBMS_REDEFINITION for an online change.";
            }
            f.Sql.Insert(0, new($"Raise INITRANS on the hottest segment ({q}{(sub.Length > 0 ? " " + sub : "")})", code, caution));
        }
        else if (eventName.StartsWith("buffer busy waits", StringComparison.OrdinalIgnoreCase) || eventName.StartsWith("enq: TX - index contention", StringComparison.OrdinalIgnoreCase))
        {
            f.Sql.RemoveAll(s => s.Title.StartsWith("Spread a hot right-growing index", StringComparison.OrdinalIgnoreCase));
            if (isIndex)
            {
                f.Sql.Insert(0, new($"Which table and columns does the hot index {q} cover?",
                    $"SELECT index_owner, index_name, table_owner, table_name, column_position, column_name\nFROM   dba_ind_columns\nWHERE  index_owner = '{owner}' AND index_name = '{obj}'\nORDER  BY column_position;"));
                f.Sql.Insert(1, new($"Options to spread inserts on {q} (choose one after reading the columns)",
                    $"-- Replace the &placeholders with the values returned by the query above.\n-- Option A: hash-partitioned global index (Enterprise Edition with the Partitioning option)\nCREATE INDEX &index_owner..&index_name._hp ON &table_owner..&table_name (&key_columns)\n  GLOBAL PARTITION BY HASH (&key_columns) PARTITIONS 16 ONLINE;\n-- Option B: reverse key index (range scans on the key can no longer use it)\nALTER INDEX &index_owner..&index_name REBUILD REVERSE ONLINE;\n-- If the leading column is fed by a sequence: raise its cache\n-- ALTER SEQUENCE &sequence_owner..&sequence_name CACHE 1000;",
                    "Test first: reverse-key indexes disable index range scans, and a new hash-partitioned index replaces the old one only after you drop it (or use ALTER INDEX ... RENAME). Not for index-organized-table primary keys or Oracle E-Business Suite seeded objects."));
            }
            else
            {
                f.Sql.Insert(0, new($"Space management of the hot table {q} (ASSM or manual free lists?)",
                    $"SELECT s.owner, s.segment_name, s.segment_type, s.tablespace_name, ts.segment_space_management\nFROM   dba_segments s\n       JOIN dba_tablespaces ts ON ts.tablespace_name = s.tablespace_name\nWHERE  s.owner = '{owner}' AND s.segment_name = '{obj}';"));
            }
        }
    }

    // ---------- buffer busy waits: block classes ----------

    private static readonly Dictionary<string, (string Meaning, string Action)> BlockClassAdvice = new(StringComparer.OrdinalIgnoreCase)
    {
        ["data block"] = ("table or index data blocks changed or read by many sessions at the same moment",
            "Concentrate on the hot segments listed above: spread inserts (hash partitioning, reverse-key or hash-partitioned index, bigger sequence cache), shorten the transactions that touch them, or lower the number of concurrent batch workers."),
        ["undo block"] = ("undo blocks (the 'before images' of changes) used by many sessions at the same time, typically queries rebuilding old versions of heavily modified blocks",
            "Reduce the number of sessions that modify and read the same blocks at the same time (see the read-consistency finding), commit promptly, and keep long queries away from tables that are being updated heavily."),
        ["undo header"] = ("the header of an undo segment, a sign of very many short concurrent transactions",
            "Check 'Undo Segment Summary' for 'Max Tx Concurrency', make sure automatic undo management is used and the undo tablespace is large enough so that enough undo segments exist."),
        ["segment header"] = ("the header of a table or index, where free-space information is kept",
            "Use ASSM tablespaces (SEGMENT SPACE MANAGEMENT AUTO), larger extents, or partitioning so that inserts do not queue on one segment header."),
        ["file header block"] = ("the header of a data file, touched when the file has to grow or space is allocated",
            "Pre-size data files or use larger AUTOEXTEND increments so that sessions do not repeatedly stall on file growth; check for many sessions allocating temporary or new space at the same time."),
        ["1st level bmb"] = ("ASSM space-bitmap blocks, used when many sessions insert into the same segment",
            "Spread inserts over partitions or use larger initial extents so that concurrent inserters use different bitmap blocks."),
        ["2nd level bmb"] = ("ASSM space-bitmap blocks, used when many sessions insert into the same segment",
            "Spread inserts over partitions or use larger initial extents so that concurrent inserters use different bitmap blocks."),
    };

    private static void AttachBufferClasses(AnalysisContext ctx, Finding f, string eventName)
    {
        if (!eventName.StartsWith("buffer busy waits", StringComparison.OrdinalIgnoreCase)) return;
        var t = ctx.Report.Table("Buffer Wait Statistics");
        if (t == null || t.Rows.Count == 0) return;
        var rows = t.DataRows
            .Select(r => (Class: r.Str("Class"), Sec: r.Num("Total Wait Time (s)", "Total Wait Time (sec)") ?? 0, Waits: r.Num("Waits") ?? 0, Avg: r.Num("Avg Time (ms)")))
            .Where(x => x.Class.Length > 0 && x.Sec > 0)
            .OrderByDescending(x => x.Sec).ToList();
        var total = rows.Sum(x => x.Sec);
        if (total <= 0) return;
        f.TableRefs.Add(t.Id);
        var shown = new List<string>();
        foreach (var x in rows.Take(4))
        {
            var share = x.Sec / total * 100;
            f.Evidence.Add(new("Block class", x.Class, $"{Dur(x.Sec)} ({P(share, 0)} of buffer busy wait time), {N(x.Waits)} waits"));
            shown.Add($"{x.Class} {P(share, 0)}");
        }
        f.Technical += $" Block classes by wait time (Buffer Wait Statistics): {string.Join(", ", shown)}.";
        var seen = new HashSet<string>();
        foreach (var x in rows.Where(x => x.Sec / total * 100 >= 10).Take(3))
        {
            if (!BlockClassAdvice.TryGetValue(x.Class, out var adv) || !seen.Add(adv.Action)) continue;
            f.Recommendations.Insert(Math.Min(1, f.Recommendations.Count), $"{x.Class} ({P(x.Sec / total * 100, 0)} of the waiting): {adv.Meaning}. {adv.Action}");
        }
        if (!f.SourceViews.Contains("DBA_HIST_WAITSTAT")) f.SourceViews.Add("DBA_HIST_WAITSTAT");
    }

    // ---------- enqueues ----------

    private static void AttachEnqueue(AnalysisContext ctx, Finding f, string eventName)
    {
        if (!eventName.StartsWith("enq:", StringComparison.OrdinalIgnoreCase)) return;
        var t = ctx.Report.Table("Enqueue Activity");
        if (t == null) return;
        var body = eventName[4..].Trim();
        var parts = body.Split(" - ", 2, StringSplitOptions.TrimEntries);
        var code = parts[0];
        var reason = parts.Length > 1 ? parts[1] : "";
        foreach (var r in t.DataRows)
        {
            var name = r.Str("Enqueue Type (Request Reason)");
            if (!name.StartsWith(code + "-", StringComparison.OrdinalIgnoreCase)) continue;
            if (!(reason.Equals("contention", StringComparison.OrdinalIgnoreCase) || name.Contains(reason, StringComparison.OrdinalIgnoreCase))) continue;
            var req = r.Num("Requests");
            var failed = r.Num("Failed Gets");
            var waits = r.Num("Waits");
            f.Evidence.Add(new("Enqueue activity", name, $"requests {N(req)}, failed gets {N(failed)}, waits {N(waits)}, {Dur(r.Num("Wt Time (s)"))} total, {Ms(r.Num("Av Wt Time(ms)"))} avg"));
            if (!f.TableRefs.Contains(t.Id)) f.TableRefs.Add(t.Id);
            if (code.Equals("TX", StringComparison.OrdinalIgnoreCase) && reason.Contains("row lock", StringComparison.OrdinalIgnoreCase) && failed > 10000 && waits > 0 && failed > 5 * waits)
            {
                f.Technical += $" Failed gets ({N(failed)}) are far higher than waits ({N(waits)}): most lock attempts failed immediately instead of waiting, the signature of work-queue code that uses SELECT ... FOR UPDATE SKIP LOCKED / NOWAIT. In that pattern the 'Segments by Row Lock Waits' counts overstate how long users really waited; the time that matters is the {Dur(r.Num("Wt Time (s)"))} actually waited.";
                f.Recommendations.Add("If the hot table is a work queue polled with SKIP LOCKED/NOWAIT, the failed attempts are by design; reduce the number of competing workers or the polling frequency instead of treating them as blocked users.");
            }
        }
        // The ASH view of the same wait shows who held the lock when a 'Top Blocking Sessions' table exists (handled in the ASH rules).
    }

    /// <summary>The ASH section of 12c+ reports lists which SQL and objects contributed to each event.</summary>
    private static void AttachAshContext(AnalysisContext ctx, Finding f, string eventName)
    {
        var topSql = ctx.Report.Table("Top SQL with Top Events");
        if (topSql != null)
        {
            foreach (var r in topSql.DataRows.Where(r => r.Str("Event").Equals(eventName, StringComparison.OrdinalIgnoreCase)).Take(3))
            {
                f.Evidence.Add(new("SQL most affected (ASH)", r.Str("SQL ID"), $"{r.Str("% Event")}% of all sampled activity; {r.Str("SQL Text")}"));
                if (!f.TableRefs.Contains(topSql.Id)) f.TableRefs.Add(topSql.Id);
            }
        }
        var objs = ctx.Report.Table("Top DB Objects");
        if (objs != null)
        {
            foreach (var r in objs.DataRows.Where(r => r.Str("Event").Equals(eventName, StringComparison.OrdinalIgnoreCase)).Take(3))
            {
                f.Evidence.Add(new("Object most affected (ASH)", r.Str("Object Name (Type)", "Object Name"), $"{r.Str("% Event")}% of all sampled activity"));
                if (!f.TableRefs.Contains(objs.Id)) f.TableRefs.Add(objs.Id);
            }
        }
        var p123 = ctx.Report.Table("Top Event P1/P2/P3 Values");
        if (p123?.DataRows.FirstOrDefault(r => r.Str("Event").Equals(eventName, StringComparison.OrdinalIgnoreCase)) is { Table: not null } pr)
        {
            var values = pr.Str("P1, P2, P3 Values").Replace("\"", "").Split(',', StringSplitOptions.TrimEntries);
            var names = new[] { pr.Str("Parameter 1"), pr.Str("Parameter 2"), pr.Str("Parameter 3") };
            var decoded = new List<string>();
            for (var i = 0; i < 3 && i < values.Length; i++)
            {
                if (names[i].Length == 0) continue;
                var v = values[i];
                if (names[i] == "name|mode" && long.TryParse(v, out var p1)) v = $"{(char)((p1 >> 24) & 0xFF)}{(char)((p1 >> 16) & 0xFF)} mode {p1 & 0xFFFF}";
                else if (names[i] == "class#" && int.TryParse(v, out var cls) && BlockClassNames.TryGetValue(cls, out var cn)) v = $"{v} ({cn})";
                decoded.Add($"{names[i]} = {v}");
            }
            if (decoded.Count > 0)
            {
                f.Evidence.Add(new("Typical wait parameters (ASH)", string.Join("; ", decoded), $"seen in {pr.Str("% Event")}% of this event's samples"));
                if (!f.TableRefs.Contains(p123.Id)) f.TableRefs.Add(p123.Id);
            }
        }
    }

    private static readonly Dictionary<int, string> BlockClassNames = new()
    {
        [1] = "data block", [2] = "sort block", [3] = "save undo block", [4] = "segment header", [5] = "save undo header", [6] = "free list",
        [7] = "extent map", [8] = "1st level bmb", [9] = "2nd level bmb", [10] = "3rd level bmb", [11] = "bitmap block", [12] = "bitmap index block",
        [13] = "file header block", [15] = "system undo header", [16] = "system undo block", [17] = "undo header", [18] = "undo block"
    };

    // ---------- CPU ----------

    private static void EvaluateCpu(AnalysisContext ctx, double pct, double? seconds, string tableId)
    {
        if (pct < 50) return;
        var kb = WaitEventKnowledge.Find("DB CPU")!;
        var host = ctx.Report.TableStartingWith("Host CPU");
        var idle = RuleHelpers.HostIdle(ctx);
        var busy = idle is null ? (double?)null : 100 - idle;
        var cpuAas = (ctx.LoadPerSec("DB CPU(s)") ?? 0) + (ctx.LoadPerSec("Background CPU(s)") ?? 0);
        double? cpuRatio = ctx.Cpus is > 0 && cpuAas > 0 ? cpuAas / ctx.Cpus.Value : null;
        // CPU-bound means the processors are really nearly full, not merely that CPU is the largest slice of DB time
        // (a busy-but-healthy OLTP system can spend 80% of its DB time on CPU while half the server is idle).
        var cpuBound = busy >= 85 || cpuRatio >= 0.9;
        var hostKnown = busy is not null;
        var spareCpu = hostKnown ? !cpuBound : cpuRatio < 0.7;
        var sev = cpuBound ? (busy >= 95 || cpuRatio >= 1.5 ? Severity.High : Severity.Medium) : Severity.Info;
        sev = SeverityMath.ForLoad(ctx, sev);
        var logicalReads = ctx.LoadPerSec("Logical read (blocks)", "Logical reads");

        var f = new Finding
        {
            Severity = sev,
            Category = "CPU",
            Impact = pct,
            Title = cpuBound
                ? $"CPU is the main consumer of database time ({P(pct)}) and the server's CPUs are nearly full"
                : spareCpu ? $"Most database time ({P(pct)}) is spent working on the CPU; the server still has spare CPU"
                           : $"Most database time ({P(pct)}) is spent working on the CPU",
            Plain = cpuBound
                ? $"The database needs a lot of processor power: {P(pct)} of its time is computation, and the server was {P(busy, 0)} busy. When the processors are full, work waits in line for a free CPU. The statements using the most CPU are listed below."
                : hostKnown
                    ? $"The database spends most of its time computing rather than waiting, which is generally a healthy profile. The server was only {P(busy, 0)} busy ({P(idle, 0)} idle), so CPU is not the bottleneck today. The statements that read the most data decide how much CPU is used, so making them do less work keeps that headroom and speeds them up."
                    : $"The database spends most of its time computing rather than waiting. This report has no server-wide CPU figures, but the database itself used about {N(cpuAas, 1)} of {N(ctx.Cpus)} CPUs on average" + (spareCpu ? ", so CPU does not look like the bottleneck. " : ", so the processors may be a limit. ") + "The statements that read the most data decide how much CPU is used, so making them do less work speeds everything up.",
            WhyItMatters = cpuBound
                ? "A CPU-saturated server slows everything down, including the database's own background processes (for example the log writer, which makes every commit wait)."
                : "CPU time is normal and good when it is used efficiently. It becomes a problem only when the processors fill up, because then every user waits in line; tuning the biggest consumers now protects against that.",
            Technical = kb.Technical + $" Logical reads: {Big(logicalReads)} blocks/s. CPU demand {N(cpuAas, 1)} CPUs of {N(ctx.Cpus)}; host busy {P(busy, 1)}.",
            Recommendations = cpuBound || !spareCpu ? [.. kb.Actions] : [.. kb.Actions.Take(3)],
            Sql = [.. kb.Sql],
            Docs = [.. kb.Docs],
            SourceViews = ["DBA_HIST_SYS_TIME_MODEL", "DBA_HIST_OSSTAT", "DBA_HIST_SQLSTAT"],
            TableRefs = [tableId],
            ChartRefs = ["db-time-breakdown", "host-cpu", "top-sql-cpu"],
            Evidence =
            [
                new("DB CPU share of DB time", P(pct)),
                new("CPU time used", Dur(seconds)),
                new("CPU used by the database", N(cpuAas, 1) + " CPUs"),
                new("CPUs on the host", N(ctx.Cpus)),
                new("Host CPU idle", idle is null ? "not in this report" : P(idle)),
                new("Logical reads per second", Big(logicalReads), "memory block reads; high values usually mean inefficient SQL")
            ]
        };
        if (host != null) f.TableRefs.Add(host.Id);

        var cpuSql = ctx.Report.Table("SQL ordered by CPU Time");
        if (cpuSql != null)
        {
            f.TableRefs.Add(cpuSql.Id);
            foreach (var r in cpuSql.DataRows.Take(3))
            {
                var id = r.Str("SQL Id");
                var text = ctx.SqlTexts.TryGetValue(id, out var full) ? full : r.Str("SQL Text");
                f.Evidence.Add(new("Top CPU statement", id, $"{r.Str("%Total")}% of DB CPU, {r.Str("Executions")} executions, module {r.Str("SQL Module")}" + (RuleHelpers.IsPlsqlText(text) ? " (PL/SQL block: includes the SQL it runs)" : "")));
            }
        }
        ctx.Add(f);
    }

    // ---------- wait classes ----------

    private static void EvaluateWaitClasses(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("Foreground Wait Class") ?? ctx.Report.Table("Wait Classes by Total Wait Time");
        if (t == null || t.Rows.Count == 0) return;

        var classes = t.DataRows
            .Select(r => (Name: r.Str("Wait Class"), Pct: r.Num("% DB time", "%DB time") ?? 0, Sec: r.Num("Total Wait Time (sec)", "Total Wait Time (s)", "Time(s)"), Avg: r.Num("Avg Wait (ms)", "Avg wait (ms)")))
            .Where(c => c.Name.Length > 0)
            .OrderByDescending(c => c.Pct)
            .ToList();
        if (classes.Count == 0) return;

        var top3 = classes.Take(3).Select(c => $"{Friendly(c.Name)} ({P(c.Pct)})");
        ctx.Add(new Finding
        {
            Severity = Severity.Info,
            Category = "Workload profile",
            Title = "Where the database time went",
            Plain = $"'DB time' is the total time all sessions spent working or waiting inside the database. Most of it went to: {string.Join(", ", top3)}.",
            WhyItMatters = "Oracle's tuning method starts with the biggest slice of DB time. Fixing a small slice cannot noticeably improve response time.",
            Technical = "Wait classes aggregate wait events (Database Reference: Classes of Wait Events); 'DB CPU' comes from the time model. Idle waits are excluded.",
            Evidence = classes.Select(c => new Evidence(c.Name, P(c.Pct), $"{Dur(c.Sec)}; {WaitEventKnowledge.WaitClass(c.Name).Plain}")).ToList(),
            Recommendations = ["Focus tuning effort on the largest wait classes and the findings below that belong to them."],
            Docs = [D.Measuring, D.WaitClasses, D.PerformanceMethod],
            SourceViews = ["DBA_HIST_SYSTEM_EVENT (wait_class)", "DBA_HIST_SYS_TIME_MODEL"],
            TableRefs = [t.Id],
            ChartRefs = ["db-time-breakdown"]
        });

        // Classes with a big share but no event-level finding (e.g. many small events in one class).
        foreach (var c in classes.Where(c => c.Pct >= 10 && !c.Name.Equals("DB CPU", StringComparison.OrdinalIgnoreCase)))
        {
            var covered = ctx.Findings.Any(f => f.Category == "Wait events" && f.Evidence.Any(e => e.Label == "Wait class" && e.Value.Equals(c.Name, StringComparison.OrdinalIgnoreCase)));
            if (covered) continue;
            var (plain, advice) = WaitEventKnowledge.WaitClass(c.Name);
            ctx.Add(new Finding
            {
                Severity = SeverityMath.ForLoad(ctx, SeverityMath.FromDbTimePct(c.Pct)),
                Category = "Wait events",
                Impact = c.Pct,
                Title = $"Wait class '{c.Name}' consumed {P(c.Pct)} of database time",
                Plain = $"{plain} This category took {P(c.Pct)} of all database time, spread over several smaller wait events.",
                WhyItMatters = "A large share of time in one category is a clear tuning target even when no single event dominates.",
                Technical = $"Average wait {Ms(c.Avg)}. Review 'Foreground Wait Events' filtered to this class.",
                Recommendations = [advice, "Review the individual events of this class in 'Foreground Wait Events'."],
                Docs = [D.WaitClasses, D.WaitEvents],
                SourceViews = ["DBA_HIST_SYSTEM_EVENT"],
                TableRefs = [t.Id],
                ChartRefs = ["db-time-breakdown"]
            });
        }
    }

    private static void EvaluateBackground(AnalysisContext ctx)
    {
        var bg = ctx.Report.Table("Background Wait Events");
        if (bg == null) return;
        var lgwr = bg.RowByLabel("log file parallel write");
        var avg = lgwr.Val("Avg wait (ms)", "Avg wait", "Avg Wait");
        if (lgwr is { } r && avg is > 10 && (r.Num("Waits") ?? 0) > 1000)
        {
            var kb = WaitEventKnowledge.Find("log file parallel write")!;
            ctx.Add(new Finding
            {
                Severity = SeverityMath.ForLoad(ctx, avg > 20 ? Severity.High : Severity.Medium),
                Category = "I/O",
                Title = $"Redo log writes are slow ({Ms(avg)} on average)",
                Plain = kb.Plain + $" Each write took {Ms(avg)} on average, where under 5 ms is expected on modern storage.",
                WhyItMatters = kb.Why + " Every commit waits for this write, so slow redo writes slow down every application that saves data.",
                Technical = kb.Technical,
                Recommendations = [.. kb.Actions],
                Sql = [.. kb.Sql.Select(s => s with { Code = s.Code.Replace("&event", "log file parallel write") })],
                Docs = [.. kb.Docs],
                Evidence = [new("log file parallel write avg", Ms(avg)), new("Waits", r.Str("Waits"))],
                SourceViews = ["DBA_HIST_BG_EVENT_SUMMARY", "DBA_HIST_SYSTEM_EVENT"],
                TableRefs = [bg.Id],
                ChartRefs = ["bg-events"]
            });
        }
        else if (avg is > 0 and <= 5 && ctx.Findings.FirstOrDefault(f => f.Title.Contains("log file sync", StringComparison.OrdinalIgnoreCase)) is { } sync)
        {
            // log file sync is high but LGWR writes are fast: points to commit rate / CPU, not storage
            sync.Technical += $" Note: 'log file parallel write' averages only {Ms(avg)}, so redo storage is fast; focus on commit frequency and LGWR CPU scheduling.";
        }
    }

    private static string Friendly(string waitClass) => waitClass.ToLowerInvariant() switch
    {
        "db cpu" => "working on the CPU",
        "user i/o" => "reading data from disk",
        "system i/o" => "background disk writes",
        "concurrency" => "contention between sessions",
        "application" => "application locks (row locks and user-defined locks)",
        "commit" => "saving transactions (commits)",
        "configuration" => "configuration limits",
        "network" => "network transfers",
        "cluster" => "RAC cluster traffic",
        "scheduler" => "Resource Manager throttling",
        "other" => "miscellaneous internal waits",
        _ => waitClass
    };
}
