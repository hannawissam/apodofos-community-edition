// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text.RegularExpressions;
using Apodofos.Core.Model;
using static Apodofos.Core.Analysis.AnalysisContext;
using static Apodofos.Core.Analysis.Rules.RuleHelpers;
using D = Apodofos.Core.Knowledge.OracleDocs;

namespace Apodofos.Core.Analysis.Rules;

/// <summary>
/// Uses the Active Session History sections of the report (Top SQL with Top Events, Top Sessions, Top Blocking Sessions,
/// Activity Over Time) to say WHO and WHEN, which the AWR wait-event totals alone cannot.
/// </summary>
public sealed class AshCorrelationRules : IRule
{
    public string Name => "ASH correlation";

    public void Evaluate(AnalysisContext ctx)
    {
        BlockingSessions(ctx);
        DominantSession(ctx);
        ActivitySpike(ctx);
        UncoveredTopSql(ctx);
    }

    private static void BlockingSessions(AnalysisContext ctx)
    {
        var t = ctx.Report.TableStartingWith("Top Blocking Sessions");
        if (t == null || t.Rows.Count == 0) return;
        var all = t.DataRows.Where(r => (r.Num("% Activity") ?? 0) >= 3).ToList();
        // ASH could not identify the blocker: nothing to act on.
        all.RemoveAll(r => r.Str("Program").Contains("NOT FOUND", StringComparison.OrdinalIgnoreCase) || r.Str("Blocking Sid (Inst)", "Blocking Sid").Contains("NOT FOUND", StringComparison.OrdinalIgnoreCase));
        // Oracle background processes (LGWR, DBWn, ...) as the 'blocker' mean sessions queue for that process (commit or buffer writes),
        // not that a user holds a lock: report that on the matching wait-event finding instead.
        var bg = System.Text.RegularExpressions.Regex.Matches("", "x");
        var isBackground = new Func<AwrRow, bool>(r => System.Text.RegularExpressions.Regex.IsMatch(r.Str("Program"), @"\((LGWR|LG\d\d|DBW\w*|CKPT|SMON|PMON|ARC\w*|LMS\w*|LMD\w*|MMON|MMNL|RVWR|CJQ\w*)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        foreach (var b in all.Where(isBackground).ToList())
        {
            var ev = b.Str("Event Caused");
            var sync = ctx.Findings.FirstOrDefault(f => ev.Length > 0 && f.Title.Contains($"'{ev}'", StringComparison.OrdinalIgnoreCase));
            sync?.Evidence.Add(new("ASH: sessions waited behind", b.Str("Program"), $"{b.Str("% Activity")}% of all sampled activity was queued behind this Oracle process"));
        }
        all.RemoveAll(r => isBackground(r));
        var rows = all.Take(5).ToList();
        if (rows.Count == 0) return;
        var top = rows[0];
        var pct = top.Num("% Activity") ?? 0;
        ctx.Add(new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, pct >= 10 ? Severity.Medium : Severity.Low),
            Category = "Wait events",
            Impact = pct,
            Title = $"One session ({top.Str("Blocking Sid (Inst)", "Blocking Sid")}, {top.Str("Program")}) blocked others during {P(pct)} of all sampled activity",
            Plain = "Sampling of active sessions shows that one connection held a lock (or another resource) that other sessions were waiting for. Fixing that one program or transaction removes a large part of the waiting.",
            WhyItMatters = "Blocked users see their screens or jobs freeze until the blocker commits, rolls back or is ended, so one slow or forgotten transaction can stall many people.",
            Technical = $"Top Blocking Sessions (ASH): blocker {top.Str("Blocking Sid (Inst)", "Blocking Sid")} caused {top.Str("Event Caused")} in {P(pct)} of samples; user {top.Str("User")}; {top.Str("# Samples Active")} samples active; open transactions {top.Str("XIDs")}. Check what the blocker was doing (its SQL, how long its transaction ran) and whether it waited on something itself.",
            Evidence = rows.Select(r => new Evidence($"Blocker {r.Str("Blocking Sid (Inst)", "Blocking Sid")}", $"{r.Str("% Activity")}% of activity", $"{r.Str("Event Caused")}; {r.Str("User")}; {r.Str("Program")}")).ToList(),
            Recommendations = ["Find out what the blocking session does (see SQL below) and shorten or split its transaction; make sure it commits promptly.", "If it is a batch job, run it outside the busy period or process the data in smaller commits."],
            Sql = [new("What the blocking sessions were running (ASH history)", "SELECT blocking_session, blocking_session_serial#, event, sql_id, COUNT(*) * 10 AS approx_seconds\nFROM   dba_hist_active_sess_history\nWHERE  snap_id BETWEEN &begin_snap AND &end_snap AND blocking_session IS NOT NULL\nGROUP  BY blocking_session, blocking_session_serial#, event, sql_id\nORDER  BY approx_seconds DESC;", "Requires the Diagnostics Pack license. Add FETCH FIRST 20 ROWS ONLY (12c and later) to limit the output.")],
            Docs = [D.Concurrency, D.Transient],
            SourceViews = ["DBA_HIST_ACTIVE_SESS_HISTORY"],
            TableRefs = [t.Id]
        });
    }

    private static void DominantSession(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("Top Sessions");
        var top = t.FirstRow();
        var pct = top.Val("% Activity") ?? 0;
        // background processes (LGWR etc.) are normal; only user sessions that dominate are interesting
        var program = top.Txt("Program");
        if (t == null || pct < 15 || program.Contains("(LGWR)") || program.Contains("(DBW")) return;
        ctx.Add(new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, pct >= 30 ? Severity.Medium : Severity.Low),
            Category = "Workload profile",
            Impact = pct,
            Title = $"A single session ({top.Txt("Sid, Serial#")}) accounts for {P(pct)} of all active-session samples",
            Plain = "One connection did a large share of all the work in this period. When a single session dominates, the whole slowdown may come from one program, job or user.",
            WhyItMatters = "Tuning or rescheduling that one program is much cheaper than tuning the whole system.",
            Technical = $"Top Sessions (ASH): {top.Txt("Sid, Serial#")}, user {top.Txt("User")}, program {program}, mostly '{top.Txt("Event")}' ({top.Txt("% Event")}%), {top.Txt("# Samples Active")} samples.",
            Evidence = [new("Session", top.Txt("Sid, Serial#")), new("Program", program), new("Top event", top.Txt("Event"), $"{top.Txt("% Event")}% of activity")],
            Recommendations = ["Identify the job or user behind this session and review its SQL (see the SQL findings).", "If it is a scheduled batch job, check whether it can run at a quieter time or in parallel slices."],
            Docs = [D.Transient],
            SourceViews = ["DBA_HIST_ACTIVE_SESS_HISTORY"],
            TableRefs = [t.Id]
        });
    }

    /// <summary>The busiest time slot of 'Activity Over Time' compared with a typical slot.</summary>
    private static void ActivitySpike(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("Activity Over Time");
        if (t == null || t.Rows.Count == 0) return;
        var slots = new List<(string Slot, double Minutes, double Count, List<(string Event, double Pct)> Events)>();
        foreach (var r in t.DataRows)
        {
            var slot = r.Str("Slot Time (Duration)", "Slot Time");
            var count = r.Num("Slot Count");
            if (slot.Length == 0 || count is null) continue;
            var m = Regex.Match(slot, @"\(([\d.,]+)\s*min");
            var minutes = m.Success && Apodofos.Core.Parsing.NumberParser.Parse(m.Groups[1].Value) is { } mm ? mm : 0;
            var idx = slots.FindIndex(s => s.Slot == slot);
            if (idx < 0) { slots.Add((slot, minutes, count.Value, [])); idx = slots.Count - 1; }
            slots[idx].Events.Add((r.Str("Event"), r.Num("% Event") ?? 0));
        }
        var full = slots.Where(s => s.Minutes >= 5).ToList();
        if (full.Count < 4) return;
        var ordered = full.Select(s => s.Count).OrderBy(c => c).ToList();
        var median = ordered[ordered.Count / 2];
        var peak = full.OrderByDescending(s => s.Count).First();
        var total = full.Sum(s => s.Count);
        if (median <= 0 || peak.Count < 2.5 * median || peak.Count / total < 0.2) return;
        var mix = peak.Events.OrderByDescending(e => e.Pct).Take(3).Select(e => $"{e.Event} ({N(e.Pct, 1)}% of all samples)");
        ctx.Add(new Finding
        {
            Severity = Severity.Info,
            Category = "Workload profile",
            Title = $"Activity was concentrated in one time slot: {peak.Slot.Split('(')[0].Trim()} ({P(peak.Count / total * 100, 0)} of all samples)",
            Plain = $"Most of the sampled activity happened in the slot starting at {peak.Slot.Split('(')[0].Trim()}; it was about {N(peak.Count / median, 1)} times busier than a typical slot. The averages in the rest of the report hide this peak.",
            WhyItMatters = "Problems that users notice usually occur at the peak. The peak slot is the best place to look for the cause, and for batch jobs to reschedule.",
            Technical = $"Activity Over Time (ASH): slot {peak.Slot} has {N(peak.Count)} samples vs a median of {N(median)}. Main events in that slot: {string.Join(", ", mix)}.",
            Evidence = [new("Busiest slot", peak.Slot), new("Samples in that slot", N(peak.Count), $"median slot: {N(median)}"), .. peak.Events.OrderByDescending(e => e.Pct).Take(3).Select(e => new Evidence("Event in that slot", e.Event, $"{e.Pct}% of all samples"))],
            Recommendations = ["Compare this slot with the busiest period in the CPU finding (they often coincide) and find which jobs started then.", "Generate an AWR/ASH report restricted to this slot for a precise diagnosis."],
            Docs = [D.Transient, D.Awr],
            SourceViews = ["DBA_HIST_ACTIVE_SESS_HISTORY"],
            TableRefs = [t.Id],
            ChartRefs = ["ash-activity"]
        });
    }

    /// <summary>A statement that is prominent in ASH but did not make it into the SQL findings.</summary>
    private static void UncoveredTopSql(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("Top SQL with Top Events");
        if (t == null) return;
        var known = ctx.Findings.SelectMany(f => f.Evidence).Where(e => e.Label == "SQL Id" || e.Label == "Same program, near-identical call" || e.Label.StartsWith("SQL most affected", StringComparison.Ordinal)).Select(e => e.Value).ToHashSet();
        var rows = t.DataRows.Where(r => (r.Num("% Activity") ?? 0) >= 5 && !known.Contains(r.Str("SQL ID"))).GroupBy(r => r.Str("SQL ID")).Take(3).ToList();
        foreach (var g in rows)
        {
            var r = g.First();
            var text = ctx.SqlTexts.TryGetValue(g.Key, out var full) ? full : r.Str("SQL Text");
            var pct = r.Num("% Activity") ?? 0;
            ctx.Add(new Finding
            {
                Severity = SeverityMath.ForLoad(ctx, pct >= 25 ? Severity.Medium : Severity.Low),
                Category = "SQL",
                Impact = pct,
                Title = $"SQL {g.Key} accounts for {P(pct)} of sampled activity",
                Plain = $"Sampling of active sessions shows that one statement was responsible for {P(pct)} of the activity, mostly '{r.Str("Event")}'.",
                WhyItMatters = "A statement that is active in so many samples is a direct candidate for tuning, even if it is not listed among the top statements by elapsed time.",
                Technical = $"Top SQL with Top Events (ASH): plan hash {r.Str("Plan Hash")}, {r.Str("Executions")} executions, top row source '{r.Str("Top Row Source")}'.",
                Evidence = [new("SQL Id", g.Key), new("Main event", r.Str("Event"), $"{r.Str("% Event")}%"), new("SQL text", text.Length > 400 ? text[..400] + " ..." : text)],
                Recommendations = ["Review the execution plan (DBMS_XPLAN.DISPLAY_AWR) and run the SQL Tuning Advisor on this statement."],
                Sql = [new("Execution plan from AWR", $"SELECT * FROM TABLE(DBMS_XPLAN.DISPLAY_AWR(sql_id => '{g.Key}', format => 'TYPICAL +PEEKED_BINDS'));")],
                Docs = [D.SqlTuningAdvisor, D.DbmsXplan],
                SourceViews = ["DBA_HIST_ACTIVE_SESS_HISTORY", "DBA_HIST_SQL_PLAN"],
                TableRefs = [t.Id]
            });
        }
    }
}

/// <summary>Where the memory reads, full scans and read-consistency work are concentrated, and hints about statistics.</summary>
public sealed class HotspotRules : IRule
{
    public string Name => "Hot objects and access paths";

    public void Evaluate(AnalysisContext ctx)
    {
        HotLogicalReads(ctx);
        ReadConsistency(ctx);
        FullScans(ctx);
        StatisticsHints(ctx);
    }

    /// <summary>Segments by Logical Reads, tied to the statements that reference those objects.</summary>
    private static void HotLogicalReads(AnalysisContext ctx)
    {
        var seg = ctx.Report.Table("Segments by Logical Reads");
        var gets = ctx.Report.Table("SQL ordered by Gets");
        if (seg == null || seg.Rows.Count == 0) return;
        var hot = seg.DataRows.Where(r => (r.Num("%Total") ?? 0) >= 10).Take(3).ToList();
        if (hot.Count == 0) return;
        var first = hot[0];
        var pct = first.Num("%Total") ?? 0;
        var evidence = new List<Evidence>();
        var sqlIds = new List<string>();
        foreach (var h in hot)
        {
            var obj = h.Str("Object Name");
            evidence.Add(new($"Hot {h.Str("Obj. Type").ToLowerInvariant()}", RuleHelpers.SegmentName(h), $"{h.Str("Logical Reads")} logical reads, {h.Str("%Total")}% of all"));
            var rx = new Regex(@"\b" + Regex.Escape(obj) + @"\b", RegexOptions.IgnoreCase);
            if (gets == null) continue;
            foreach (var g in gets.DataRows.Take(25))
            {
                var id = g.Str("SQL Id");
                var text = ctx.SqlTexts.TryGetValue(id, out var full) ? full : g.Str("SQL Text");
                if (!rx.IsMatch(text) || sqlIds.Contains(id)) continue;
                sqlIds.Add(id);
                if (sqlIds.Count <= 5)
                    evidence.Add(new($"Statement reading {obj}", id, $"{g.Str("%Total")}% of buffer gets, {g.Str("Gets per Exec")} gets per execution, {g.Str("Executions")} executions"));
            }
        }
        ctx.Add(new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, pct >= 30 ? Severity.Medium : Severity.Low),
            Category = "SQL",
            Impact = pct,
            Title = $"{RuleHelpers.SegmentName(first)} receives {P(pct)} of all memory reads",
            Plain = "A few tables or indexes receive a very large share of the data reads from memory. Reading memory is fast, but doing it hundreds of millions of times uses a lot of processor time, and it often means the same small lookup table is read again and again."
                    + (sqlIds.Count > 0 ? $" The statements below read these objects." : ""),
            WhyItMatters = "These objects are where tuning pays off: making the statements that read them touch fewer blocks lowers CPU use for everybody and shortens the busiest jobs.",
            Technical = $"Segments by Logical Reads (DBA_HIST_SEG_STAT): top segment {P(pct)} of logical reads. Check the plans of the statements for full scans or nested-loop probes of this object, redundant repeated lookups (cache the value in the application or with the result cache), and non-selective index range scans. Hot blocks of such objects also drive 'latch: cache buffers chains' waits.",
            Evidence = evidence,
            Recommendations =
            [
                "Look at the execution plans of the statements listed (or find them with the SQL below) and check how this object is accessed: full scan or index, and how often per row processed.",
                "If it is a small lookup table queried per row, read it once and cache the result (PL/SQL collection, RESULT_CACHE hint or function) instead of querying it millions of times.",
                "Make sure the optimizer statistics of the object are current and that the access path is selective."
            ],
            Sql =
            [
                new($"Statements that reference {first.Str("Object Name")} and their buffer gets (AWR)", $"SELECT p.sql_id, p.operation, p.options, SUM(s.buffer_gets_delta) AS buffer_gets, SUM(s.executions_delta) AS executions\nFROM   dba_hist_sql_plan p\n       JOIN dba_hist_sqlstat s ON s.sql_id = p.sql_id AND s.plan_hash_value = p.plan_hash_value AND s.dbid = p.dbid\nWHERE  p.object_owner = '{first.Str("Owner")}' AND p.object_name = '{first.Str("Object Name")}'\nAND    s.snap_id BETWEEN &begin_snap AND &end_snap\nGROUP  BY p.sql_id, p.operation, p.options\nORDER  BY buffer_gets DESC;", "In a CDB run this inside the PDB that owns the object. Add FETCH FIRST 10 ROWS ONLY (12c and later) to limit the output.")
            ],
            Docs = [D.AccessPaths, D.ReadingPlans, D.InstanceTuning],
            SourceViews = ["DBA_HIST_SEG_STAT", "DBA_HIST_SQLSTAT", "DBA_HIST_SQL_PLAN"],
            TableRefs = new[] { seg.Id, gets?.Id }.OfType<string>().ToList(),
            ChartRefs = ["seg-logical-reads"]
        });
    }

    /// <summary>
    /// 'data blocks consistent reads - undo records applied' per consistent get: how often a query must roll back changes
    /// made by other sessions to show a consistent snapshot. No documented threshold: above about 10 per 100 gets is unusual.
    /// </summary>
    private static void ReadConsistency(AnalysisContext ctx)
    {
        var undoApplied = ctx.InstanceStat("data blocks consistent reads - undo records applied");
        var gets = ctx.InstanceStat("consistent gets");
        if (undoApplied is not > 0 || gets is not > 100000) return;
        var ratio = undoApplied.Value / gets.Value;
        var perSec = ctx.InstanceStatPerSec("data blocks consistent reads - undo records applied") ?? 0;
        if (ratio < 0.1 || perSec < 5000) return;
        var bw = ctx.Report.Table("Buffer Wait Statistics");
        var undoWait = bw?.DataRows.Where(r => r.Label.StartsWith("undo", StringComparison.OrdinalIgnoreCase)).Sum(r => r.Num("Total Wait Time (s)", "Total Wait Time (sec)") ?? 0) ?? 0;
        var cleanouts = ctx.InstanceStatPerSec("cleanouts and rollbacks - consistent read gets");
        ctx.Add(new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, ratio >= 0.3 ? Severity.Medium : Severity.Low),
            Category = "SQL",
            Impact = ratio * 100,
            Title = $"Queries constantly rebuild older versions of blocks ({N(ratio * 100, 0)} undo records applied per 100 block reads)",
            Plain = "To show every query a consistent picture of the data, Oracle must undo the recent changes that other sessions made to the blocks being read. Here that extra work is needed for a noticeable share of all block reads (see the numbers below), which happens when many sessions read and change the same blocks at the same time.",
            WhyItMatters = "Each rebuilt block costs extra CPU and memory, and sessions queue for the same hot blocks, so this is a typical root cause behind high CPU together with 'buffer busy waits' on a busy batch system.",
            Technical = $"'data blocks consistent reads - undo records applied' = {Big(undoApplied)} ({Big(perSec)}/s) vs 'consistent gets' {Big(gets)} (ratio {N(ratio, 2)}); cleanouts and rollbacks - consistent read gets {Big(cleanouts)}/s; undo block/header buffer waits {Dur(undoWait)}. Rule of thumb (not an Oracle-documented threshold): above 0.1 deserves attention. Typical causes: long queries running against tables that are updated heavily, work-queue / interface tables read and updated by many concurrent processes, and transactions that stay open long.",
            Evidence = [new("Undo records applied per consistent get", N(ratio, 2)), new("Undo records applied per second", Big(perSec)), new("Consistent gets", Big(gets)), new("Undo block / undo header buffer waits", Dur(undoWait))],
            Recommendations =
            [
                "Shorten transactions and commit sooner on the tables that are read and updated at the same time (interface, queue and status tables).",
                "Reduce the number of concurrent workers or batch streams that touch the same tables, or have each worker process a different partition or key range.",
                "Reduce the rows per block of the hottest small tables (higher PCTFREE, or partitioning) so that fewer sessions share a block.",
                "Tune the statements that scan these tables repeatedly: each repeated scan multiplies the rollback work."
            ],
            Docs = [D.Concurrency, D.Undo, D.InstanceTuning, D.Statistics],
            SourceViews = ["DBA_HIST_SYSSTAT", "DBA_HIST_WAITSTAT"],
            TableRefs = new[] { ctx.Report.Table("Instance Activity Stats")?.Id, bw?.Id }.OfType<string>().ToList()
        });
    }

    private static void FullScans(AnalysisContext ctx)
    {
        var userIo = RuleHelpers.WaitClassPct(ctx, "User I/O") ?? 0;
        var scatter = (RuleHelpers.ForegroundEventPct(ctx, "db file scattered read") ?? 0) + (RuleHelpers.ForegroundEventPct(ctx, "direct path read") ?? 0);
        var longScans = ctx.InstanceStatPerSec("table scans (long tables)") ?? 0;
        var scans = ctx.Report.Table("Segments by Table Scans");
        var topScan = scans.FirstRow();
        var scanShare = topScan.Val("%Total") ?? 0;
        if (!(userIo >= 5 || scatter >= 3) || !(longScans >= 0.1 || scanShare >= 50)) return;

        var reads = ctx.Report.Table("Segments by Physical Reads");
        var ev = new List<Evidence> { new("Long full table scans per second", N(longScans, 2)), new("Scattered + direct path read share of DB time", P(scatter)), new("User I/O share of DB time", P(userIo)) };
        if (scans != null) ev.AddRange(scans.DataRows.Take(3).Select(r => new Evidence("Table scanned", RuleHelpers.SegmentName(r), $"{r.Str("Table Scans")} scans, {r.Str("%Total")}% of all")));
        if (reads != null) ev.AddRange(reads.DataRows.Take(2).Select(r => new Evidence("Most physical reads", RuleHelpers.SegmentName(r), $"{r.Str("%Total")}% of physical reads")));
        ctx.Add(new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, userIo >= 25 ? Severity.Medium : Severity.Low),
            Category = "SQL",
            Impact = Math.Max(userIo, scatter),
            Title = $"Large tables are read in full again and again ({N(longScans, 1)} long scans per second)",
            Plain = "Some statements read an entire large table from start to end (a 'full table scan') instead of using an index to jump to the few rows they need.",
            WhyItMatters = "A full scan of a big table reads far more data than needed, takes disk bandwidth and memory away from everyone else, and gets slower as the table grows.",
            Technical = $"'table scans (long tables)' {N(longScans, 2)}/s (Instance Activity); scattered/direct path reads {P(scatter)} of DB time; top scanned segment {(topScan is { } ts ? RuleHelpers.SegmentName(ts) : "n/a")}. Check for missing indexes, functions or implicit conversions on indexed columns, stale statistics, and partition pruning.",
            Evidence = ev,
            Recommendations = ["Find the statements that scan these tables ('SQL ordered by Reads', ASH) and check whether an index or partition pruning would serve them.", "Verify optimizer statistics are current for the scanned tables.", "For legitimate reporting scans, schedule them off-peak or use parallel query/partitioning."],
            Docs = [D.AccessPaths, D.ReadingPlans, D.GatherStats],
            SourceViews = ["DBA_HIST_SEG_STAT", "DBA_HIST_SQLSTAT", "DBA_HIST_SYSSTAT"],
            TableRefs = new[] { scans?.Id, reads?.Id }.OfType<string>().ToList()
        });
    }

    /// <summary>
    /// AWR contains no statistics-staleness data, so this is a hint, raised only when the symptoms point at it:
    /// a leaf statement with a very high number of gets per execution, and/or heavy DML churn on a few tables.
    /// </summary>
    private static void StatisticsHints(AnalysisContext ctx)
    {
        var gets = ctx.Report.Table("SQL ordered by Gets");
        var suspects = gets?.DataRows.Where(r => !RuleHelpers.IsPlsqlText(ctx.SqlTexts.TryGetValue(r.Str("SQL Id"), out var tx) ? tx : r.Str("SQL Text"))
                                                 && (r.Num("Gets per Exec") ?? 0) >= 20000 && (r.Num("%Total") ?? 0) >= 3 && (r.Num("Executions") ?? 0) >= 100).Take(3).ToList() ?? [];
        var dml = ctx.Report.Table("Segments by DB Blocks Changes");
        var churn = dml?.DataRows.Where(r => (r.Num("% of Capture") ?? 0) >= 25).Take(2).ToList() ?? [];
        if (suspects.Count == 0 && churn.Count == 0) return;
        if (suspects.Count == 0 && churn.Count > 0 && !ctx.Findings.Any(f => f.Category is "SQL" or "CPU")) return;
        var gtt = suspects.Any(r => Regex.IsMatch(ctx.SqlTexts.TryGetValue(r.Str("SQL Id"), out var tx) ? tx : r.Str("SQL Text"), @"\b\w+_GT\b", RegexOptions.IgnoreCase));
        var autoStatsOff = RuleHelpers.Param(ctx, "_optimizer_autostats_job")?.Equals("FALSE", StringComparison.OrdinalIgnoreCase) == true;
        var ev = new List<Evidence>();
        ev.AddRange(suspects.Select(r => new Evidence("Statement with very many reads per execution", r.Str("SQL Id"), $"{r.Str("Gets per Exec")} buffer gets per execution, {r.Str("Executions")} executions, {r.Str("%Total")}% of all gets")));
        ev.AddRange(churn.Select(r => new Evidence("Table/index with heavy change volume", RuleHelpers.SegmentName(r), $"{r.Str("% of Capture")}% of all block changes")));
        if (autoStatsOff) ev.Add(new("_optimizer_autostats_job", "FALSE", "the automatic optimizer statistics job is disabled; statistics must be gathered by another process"));
        ctx.Add(new Finding
        {
            Severity = Severity.Low,
            Category = "SQL",
            Title = "Check that optimizer statistics are current on the busiest tables",
            Plain = "The optimizer chooses execution plans from statistics about the tables (row counts, value distribution). Some statements here read an unusually large number of blocks per execution, and a few tables change very quickly, which is when statistics are most likely to be out of date and to produce poor plans."
                    + (gtt ? " One of the statements works on a temporary working table (name ending in _GT), whose statistics are often missing or wrong." : ""),
            WhyItMatters = "A stale statistic can make a statement choose a much slower plan, and the damage is invisible in the report. Checking takes minutes.",
            Technical = "This is a hint, not a measurement: AWR does not record statistics age. Heuristic: leaf statement with >= 20,000 buffer gets per execution and >= 3% of all gets, or a segment with >= 25% of all block changes."
                        + (autoStatsOff ? " The hidden parameter _optimizer_autostats_job=FALSE disables the automatic statistics job (this is the setting recommended for E-Business Suite, which gathers statistics with its own 'Gather Schema Statistics' / FND_STATS programs, so verify that those jobs run instead of enabling the automatic job)." : "")
                        + (gtt ? " For global temporary tables, 12c+ supports session-private statistics (DBMS_STATS.SET_TABLE_PREFS(..., 'GLOBAL_TEMP_TABLE_STATS', 'SESSION'))." : ""),
            Evidence = ev,
            Recommendations = ["Check last_analyzed and stale_stats for the tables used by the statements above (SQL below) and re-gather where stale.", "Make sure a statistics job (automatic or application-specific) really runs after bulk loads and interface-table churn.", "If plans changed recently, compare plan_hash_value over time (DBMS_XPLAN.DISPLAY_AWR) before and after gathering."],
            Sql =
            [
                new("Tables with stale statistics", "EXEC DBMS_STATS.FLUSH_DATABASE_MONITORING_INFO;\nSELECT owner, table_name, num_rows, last_analyzed, stale_stats\nFROM   dba_tab_statistics\nWHERE  stale_stats = 'YES' AND partition_name IS NULL AND owner NOT IN ('SYS', 'SYSTEM')\nORDER  BY last_analyzed NULLS FIRST;"),
                new("Is the automatic statistics job enabled, and what did it do last? (the second query needs 11.2 or later)", "SELECT client_name, status FROM dba_autotask_client WHERE client_name = 'auto optimizer stats collection';\nSELECT operation, target, start_time, end_time FROM dba_optstat_operations ORDER BY start_time DESC;")
            ],
            Docs = [D.OptimizerStats, D.GatherStats, D.DbmsStats],
            SourceViews = ["DBA_HIST_SQLSTAT", "DBA_HIST_SEG_STAT"],
            TableRefs = new[] { gets?.Id, dml?.Id }.OfType<string>().ToList()
        });
    }
}
