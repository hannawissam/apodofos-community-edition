// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text.RegularExpressions;
using Apodofos.Core.Knowledge;
using Apodofos.Core.Model;
using static Apodofos.Core.Analysis.AnalysisContext;
using static Apodofos.Core.Analysis.Rules.AshHelpers;
using static Apodofos.Core.Analysis.Rules.RuleHelpers;
using D = Apodofos.Core.Knowledge.OracleDocs;

namespace Apodofos.Core.Analysis.Rules;

/// <summary>ASH events: Top User/Background Events, wait classes, decoded P1/P2/P3 and parallel-query patterns.</summary>
public sealed class AshEventRules : IRule
{
    public string Name => "ASH events";
    public ReportKind[] Kinds => [ReportKind.Ash];

    public void Evaluate(AnalysisContext ctx)
    {
        UserEvents(ctx);
        WaitClasses(ctx);
        BackgroundEvents(ctx);
        Parameters(ctx);
        Parallel(ctx);
    }

    /// <summary>Knowledge-base actions point at AWR sections that an ASH report does not have; keep only the portable ones.</summary>
    internal static IEnumerable<string> AshActions(string[]? actions, string fallback)
    {
        var keep = (actions ?? []).Where(a => !Regex.IsMatch(a, @"SQL ordered by|File IO Stats|Advisory|Foreground|Segments by|Top 10|AWR|Instance Activity|Load Profile", RegexOptions.IgnoreCase)).ToList();
        return keep.Count > 0 ? keep : [fallback];
    }

    private static void UserEvents(AnalysisContext ctx)
    {
        var t = T(ctx, "Top User Events");
        if (t == null) return;
        foreach (var r in t.DataRows.Where(r => !IsCpu(r.Str("Event")) && !r.Str("Event").StartsWith("PX ", StringComparison.OrdinalIgnoreCase) && Pct(r) >= 5).OrderByDescending(Pct).Take(5))
        {
            var ev = r.Str("Event");
            var cls = r.Str("Event Class", "Wait Class");
            var pct = Pct(r);
            var k = WaitEventKnowledge.Find(ev);
            var (clsPlain, clsAdvice) = WaitEventKnowledge.WaitClass(cls);
            var sess = r.Val("Avg Active Sessions") ?? Sessions(ctx, pct);
            ctx.Add(new Finding
            {
                Severity = Sev(ctx, pct),
                Category = "Wait events",
                Impact = pct,
                Title = $"'{ev}' held back sessions in {Pc(pct)} of sampled activity",
                Plain = k?.Plain ?? $"Sessions spent {Pc(pct)} of the sampled time waiting on '{ev}' ({cls}). {clsPlain}",
                WhyItMatters = k?.Why ?? "Time spent waiting is time a user or job is not getting work done; the biggest wait is where tuning pays off first.",
                Technical = (k?.Technical ?? $"Event class {cls}. {clsAdvice}") + $" In this window the event accounted for {N(sess, 2)} average active sessions ({Pc(pct)} of samples).",
                Evidence = [new("Event", ev, cls), new("Share of sampled activity", P(pct)), new("Average active sessions", N(sess, 2))],
                Recommendations = [.. AshActions(k?.Actions, clsAdvice), "Use the SQL below, then the Top SQL and Top Sessions sections, to see who was waiting."],
                Sql = [ByEvent(ctx, ev)],
                Docs = [.. (k?.Docs ?? [D.WaitEvents]), D.Transient],
                SourceViews = ["V$ACTIVE_SESSION_HISTORY"],
                ChartRefs = ["ash-top-events"],
                TableRefs = [t.Id]
            });
        }
    }

    /// <summary>Concurrency, application, configuration ... classes taken together: what the contention is about.</summary>
    private static void WaitClasses(AnalysisContext ctx)
    {
        var t = T(ctx, "Top User Events");
        if (t == null) return;
        var by = t.DataRows.Where(r => !IsCpu(r.Str("Event")))
            .GroupBy(r => r.Str("Event Class", "Wait Class"), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Class: g.Key, Pct: g.Sum(Pct), Events: g.OrderByDescending(Pct).Take(3).Select(x => $"{x.Str("Event")} ({N(Pct(x), 1)}%)").ToList()))
            .ToDictionary(x => x.Class, StringComparer.OrdinalIgnoreCase);

        void Class(string name, double min, Severity cap, string title, string plain, string why, string technical, string[] recs, SqlSnippet[]? sql = null)
        {
            if (!by.TryGetValue(name, out var c) || c.Pct < min) return;
            ctx.Add(new Finding
            {
                Severity = Sev(ctx, c.Pct, cap),
                Category = "Wait events",
                Impact = c.Pct,
                Title = title.Replace("{p}", Pc(c.Pct)),
                Plain = plain.Replace("{p}", Pc(c.Pct)),
                WhyItMatters = why,
                Technical = technical + $" Events: {string.Join(", ", c.Events)}.",
                Evidence = [new($"{name} class", P(c.Pct), "share of sampled activity"), .. c.Events.Select(e => new Evidence("Event", e))],
                Recommendations = [.. recs],
                Sql = sql is null ? [] : [.. sql],
                Docs = [D.WaitClasses, D.Concurrency],
                SourceViews = ["V$ACTIVE_SESSION_HISTORY"],
                ChartRefs = ["ash-wait-classes"],
                TableRefs = [t.Id]
            });
        }

        Class("Application", 10, Severity.High, "Application waits (locks) took {p} of sampled activity",
            "A large part of the time sessions were waiting for locks held by other sessions, such as two users changing the same row or an uncommitted transaction that blocks others.",
            "Users see their screens freeze until the other transaction finishes. This is almost always an application or transaction design matter, not hardware.",
            "Application class: enq: TX - row lock contention, enq: TM, lock-type enqueues. Combine with Top Blocking Sessions and the decoded P1 (lock type and mode).",
            ["Find and fix the blocking session or long transaction (see Top Blocking Sessions).", "Commit sooner; avoid user think-time inside open transactions; index foreign keys to avoid TM locks."],
            [new("Current blockers right now (live)", "SELECT blocking_session, sid, serial#, event, seconds_in_wait, sql_id\nFROM   v$session\nWHERE  blocking_session IS NOT NULL\nORDER  BY seconds_in_wait DESC;")]);
        Class("Concurrency", 10, Severity.High, "Contention inside the database took {p} of sampled activity",
            "Sessions were competing for the same internal resources (the same data blocks, memory structures or SQL parsing locks) rather than for CPU or disk.",
            "Contention does not go away with bigger hardware: more CPUs usually make it worse, and it limits how many users the system can serve at once.",
            "Concurrency class: buffer busy waits, latch/mutex waits, library cache and cursor pin waits. Use P1/P2/P3 and Top DB Objects to find the hot block or structure.",
            ["Identify the hot object or latch in Top DB Objects / Top Latches.", "Reduce hard parsing and literal SQL; consider hash partitioning or reverse key indexes for hot blocks."]);
        Class("Configuration", 5, Severity.Medium, "Configuration-related waits took {p} of sampled activity",
            "Sessions waited because of sizing choices such as redo log size, sequence caches or free space handling.",
            "These waits come from settings that can usually be changed cheaply once identified.",
            "Configuration class: log file switch, free buffer waits, enq: HW/SQ/ST/TX allocate ITL.",
            ["Check redo log size and number of groups, sequence CACHE values, and INITRANS/ASSM settings for the objects involved."]);
        Class("Cluster", 10, Severity.High, "RAC interconnect (cluster) waits took {p} of sampled activity",
            "Sessions waited for data blocks to travel between RAC nodes.",
            "Cross-node traffic is slower than local memory; heavy sharing of the same blocks between nodes slows both.",
            "Cluster class: gc buffer busy, gc cr/current block requests.",
            ["Run the application so each node works on its own data (service affinity) and check interconnect latency."]);
        Class("Commit", 5, Severity.High, "Commit waits took {p} of sampled activity",
            "Sessions waited for the redo log to confirm that their commits were safely written.",
            "Every commit-heavy program is slowed by log write latency.",
            "Commit class: log file sync. Compare with log file parallel write latency and commit frequency.",
            ["Commit in batches where the business logic allows it; place redo logs on the fastest storage."]);
        Class("Scheduler", 5, Severity.Medium, "Resource Manager throttling took {p} of sampled activity",
            "The Resource Manager deliberately held sessions back to share CPU between consumer groups.",
            "The database was not out of resources: a resource plan limited some sessions.",
            "Scheduler class: resmgr:cpu quantum.",
            ["Review the active resource plan and the CPU allocation of the affected consumer group."]);
        Class("Network", 15, Severity.Medium, "Network waits took {p} of sampled activity",
            "A noticeable part of activity was waiting for the client or another database to send or accept data.",
            "Large result sets and chatty applications make the database wait for the network instead of working.",
            "Network class: SQL*Net message from dblink, SQL*Net more data to client.",
            ["Reduce round trips (array fetch/bulk operations) and check network latency."]);
    }

    private static void BackgroundEvents(AnalysisContext ctx)
    {
        var t = T(ctx, "Top Background Events");
        if (t == null) return;
        foreach (var r in t.DataRows.Where(r => !IsCpu(r.Str("Event")) && Pct(r) >= 5).OrderByDescending(Pct).Take(2))
        {
            var ev = r.Str("Event");
            var k = WaitEventKnowledge.Find(ev);
            ctx.Add(new Finding
            {
                Severity = Sev(ctx, Pct(r), Severity.Low),
                Category = "Background processes",
                Impact = Pct(r) / 2,
                Title = $"Background process wait '{ev}' in {Pc(Pct(r))} of sampled activity",
                Plain = k?.Plain ?? $"Oracle's own background processes spent time on '{ev}' ({r.Str("Event Class")}). This is normally harmless unless users also wait for it.",
                WhyItMatters = "Background waits matter when they slow down user sessions (for example a slow log writer delays commits); on their own they are usually not user-visible.",
                Technical = (k?.Technical ?? "Background wait event.") + " Cross-check against the user events: if user sessions wait on the matching foreground event, treat this as the root cause.",
                Evidence = [new("Event", ev, r.Str("Event Class")), new("Share of sampled activity", P(Pct(r)))],
                Recommendations = [.. AshActions(k?.Actions, "Check whether user sessions wait on the related foreground event.")],
                Docs = [.. (k?.Docs ?? [D.WaitEvents])],
                SourceViews = ["V$ACTIVE_SESSION_HISTORY"],
                TableRefs = [t.Id]
            });
        }
    }

    // ------------------------------------------------------------------------------ P1/P2/P3 decoded

    internal static (string Type, int Mode) DecodeEnqueue(long p1) => ($"{(char)((p1 >> 24) & 0xFF)}{(char)((p1 >> 16) & 0xFF)}", (int)(p1 & 0xFFFF));

    private static string EnqueueMeaning(string type, int mode) => (type, mode) switch
    {
        ("TX", 6) => "row lock: another transaction holds the same row (application locking)",
        ("TX", 4) => "share mode: usually a shortage of ITL slots, a duplicate key being inserted by two sessions, or a bitmap index conflict",
        ("TM", 3 or 5) => "table lock from DML or an unindexed foreign key",
        ("TM", 6) => "exclusive table lock (DDL or LOCK TABLE) against running DML",
        ("HW", _) => "high-water mark extension of a segment (space allocation)",
        ("SQ", _) => "sequence number cache exhausted",
        ("US", _) => "undo segment management",
        _ => "lock type " + type + ", mode " + mode
    };

    private static string BlockClass(long c) => c switch
    {
        1 => "data block", 2 => "sort block", 3 => "save undo block", 4 => "segment header", 5 => "save undo header", 6 => "free list",
        7 => "extent map", 8 or 9 or 10 => "bitmap/space management block", 11 or 12 => "bitmap block", 13 => "file header block",
        15 => "system undo header", 16 => "system undo block", >= 17 => "undo header or block of an undo segment", _ => "block class " + c
    };

    private static List<string> Vals(string s) => Regex.Matches(s, "\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();

    private static void Parameters(AnalysisContext ctx)
    {
        var t = T(ctx, "Top Event P1/P2/P3 Values");
        if (t == null || t.Rows.Count == 0) return;
        var valCol = t.Col("P1 Value, P2 Value, P3 Value", "P1 Value");
        if (valCol < 0) return;
        var evidence = new List<Evidence>();
        var recs = new List<string>();
        var sql = new List<SqlSnippet>();
        string? hotFile = null;
        double bestPct = 0;
        foreach (var r in t.DataRows.Take(8))
        {
            var ev = r.Str("Event");
            var vals = Vals(r.StrAt(valCol));
            if (ev.Length == 0 || vals.Count == 0) continue;
            var names = new[] { r.Str("Parameter 1"), r.Str("Parameter 2"), r.Str("Parameter 3") };
            var decoded = string.Join(", ", vals.Select((v, i) => $"{(i < 3 && names[i].Length > 0 ? names[i] : "p" + (i + 1))}={v}"));
            var note = "";
            var pct = r.Val("% Activity") ?? 0;
            if (ev.StartsWith("enq:", StringComparison.OrdinalIgnoreCase) && long.TryParse(vals[0], out var p1))
            {
                var (type, mode) = DecodeEnqueue(p1);
                note = $"{type} lock, mode {mode}: {EnqueueMeaning(type, mode)}";
                if (type == "TX" && mode == 4) recs.Add("Lock mode 4 on TX points to ITL shortage or duplicate-key inserts: raise INITRANS/PCTFREE on the hot segment or check unique-key races.");
                if (type == "TM") recs.Add("TM locks usually mean foreign key columns without an index: create an index on the child table's FK columns.");
            }
            else if (ev.Contains("cursor: pin", StringComparison.OrdinalIgnoreCase) && vals.Count > 0)
            {
                note = $"cursor hash value {vals[0]}";
                sql.Add(new("Which statement has this cursor hash value", $"SELECT sql_id, hash_value, SUBSTR(sql_text, 1, 100) AS sql_text FROM v$sqlarea WHERE hash_value = {Regex.Replace(vals[0], "[^0-9]", "")};"));
            }
            else if ((ev.Contains("buffer busy") || ev.Contains("read by other session")) && vals.Count > 2 && long.TryParse(vals[2], out var cls))
                note = $"block class: {BlockClass(cls)}";
            if (names[0].StartsWith("file", StringComparison.OrdinalIgnoreCase) && pct >= bestPct && int.TryParse(vals[0], out _)) { hotFile = vals[0]; bestPct = pct; }
            evidence.Add(new Evidence($"{ev} ({N(r.Val("% Event"), 1)}% of activity)", decoded, note.Length > 0 ? note : null));
        }
        if (evidence.Count == 0) return;
        if (hotFile != null)
            sql.Add(new("Which data file and segment the waits point to", $"SELECT file_id, tablespace_name, file_name FROM dba_data_files WHERE file_id = {hotFile}\nUNION ALL\nSELECT file_id, tablespace_name, file_name FROM dba_temp_files WHERE file_id = {hotFile};\n\n-- Segment holding a block (replace &block with the block# value):\nSELECT owner, segment_name, segment_type\nFROM   dba_extents\nWHERE  file_id = {hotFile} AND &block BETWEEN block_id AND block_id + blocks - 1;",
                "Searching DBA_EXTENTS can be slow on very large databases. Temp file ids are offset by db_files in ASH."));
        var topEv = t.DataRows.First();
        ctx.Add(new Finding
        {
            Severity = Severity.Info,
            Category = "Wait events",
            Impact = Math.Min(30, t.DataRows.Sum(r => r.Val("% Event") ?? 0)),
            Title = "What the top waits were waiting on (decoded parameters)",
            Plain = "Each wait event carries three extra values that say which file, block, lock or cursor the session was waiting for. They are translated here so you can go straight to the object or lock involved.",
            WhyItMatters = "Knowing that the waits concentrate on one file, one block class or one lock mode turns a general slowdown into a specific, fixable cause.",
            Technical = "Top Event P1/P2/P3 Values: parameter names come from V$EVENT_NAME. Enqueue P1 encodes lock type (two characters) and mode in its high and low words; buffer busy / read by other session P3 is the block class.",
            Evidence = evidence,
            Recommendations = [.. recs.Distinct(), "Use the file# and block# values with the SQL below to find the segment, then check whether it is a hot table or index."],
            Sql = sql,
            Docs = [D.VEventName, D.WaitEvents],
            SourceViews = ["V$EVENT_NAME", "V$ACTIVE_SESSION_HISTORY"],
            TableRefs = [t.Id]
        });
    }

    // ------------------------------------------------------------------------------ parallel query

    private static void Parallel(AnalysisContext ctx)
    {
        var ev = T(ctx, "Top User Events");
        var px = ev?.DataRows.Where(r => r.Str("Event").StartsWith("PX ", StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        var pqSessions = T(ctx, "Top Sessions running PQs");
        var pct = px.Sum(Pct);
        var pqPct = pqSessions?.DataRows.Select(r => r.Val("% Activity") ?? 0).DefaultIfEmpty(0).Max() ?? 0;
        if (pct < 5 && pqPct < 15) return;
        var worst = px.OrderByDescending(Pct).FirstOrDefault();
        var share = Math.Max(pct, pqPct);
        // PX coordinator / idle-consumer waits and the PX Nsq load poll are the machinery of parallel execution, mostly idle time. On a
        // near-idle system they are noise (often a monitoring tool polling the parallel servers), not a problem to tune.
        var benign = new Regex("^PX (Nsq|Idle Wait|Deq: (Join ACK|Parse Reply|Execute Reply|Signal ACK|Msg Fragment|Table Q Normal|Execution Msg)|Deq Credit)", RegexOptions.IgnoreCase);
        var nearIdle = ctx.Aas is < 0.5 || (ctx.Cpus is > 0 && ctx.Aas / ctx.Cpus < 0.1);
        var quiet = px.Count > 0 && px.All(r => benign.IsMatch(r.Str("Event"))) && pqPct < 15 && nearIdle;
        var poller = px.Any(r => r.Str("Event").StartsWith("PX Nsq", StringComparison.OrdinalIgnoreCase));
        var sev = quiet ? Severity.Info : Sev(ctx, share, Severity.Medium);
        ctx.Add(new Finding
        {
            Severity = sev,
            Category = "Parallel execution",
            Impact = share,
            Title = quiet ? $"Parallel-query coordinator waits ({Pc(pct)} of samples) on a nearly idle system: normal"
                  : pct >= 5 ? $"Parallel query coordination waits took {Pc(pct)} of sampled activity" : $"Parallel queries drove {Pc(pqPct)} of sampled activity",
            Plain = pct >= 5
                ? "A large part of the sampled time was spent in parallel-query machinery: the coordinator or helper processes waiting on each other instead of working. This is typical when a parallel query is too wide, is starved of resources, or one helper is slower than the rest."
                : "One session running parallel queries (through its helper processes) accounts for a large part of all activity.",
            WhyItMatters = "Parallel queries use many processes at once. A few of them can crowd out everybody else, and badly balanced ones burn resources while finishing no sooner.",
            Technical = (px.Any() ?$"Top PX event '{worst.Str("Event")}' {P(Pct(worst))}. " : "") + "PX Nsq / PX Deq Credit: send blkd mean consumers or the query coordinator were slow to accept rows; skew shows up as one slave busy while the rest wait. Check DOP, parallel_max_servers, Resource Manager limits and parallel statement queuing.",
            Evidence = [.. px.Select(r => new Evidence(r.Str("Event"), P(Pct(r)))), .. (pqPct > 0 ? new[] { new Evidence("Busiest parallel-query session", P(pqPct)) } : [])],
            Recommendations = [.. (quiet ? new[] { poller
                    ? "'PX Nsq' is a session polling the load of the parallel servers (the PQ load-info query), not a wait on a database object: typically a monitoring tool, a scheduler job or parallel statement queuing: identify the program in Top Sessions; nothing needs tuning if the system is otherwise idle."
                    : "These are waits of the parallel-query coordinator for its servers (or idle servers waiting for work): normal while a parallel statement runs and not a wait on a database object. No action is needed on a nearly idle system." }
                : new[] { "These are waits of the query coordinator or of idle parallel servers, not waits on a database object: tune the statement behind them and its degree of parallelism rather than searching for a hot segment." }),
                "Lower the degree of parallelism for statements that do not need it (ALTER TABLE ... PARALLEL n, hints, PARALLEL_DEGREE_POLICY).", "Cap parallel servers per consumer group with Resource Manager so one query cannot take all of them.", "Check for skewed data or a slow consumer when 'PX Deq Credit: send blkd' dominates."],
            Sql = [new("Parallel server use per query coordinator (live)", "SELECT qcsid, COUNT(*) AS slaves, MAX(req_degree) AS requested_dop, MAX(degree) AS actual_dop\nFROM   v$px_session\nWHERE  sid <> qcsid\nGROUP  BY qcsid;"),
                new("Parallel activity in the window (ASH)", $"SELECT qc_session_id, sql_id, COUNT(DISTINCT session_id) AS slaves, {Weight(ctx)} AS approx_db_seconds\nFROM   {View(ctx)}\nWHERE  {Window(ctx)} AND qc_session_id IS NOT NULL\nGROUP  BY qc_session_id, sql_id\nORDER  BY approx_db_seconds DESC;", HistoryCaution)],
            Docs = [D.ParallelExecution, D.VPxSession, D.ResourceManager],
            SourceViews = ["V$ACTIVE_SESSION_HISTORY", "V$PX_SESSION"],
            TableRefs = [.. new[] { ev?.Id, pqSessions?.Id }.Where(i => i != null).Select(i => i!)]
        });
    }
}
