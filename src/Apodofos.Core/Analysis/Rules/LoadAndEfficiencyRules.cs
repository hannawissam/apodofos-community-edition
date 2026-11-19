// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using static Apodofos.Core.Analysis.AnalysisContext;
using static Apodofos.Core.Analysis.Rules.RuleHelpers;
using D = Apodofos.Core.Knowledge.OracleDocs;

namespace Apodofos.Core.Analysis.Rules;

/// <summary>Overall load (CPU demand vs capacity), host I/O wait, Load Profile, Instance Efficiency and Time Model checks.</summary>
public sealed class LoadAndEfficiencyRules : IRule
{
    public string Name => "Load and efficiency";

    public void Evaluate(AnalysisContext ctx)
    {
        Load(ctx);
        HostIoWait(ctx);
        Parsing(ctx);
        Logons(ctx);
        Rollbacks(ctx);
        Efficiency(ctx);
        TimeModel(ctx);
    }

    /// <summary>
    /// Capacity check. Oracle's DB time counts sessions that WAIT as well as sessions that run, so Average Active Sessions
    /// above the CPU count does not by itself mean the CPUs are saturated (many sessions can be waiting on locks).
    /// The verdict therefore rests on CPU demand (DB CPU + background CPU per second) and on host CPU busy %,
    /// including the busiest slice of the window (averages hide peaks).
    /// </summary>
    private static void Load(AnalysisContext ctx)
    {
        if (ctx.Aas is not { } aas) return;
        var host = ctx.Report.TableStartingWith("Host CPU");
        var cpus = ctx.Cpus;
        var cores = ctx.Info.Cores ?? (int?)host.FirstRow().Val("Cores");
        var smt = cores is > 0 && cpus is > 0 && cores < cpus;
        var cpuAas = (ctx.LoadPerSec("DB CPU(s)") ?? 0) + (ctx.LoadPerSec("Background CPU(s)") ?? 0);
        var idle = RuleHelpers.HostIdle(ctx);
        var busy = idle is null ? (double?)null : 100 - idle;
        double? cpuRatio = cpus is > 0 && cpuAas > 0 ? cpuAas / cpus : null;
        double? coreRatio = smt && cpuAas > 0 ? cpuAas / cores!.Value : null;
        var aasRatio = cpus is > 0 ? aas / cpus.Value : (double?)null;

        // Busiest slice of the window (Operating System Statistics - Detail has one row per snapshot).
        var det = ctx.Report.Table("Operating System Statistics - Detail");
        string peakFrom = "", peakTo = "";
        double peak = -1, peakLoad = 0;
        if (det != null)
        {
            var prev = "";
            foreach (var r in det.DataRows)
            {
                var when = r.Str("Snap Time");
                if (r.Num("%busy") is { } b && b > peak) { peak = b; peakFrom = prev; peakTo = when; peakLoad = r.Num("Load") ?? 0; }
                prev = when;
            }
        }

        var sev = Severity.Ok;
        void Bump(Severity s) { if (s < sev) sev = s; }
        if (busy >= 95 || cpuRatio >= 1.5) Bump(Severity.Critical);
        else if (busy >= 90 || cpuRatio >= 1.0) Bump(Severity.High);
        else if (busy >= 80 || cpuRatio >= 0.8) Bump(Severity.Medium);
        // A snapshot-slice average is not an instantaneous peak: only a slice that is almost fully busy is serious.
        if (peak >= 97) Bump(Severity.High);
        else if (peak >= 90) Bump(Severity.Medium);
        else if (peak >= 75) Bump(Severity.Low);
        // Far more active sessions than CPUs: even when many are waiting, response time is dominated by queuing.
        var overloaded = aasRatio >= 1.5 && !(busy >= 80 || cpuRatio >= 0.8);
        if (aasRatio >= 4) Bump(Severity.High);
        else if (aasRatio >= 2) Bump(Severity.Medium);
        else if (aasRatio >= 1.5) Bump(Severity.Low);
        if (smt && coreRatio >= 0.7) Bump(Severity.Low);

        var peakText = peak >= 0 && peakFrom.Length > 0 ? $"{peakFrom} to {peakTo}" : peakTo;
        var avgSaturated = busy >= 80 || cpuRatio >= 0.8;
        var title = sev switch
        {
            Severity.Ok => $"Overall load is within capacity ({N(aas, 1)} active sessions on {N(cpus)} CPUs)",
            _ when avgSaturated => $"The server's CPUs are heavily used ({P(busy, 0)} busy on average, {N(cpuAas, 1)} sessions' worth of CPU on {N(cpus)} CPUs)",
            _ when overloaded => $"Far more sessions were active than the server has CPUs ({N(aas, 1)} active sessions on {N(cpus)} CPUs)",
            _ when peak >= 75 => $"CPU is comfortable on average ({P(busy, 0)} busy) but peaked at {P(peak, 0)} busy ({peakText})",
            _ => $"CPU headroom is limited on this hyper-threaded server ({N(cpuAas, 1)} sessions' worth of CPU on {N(cores)} physical cores)"
        };

        var plain = $"Added up across all sessions, the database worked or waited for {Dur(ctx.DbTimeSec)} during {Dur(ctx.ElapsedSec)} of wall-clock time. "
                    + $"That equals about {N(aas, 1)} sessions being active at every moment (Oracle calls this 'Average Active Sessions'). "
                    + (cpus is > 0 ? $"The server has {N(cpus)} CPUs" + (smt ? $" (hyper-threads of {N(cores)} physical cores)" : "") + $"; the database used about {N(cpuAas, 1)} of them on average" + (busy is not null ? $" and the whole server was {P(busy, 0)} busy." : ".") : "");
        if (peak >= 75 && sev != Severity.Ok)
            plain += $" The busiest slice ({peakText}) was {P(peak, 0)} busy (load average {N(peakLoad, 1)}), so users may have noticed slowness in that slice even though the average looks fine.";
        if (aasRatio >= 1 && !avgSaturated)
            plain += cpuRatio >= 0.6
                ? $" About {N(aasRatio, 1)} times more sessions were active than there are CPUs. Many were waiting (disk, locks) rather than computing, but the database alone kept about {N(cpuAas, 1)} CPUs busy, so work also queues for processor time. Parallel-query processes count as active sessions too."
                : $" More sessions were active than there are CPUs, but most of them were waiting (for locks, memory contention, etc.) rather than computing, so adding CPUs would not fix it.";

        ctx.Add(new Finding
        {
            Severity = sev,
            Category = "Workload profile",
            Title = title,
            Plain = plain,
            WhyItMatters = sev == Severity.Ok
                ? "There is spare capacity: the performance problems listed below are about doing work more efficiently, not about a lack of hardware."
                : "When the processors are nearly full, every request has to queue for a free CPU, so response times grow quickly and unevenly, and a small increase in work can cause a large slowdown.",
            Technical = $"AAS = DB Time / Elapsed = {N(aas, 2)}; CPU demand (DB CPU + background CPU) = {N(cpuAas, 2)} per second = {P(cpuRatio * 100, 0)} of {N(cpus)} logical CPUs"
                        + (smt ? $" and {P(coreRatio * 100, 0)} of {N(cores)} physical cores (SMT threads add only a fraction of a core's throughput, so judge headroom against cores)" : "")
                        + $". Host CPU busy {P(busy, 1)} on average" + (peak >= 0 ? $", maximum {P(peak, 1)} in a single snapshot slice ('Operating System Statistics - Detail')" : "")
                        + ". Only CPU demand and host busy % indicate CPU saturation; AAS above the CPU count can also be sessions waiting on locks or latches.",
            Evidence =
            [
                new("Snapshot window", $"{ctx.Info.BeginSnapTime} to {ctx.Info.EndSnapTime}"),
                new("Elapsed time", Dur(ctx.ElapsedSec)),
                new("DB time (all sessions added up)", Dur(ctx.DbTimeSec)),
                new("Average Active Sessions", N(aas, 2), "DB time / elapsed time; includes waiting sessions"),
                new("CPU used by the database", N(cpuAas, 2) + " CPUs", "DB CPU + background CPU per second"),
                new("CPUs (logical) / cores", $"{N(cpus)} / {N(cores)}"),
                new("Host CPU busy (average)", P(busy, 1)),
                new("Host CPU busy (busiest slice)", peak >= 0 ? $"{P(peak, 1)} ({peakText})" : "n/a")
            ],
            Recommendations = sev == Severity.Ok ? [] : BuildLoadRecommendations(avgSaturated, peak >= 75, smt, overloaded),
            Sql = sev == Severity.Ok || peak < 75 ? [] :
            [
                new("CPU busy per snapshot from the AWR repository (find the peak)", """
                SELECT s.snap_id,
                       TO_CHAR(s.end_interval_time, 'YYYY-MM-DD HH24:MI') AS snap_end,
                       ROUND(100 * (b.value - LAG(b.value) OVER (ORDER BY s.snap_id))
                             / NULLIF((b.value - LAG(b.value) OVER (ORDER BY s.snap_id))
                                    + (i.value - LAG(i.value) OVER (ORDER BY s.snap_id)), 0), 1) AS pct_busy
                FROM   dba_hist_osstat b
                       JOIN dba_hist_osstat i   ON i.snap_id = b.snap_id AND i.dbid = b.dbid AND i.instance_number = b.instance_number
                                                AND i.stat_name = 'IDLE_TIME'
                       JOIN dba_hist_snapshot s ON s.snap_id = b.snap_id AND s.dbid = b.dbid AND s.instance_number = b.instance_number
                WHERE  b.stat_name = 'BUSY_TIME'
                AND    b.instance_number = &inst_num
                AND    s.snap_id BETWEEN &begin_snap - 1 AND &end_snap
                ORDER  BY s.snap_id;
                """, "BUSY_TIME/IDLE_TIME are cumulative counters, so the query subtracts the previous snapshot. A counter reset (instance restart) shows as NULL/negative for that snapshot.")
            ],
            Docs = [D.OsResources, D.Measuring, D.PerformanceMethod],
            SourceViews = ["DBA_HIST_SYS_TIME_MODEL", "DBA_HIST_OSSTAT", "DBA_HIST_SNAPSHOT"],
            TableRefs = new[] { ctx.Report.TableWithColumns("Snap Id", "Snap Time")?.Id, host?.Id, det?.Id }.OfType<string>().ToList(),
            ChartRefs = ["kpi", "host-cpu"],
            Impact = 100
        });
    }

    private static List<string> BuildLoadRecommendations(bool saturated, bool peaks, bool smt, bool overloaded)
    {
        var l = new List<string>();
        if (overloaded)
        {
            l.Add("Find out what the extra sessions are doing: look at the largest wait events and the top SQL in this report. Fixing the dominant wait or statement shrinks the queue.");
            l.Add("If parallel query is running (see the parallel execution finding), lower its degree of parallelism or the number of concurrent parallel statements so that they fit the CPU count.");
            l.Add("Limit concurrency with Resource Manager or a connection pool so that the number of active sessions stays near the number of CPUs.");
        }
        if (saturated)
        {
            l.Add("Reduce demand first: tune the SQL statements that use the most CPU (see the SQL and CPU findings); this is usually cheaper than adding hardware.");
            l.Add("Check whether anything other than the database competes for the CPUs (OS tools such as top or sar), and move it away.");
            l.Add("Use Resource Manager or instance caging so that critical workloads keep their CPU when the server is busy.");
        }
        if (peaks)
        {
            l.Add("Find what ran in the busiest slice (compare the snapshots around it, 'Top Sessions' and the top SQL) and, for batch jobs, spread them out or lower their parallelism.");
            l.Add("Generate a separate AWR report for just the busy slice for a precise diagnosis.");
        }
        if (smt) l.Add("Treat hyper-threads as a limited bonus: capacity planning should be based on physical cores.");
        l.Add("If the workload is already efficient, plan additional CPU capacity.");
        return l;
    }

    private static void HostIoWait(AnalysisContext ctx)
    {
        var host = ctx.Report.TableStartingWith("Host CPU");
        var wio = host.FirstRow().Val("%WIO");
        if (wio is not > 20) return;
        var userIo = RuleHelpers.WaitClassPct(ctx, "User I/O") ?? 0;
        ctx.Add(new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, wio >= 40 ? Severity.Medium : Severity.Low),
            Category = "I/O",
            Title = $"CPUs spent {P(wio)} of their time waiting for disk I/O",
            Plain = "The server's processors were often idle only because they were waiting for the storage to answer.",
            WhyItMatters = "Slow or saturated storage limits how fast every query that touches disk can run.",
            Technical = $"%WIO from DBA_HIST_OSSTAT (IOWAIT_TIME); User I/O wait class is {P(userIo)} of DB time. Correlate with File IO Stats latency. Note %WIO includes I/O of non-database processes.",
            Recommendations = ["Check storage latency and throughput for the busiest data files.", "Reduce physical reads by tuning the SQL in 'SQL ordered by Reads'."],
            Docs = [D.IoDesign, D.OsResources],
            SourceViews = ["DBA_HIST_OSSTAT"],
            TableRefs = host != null ? [host.Id] : [],
            Evidence = [new("%WIO", P(wio)), new("User I/O share of DB time", P(userIo))]
        });
    }

    private static void Parsing(AnalysisContext ctx)
    {
        var hard = ctx.LoadPerSec("Hard parses (SQL)", "Hard parses");
        var parses = ctx.LoadPerSec("Parses (SQL)", "Parses");
        var execs = ctx.LoadPerSec("Executes (SQL)", "Executes");
        var softParse = ctx.Eff("Soft Parse %");
        var lp = ctx.Report.Table("Load Profile");
        if (hard is null && softParse is null) return;

        // Oracle's guidance: hard parses should be rare in OLTP. A single-digit rate is normal, tens per second start to cost real time.
        var hardParseShare = ctx.TimeModelPct("hard parse elapsed time") ?? 0;
        var hardHigh = hard >= 10 || (softParse < 90 && hard >= 2);
        if (hardHigh)
        {
            var sev = hard >= 100 || hardParseShare >= 10 ? Severity.High : hard >= 30 || hardParseShare >= 3 ? Severity.Medium : Severity.Low;
            sev = SeverityMath.ForLoad(ctx, sev);
            var sp = ctx.Report.Table("Shared Pool Statistics");
            ctx.Add(new Finding
            {
                Severity = sev,
                Category = "Parsing",
                Title = $"Many SQL statements are compiled from scratch ({N(hard, 1)} hard parses per second)",
                Plain = "Before running a SQL statement the database must 'compile' it. Reusing an already-compiled statement is cheap. Here the application keeps sending new, slightly different statements (usually with the values typed directly into the SQL text instead of using bind variables, which are placeholders for the values), so the database compiles them again and again.",
                WhyItMatters = "Compiling wastes CPU, makes sessions queue for the same shared memory and limits how many users the system can support. Values pasted into SQL text can also be a security risk (SQL injection).",
                Technical = $"Hard parses/s {N(hard, 1)}, parses/s {N(parses, 1)}, Soft Parse % {P(softParse)}, hard parse elapsed time {P(hardParseShare)} of DB time. Target: Soft Parse % close to 100 and hard parses in low single digits per second. Look for literal SQL via FORCE_MATCHING_SIGNATURE.",
                Recommendations =
                [
                    "Change the application to use bind variables for values that change between executions.",
                    "Find the statement families that differ only by literals (see SQL below) and fix the top ones first.",
                    "As a temporary mitigation only, consider CURSOR_SHARING=FORCE at session or system level after testing.",
                    "Check that the shared pool is not undersized (Shared Pool Advisory)."
                ],
                Sql =
                [
                    new("Statement families that differ only by literal values", """
                    SELECT *
                    FROM  (SELECT force_matching_signature,
                                  COUNT(*)              AS versions,
                                  MIN(sql_id)           AS sample_sql_id,
                                  MIN(SUBSTR(sql_text, 1, 100)) AS sample_text
                           FROM   v$sql
                           WHERE  force_matching_signature <> 0
                           GROUP  BY force_matching_signature
                           HAVING COUNT(*) > 20
                           ORDER  BY versions DESC)
                    WHERE ROWNUM <= 20;
                    """),
                    new("Temporary mitigation (test first)", "ALTER SYSTEM SET cursor_sharing = 'FORCE' SCOPE = BOTH;", "CURSOR_SHARING=FORCE can change execution plans; prefer fixing the application.")
                ],
                Docs = [D.CursorSharing, D.SharedPool, D.SqlProcessing, D.Parameter("CURSOR_SHARING")],
                SourceViews = ["DBA_HIST_SYSSTAT", "DBA_HIST_SYS_TIME_MODEL"],
                TableRefs = lp != null ? sp != null ? [lp.Id, sp.Id] : [lp.Id] : [],
                ChartRefs = ["efficiency"],
                Evidence = [new("Hard parses per second", N(hard, 1)), new("Parses per second", N(parses, 1)), new("Soft Parse %", P(softParse)), new("Hard parse time share", P(hardParseShare))]
            });
        }

        var execToParse = ctx.Eff("Execute to Parse %");
        if (execToParse is < 50 && parses > 100)
        {
            var scc = RuleHelpers.Param(ctx, "session_cached_cursors");
            ctx.Add(new Finding
            {
                Severity = Severity.Low,
                Category = "Parsing",
                Title = $"Statements are re-parsed almost every time they run (Execute to Parse {P(execToParse)})",
                Plain = "The application asks the database to look up (parse) a statement nearly every time it runs it, instead of keeping it open and re-executing it.",
                WhyItMatters = "Each parse call costs CPU and shared-memory access. Reusing open statements in the application (or the session cursor cache) avoids this overhead.",
                Technical = $"Execute to Parse % {P(execToParse)} ({N(parses, 0)} parses/s vs {N(execs, 0)} executes/s). Keep cursors open in the application (statement caching in JDBC/ODP.NET) and make sure SESSION_CACHED_CURSORS is adequate (current: {scc ?? "default 50"}).",
                Recommendations =
                [
                    "Enable statement caching in the application's connection pool or driver.",
                    "Consider increasing SESSION_CACHED_CURSORS (for example to 100-200) after checking 'session cursor cache hits'.",
                    "See 'SQL ordered by Parse Calls' for statements parsed on every execution."
                ],
                Sql = [new("Session cursor cache effectiveness", "SELECT name, value FROM v$sysstat WHERE name IN ('session cursor cache hits', 'parse count (total)', 'parse count (hard)');\nALTER SYSTEM SET session_cached_cursors = 200 SCOPE = SPFILE;  -- takes effect after restart")],
                Docs = [D.SharedPool, D.Parameter("SESSION_CACHED_CURSORS")],
                SourceViews = ["DBA_HIST_SYSSTAT"],
                TableRefs = lp != null ? [lp.Id] : [],
                ChartRefs = ["efficiency"],
                Evidence = [new("Execute to Parse %", P(execToParse)), new("Parses/s", N(parses, 1)), new("Executes/s", N(execs, 1))]
            });
        }
    }

    private static void Logons(AnalysisContext ctx)
    {
        var logons = ctx.LoadPerSec("Logons");
        var connMgmt = ctx.TimeModelPct("connection management call elapsed time");
        if (logons is not >= 1 || (logons < 2 && !(connMgmt >= 1))) return;
        var lp = ctx.Report.Table("Load Profile");
        // Severity follows the measured cost; the logon rate alone is only a hint.
        var sev = connMgmt >= 5 || logons >= 100 ? Severity.High : connMgmt >= 2 || logons >= 30 ? Severity.Medium : Severity.Low;
        ctx.Add(new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, sev),
            Category = "Connections",
            Title = $"Applications connect and disconnect too often ({N(logons, 1)} logons per second)",
            Plain = "Applications open a brand-new database connection very frequently instead of reusing connections from a pool. Opening a connection is like checking in at a hotel each time you need a glass of water.",
            WhyItMatters = "Each new connection costs CPU and memory on the server and adds delay for the user. Connection storms can also overload the listener (the database's front door).",
            Technical = $"Logons/s {N(logons, 2)}; connection management call elapsed time {P(connMgmt)} of DB time. Use connection pooling (UCP, ODP.NET pool, application server pools) or DRCP.",
            Recommendations = ["Enable or fix connection pooling in the application tier.", "Find the programs that connect most often (listener log or audit trail).", "Consider Database Resident Connection Pooling (DRCP) for many short-lived connections."],
            Sql =
            [
                RuleHelpers.AtLeast(ctx, 12, 1)
                    ? new("Who is logging on most (only if a LOGON audit policy is enabled)", "SELECT os_username, userhost, client_program_name, COUNT(*) AS logons\nFROM   unified_audit_trail\nWHERE  action_name = 'LOGON' AND event_timestamp > SYSTIMESTAMP - INTERVAL '1' HOUR\nGROUP  BY os_username, userhost, client_program_name\nORDER  BY logons DESC;", "Unified auditing records LOGON only when an audit policy for it is enabled (in 12.1 the unified trail exists only when unified auditing is turned on); otherwise use the listener log or DBA_AUDIT_SESSION.")
                    : new("Who is logging on most (only if session auditing is enabled)", "SELECT os_username, userhost, COUNT(*) AS logons\nFROM   dba_audit_session\nWHERE  action_name = 'LOGON' AND timestamp > SYSDATE - 1/24\nGROUP  BY os_username, userhost\nORDER  BY logons DESC;", "Requires AUDIT SESSION (or AUDIT_TRAIL=DB); otherwise use the listener log."),
                new("Current sessions by program and machine", "SELECT *\nFROM  (SELECT program, machine, COUNT(*) AS sessions FROM v$session WHERE type = 'USER' GROUP BY program, machine ORDER BY sessions DESC)\nWHERE ROWNUM <= 20;")
            ],
            Docs = [D.Processes, D.DesignForPerformance],
            SourceViews = ["DBA_HIST_SYSSTAT"],
            TableRefs = lp != null ? [lp.Id] : [],
            Evidence = [new("Logons per second", N(logons, 2)), new("Connection management % of DB time", P(connMgmt))]
        });
    }

    private static void Rollbacks(AnalysisContext ctx)
    {
        var rb = ctx.LoadPerSec("Rollbacks");
        var tx = ctx.LoadPerSec("Transactions");
        if (rb is null || tx is not > 0) return;
        var pct = rb / tx * 100;
        if (pct < 10 || rb < 1) return;
        ctx.Add(new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, pct >= 30 ? Severity.Medium : Severity.Low),
            Category = "Application",
            Title = $"{P(pct, 0)} of transactions are rolled back",
            Plain = "A notable share of the work done by applications is cancelled (rolled back) instead of saved.",
            WhyItMatters = "Rolled-back work was wasted effort: it used CPU, disk and undo space, and then had to be undone, which costs about as much again. It can also signal application errors.",
            Technical = $"Rollbacks/s {N(rb, 1)} vs Transactions/s {N(tx, 1)}. Some frameworks issue rollbacks on connection return (cheap when no work was done); check 'transaction rollbacks' in Instance Activity Stats for rollbacks that actually undid changes.",
            Recommendations = ["Check application logs for errors causing rollbacks.", "Verify whether the connection pool issues a rollback on every connection return."],
            Docs = [D.Undo, D.Statistics],
            SourceViews = ["DBA_HIST_SYSSTAT"],
            Evidence = [new("Rollbacks/s", N(rb, 2)), new("Transactions/s", N(tx, 2)), new("transaction rollbacks (undo applied)", N(ctx.InstanceStat("transaction rollbacks")))]
        });
    }

    private static readonly (string Key, string Label)[] Ratios =
    [
        ("Buffer Nowait %", "Buffer Nowait %"), ("Redo NoWait %", "Redo NoWait %"), ("In-memory Sort %", "In-memory Sort %"),
        ("Library Hit %", "Library Hit %"), ("Soft Parse %", "Soft Parse %"), ("Execute to Parse %", "Execute to Parse %"),
        ("Latch Hit %", "Latch Hit %"), ("Parse CPU to Parse Elapsd %", "Parse CPU to Parse Elapsd %"), ("% Non-Parse CPU", "% Non-Parse CPU"),
        ("Buffer Hit %", "Buffer Hit %")
    ];

    private static void Efficiency(AnalysisContext ctx)
    {
        if (ctx.Efficiency.Count == 0) return;
        var t = ctx.Report.TableStartingWith("Instance Efficiency");
        var issues = new List<Evidence>();
        var parseShare = ctx.TimeModelPct("parse time elapsed") ?? 0;

        void Check(string key, double threshold, string meaning, bool relevant = true)
        {
            var v = ctx.Eff(key);
            if (relevant && v is not null && v < threshold) issues.Add(new(key, P(v, 2), $"target > {threshold}%: {meaning}"));
        }
        Check("Buffer Nowait %", 99, "sessions waited for memory blocks held by others (see buffer busy waits)");
        Check("Redo NoWait %", 99, "sessions waited for space in the redo log buffer");
        Check("Library Hit %", 95, "statements or objects had to be reloaded into the shared pool");
        Check("In-memory Sort %", 95, "sorts spilled to disk (PGA too small or large sorts)");
        Check("Latch Hit %", 99, "contention on internal memory latches");
        // Only meaningful when parsing is a visible part of the workload.
        Check("Parse CPU to Parse Elapsd %", 70, "parsing waited (latch/mutex contention) instead of working", parseShare >= 2);
        Check("% Non-Parse CPU", 90, "a large part of CPU went to parsing instead of executing");
        // The buffer cache hit ratio alone is not a tuning goal (Performance Tuning Guide): only flag very low values.
        Check("Buffer Hit %", 80, "many reads went to disk; only meaningful together with high physical read waits");

        if (issues.Count == 0) return; // the all-clear is reported by the healthy-findings rule

        ctx.Add(new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, issues.Count >= 3 ? Severity.Medium : Severity.Low),
            Category = "Memory",
            Title = $"{issues.Count} instance efficiency indicator(s) below target",
            Plain = "Some of the database's internal efficiency indicators are lower than Oracle's usual targets. Each one is explained below.",
            WhyItMatters = "These ratios are symptoms. They help confirm the root causes identified by the wait-event findings.",
            Technical = "Instance Efficiency Percentages (target 100%). Oracle's Performance Tuning Guide advises using ratios only to corroborate wait-based diagnosis, not as goals in themselves.",
            Evidence = issues,
            Recommendations = ["Use these indicators together with the top wait events: fix the corresponding wait event finding rather than tuning the ratio directly."],
            Docs = [D.InstanceTuning, D.BufferCache, D.SharedPool],
            SourceViews = ["DBA_HIST_SYSSTAT", "DBA_HIST_LATCH"],
            TableRefs = t != null ? [t.Id] : [],
            ChartRefs = ["efficiency"]
        });
    }

    internal static List<Evidence> RatioEvidence(AnalysisContext ctx) =>
        Ratios.Where(r => ctx.Eff(r.Key) is not null).Select(r => new Evidence(r.Label, P(Math.Min(ctx.Eff(r.Key)!.Value, 100), 2))).ToList();

    private static void TimeModel(AnalysisContext ctx)
    {
        var tm = ctx.Report.Table("Time Model Statistics");
        if (tm == null) return;

        void Flag(string stat, double threshold, Severity sev, string title, string plain, string why, string[] actions, DocRef[] docs)
        {
            var pct = ctx.TimeModelPct(stat);
            if (pct is null || pct < threshold) return;
            ctx.Add(new Finding
            {
                Severity = SeverityMath.ForLoad(ctx, sev),
                Category = "Time model",
                Impact = pct.Value,
                Title = $"{title} ({P(pct)} of DB time)",
                Plain = plain,
                WhyItMatters = why,
                Technical = $"Time model statistic '{stat}' = {Dur(ctx.TimeModelSec(stat))} ({P(pct)} of DB time).",
                Recommendations = [.. actions],
                Docs = [.. docs],
                SourceViews = ["DBA_HIST_SYS_TIME_MODEL"],
                TableRefs = [tm.Id],
                ChartRefs = ["time-model"],
                Evidence = [new(stat, P(pct)), new("Time", Dur(ctx.TimeModelSec(stat)))]
            });
        }

        if (!ctx.Findings.Any(f => f.Category == "Parsing" && f.Severity <= Severity.Medium))
        {
            Flag("hard parse elapsed time", 5, Severity.Medium, "Compiling new SQL takes a noticeable share of time",
                "A noticeable part of database time is spent compiling (hard parsing) SQL statements.",
                "Time spent compiling is time not spent doing useful work.",
                ["Use bind variables.", "Check for invalidations (statistics gathering, DDL) during busy hours."], [D.CursorSharing, D.SharedPool]);
            Flag("parse time elapsed", 10, Severity.Medium, "Parsing (looking up and compiling SQL) takes a large share of time",
                "A large part of database time goes to the preparation of SQL statements (parsing) rather than to running them.",
                "Parse time is pure overhead: it delays every statement before any data is touched.",
                ["Keep statements open and re-execute them (statement caching in the application driver).", "Use bind variables, and check SESSION_CACHED_CURSORS."], [D.SqlProcessing, D.SharedPool]);
        }
        Flag("failed parse elapsed time", 1, Severity.Medium, "SQL statements fail to compile",
            "Applications are sending SQL that the database rejects (syntax errors, missing objects or privileges).",
            "Failed parses waste resources and usually indicate an application bug that may be hidden from users.",
            ["Identify the failing SQL with a SERVERERROR trigger or SQL trace and fix the application code."], [D.SqlProcessing]);
        Flag("hard parse (sharing criteria) elapsed time", 2, Severity.Medium, "Statements cannot be shared between sessions",
            "Identical statements are compiled again because some setting differs between sessions (language, optimizer settings, privileges).",
            "Each unshareable copy costs memory and CPU.",
            ["Check V$SQL_SHARED_CURSOR for the reason child cursors are created."], [D.CursorSharing]);
        Flag("hard parse (bind mismatch) elapsed time", 1, Severity.Low, "Bind variable type or length mismatches force re-compiles",
            "The application passes values with varying data types or lengths, which forces extra compiles.",
            "Extra copies of the same statement waste shared memory and CPU.",
            ["Make the application bind variables with consistent data types and sizes."], [D.CursorSharing]);
        Flag("sequence load elapsed time", 2, Severity.Low, "Refilling sequence caches takes measurable time",
            "Sessions frequently wait for new batches of sequence numbers (ID generators).",
            "Small sequence caches throttle inserts that need new IDs.",
            ["Increase CACHE on the busiest sequences (for example 1000)."], [D.WaitEvents]);
        Flag("PL/SQL compilation elapsed time", 2, Severity.Low, "PL/SQL code is recompiled during the workload",
            "Stored programs are being compiled while users are working, typically after invalidations.",
            "Recompilation blocks users of the affected code.",
            ["Avoid deploying code or DDL during business hours.", "Check for objects being invalidated (DBA_OBJECTS.STATUS / LAST_DDL_TIME)."], [D.SharedPool]);

        var plsql = ctx.TimeModelPct("PL/SQL execution elapsed time");
        if (plsql is >= 30)
        {
            var proc = ctx.Report.Table("Top PL/SQL Procedures");
            ctx.Add(new Finding
            {
                Severity = Severity.Info,
                Category = "Time model",
                Title = $"PL/SQL code execution accounts for {P(plsql)} of DB time",
                Plain = "A large part of the database's time is spent running stored program code (PL/SQL), not just SQL queries.",
                WhyItMatters = "Tuning should look inside these programs, at both their own logic and the SQL they run.",
                Technical = "PL/SQL execution elapsed time excludes the SQL executed by the PL/SQL. Use DBMS_HPROF (hierarchical profiler) to find hot spots. Row-by-row processing should be converted to bulk (FORALL/BULK COLLECT) or set-based SQL.",
                Recommendations = ["Profile the top PL/SQL procedures with DBMS_HPROF.", "Replace row-by-row loops with set-based SQL or BULK COLLECT / FORALL."],
                Evidence = proc?.DataRows.GroupBy(r => r.Str("PL/SQL Entry Subprogram")).Where(g => g.Key.Length > 0).Take(5).Select(g => new Evidence("Top PL/SQL", g.Key, $"{N(g.Sum(r => r.Num("% Activity") ?? 0), 1)}% of activity")).ToList() ?? [],
                Docs = [D.DesignForPerformance],
                SourceViews = ["DBA_HIST_SYS_TIME_MODEL", "DBA_HIST_ACTIVE_SESS_HISTORY"],
                TableRefs = proc != null ? [tm.Id, proc.Id] : [tm.Id],
                ChartRefs = ["time-model"]
            });
        }
    }
}
