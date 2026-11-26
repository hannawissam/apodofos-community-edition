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

/// <summary>ASH "who and where": services/modules, clients, sessions, blockers, PL/SQL, Java, objects, files and latches.</summary>
public sealed class AshSessionRules : IRule
{
    public string Name => "ASH sessions and objects";
    public ReportKind[] Kinds => [ReportKind.Ash];

    public void Evaluate(AnalysisContext ctx)
    {
        ServiceModule(ctx);
        Clients(ctx);
        Sessions(ctx);
        Blockers(ctx);
        PlSql(ctx);
        CallTypes(ctx);
        Objects(ctx);
        Files(ctx);
        Latches(ctx);
    }

    private static bool IsBackground(string program) =>
        Regex.IsMatch(program, @"\((LGWR|LG\d\d|DBW\w*|CKPT|SMON|PMON|ARC\w*|LMS\w*|LMD\w*|MMON|MMNL|RVWR|CJQ\w*|J\d+|W\d+|M\d+)\)", RegexOptions.IgnoreCase);

    private static void ServiceModule(AnalysisContext ctx)
    {
        var t = T(ctx, "Top Service/Module");
        if (t == null || t.Rows.Count == 0) return;
        var rows = Distinct(t, "Service", "Module").OrderByDescending(r => r.Val("% Activity") ?? 0).ToList();
        if (rows.Count == 0) return; // damaged table: the key cells are blank
        var top = rows[0];
        var pct = top.Val("% Activity") ?? 0;
        if (pct < 40) return;
        ctx.Add(new Finding
        {
            Severity = Severity.Info,
            Category = "Workload profile",
            Impact = pct / 2,
            Title = $"Module '{top.Str("Module")}' on service '{top.Str("Service")}' generated {Pc(pct)} of sampled activity",
            Plain = $"Most of the database's work came from one application component: module '{top.Str("Module")}' connected through service '{top.Str("Service")}'. Look there first.",
            WhyItMatters = "Knowing which application, job or user population drives the load tells you who to talk to and what to change.",
            Technical = $"Top Service/Module: {top.Str("Service")}/{top.Str("Module")} {P(pct)}, action '{top.Str("Action")}' {top.Str("% Action")}%. The module and action names come from DBMS_APPLICATION_INFO; blank or UNNAMED means the application does not set them.",
            Evidence = rows.Take(5).Select(r => new Evidence($"{r.Str("Service")} / {r.Str("Module")}", r.Str("% Activity") + "%", r.Str("Action"))).ToList(),
            Recommendations = ["Ask the owners of this module what ran in the window.", "Ask developers to set MODULE and ACTION (DBMS_APPLICATION_INFO) so future reports show which part of the application is responsible."],
            Docs = [D.Transient],
            SourceViews = ["V$ACTIVE_SESSION_HISTORY"],
            TableRefs = [t.Id]
        });
    }

    private static void Clients(AnalysisContext ctx)
    {
        var t = T(ctx, "Top Client IDs");
        if (t == null || t.Rows.Count == 0) return;
        var top = t.DataRows.OrderByDescending(r => r.Val("% Activity") ?? 0).First();
        var pct = top.Val("% Activity") ?? 0;
        if (pct < 30) return;
        ctx.Add(new Finding
        {
            Severity = Severity.Info, Category = "Workload profile", Impact = pct / 3,
            Title = $"One end user or client ID ('{top.Str(t.Headers.FirstOrDefault() ?? "")}') was behind {Pc(pct)} of sampled activity",
            Plain = "A single client identifier, usually an application end user, produced a large part of the load.",
            WhyItMatters = "One heavy user or batch identity is often a reporting or export job that can be rescheduled or limited.",
            Technical = $"Top Client IDs (CLIENT_ID set through DBMS_SESSION.SET_IDENTIFIER): {top.Str(t.Headers.FirstOrDefault() ?? "")} {P(pct)}.",
            Evidence = t.DataRows.Take(5).Select(r => new Evidence(r.Label, r.Str("% Activity") + "%")).ToList(),
            Recommendations = ["Check what this user or job did in the window and whether it can run off-peak."],
            Docs = [D.Transient], SourceViews = ["V$ACTIVE_SESSION_HISTORY"], TableRefs = [t.Id]
        });
    }

    private static void Sessions(AnalysisContext ctx)
    {
        var t = T(ctx, "Top Sessions");
        if (t == null || t.Rows.Count == 0) return;
        var first = Distinct(t, "Sid, Serial#", "Sid,Srl# (Inst)", "Sid").OrderByDescending(r => r.Val("% Activity") ?? 0).FirstOrDefault();
        if (first.Table == null) return;
        var pct = first.Val("% Activity") ?? 0;
        var program = first.Str("Program");
        if (pct < 25 || IsBackground(program)) return;
        var sid = first.Str("Sid, Serial#", "Sid,Srl# (Inst)", "Sid");
        var events = t.DataRows.Where(r => r.Str("Sid, Serial#", "Sid,Srl# (Inst)", "Sid") == sid && r.Str("Event").Length > 0).Take(3).Select(r => $"{r.Str("Event")} {r.Str("% Event")}%").ToList();
        ctx.Add(new Finding
        {
            Severity = Sev(ctx, pct, Severity.Medium),
            Category = "Workload profile",
            Impact = pct,
            Title = $"A single session ({sid}) accounted for {Pc(pct)} of sampled activity",
            Plain = "One connection did a large share of all the work. When one session dominates, the slowdown may come from a single program, job or user.",
            WhyItMatters = "Tuning or rescheduling that one program is much cheaper than tuning the whole system.",
            Technical = $"Top Sessions: {sid}, user {first.Str("User")}, program {program}; events {string.Join(", ", events)}; {first.Str("# Samples Active")} samples active.",
            Evidence = [new("Session (SID,SERIAL#)", sid), new("User", first.Str("User")), new("Program", program), new("Events", string.Join(", ", events))],
            Recommendations = ["Identify the job or user behind the session and review its SQL (see the SQL findings).", "If it is a batch job, check whether it can run at a quieter time or be split."],
            Sql = [new("What this session did in the window (ASH)", Limit(ctx, $"SELECT sql_id, event, session_state, {Weight(ctx)} AS approx_db_seconds\nFROM   {View(ctx)}\nWHERE  {Window(ctx)} AND session_id = {SqlSafe.Digits(SessionKey(sid), "session_id")}\nGROUP  BY sql_id, event, session_state\nORDER  BY approx_db_seconds DESC", 10), HistoryCaution)],
            Docs = [D.Transient], SourceViews = ["V$ACTIVE_SESSION_HISTORY"], TableRefs = [t.Id]
        });
    }

    private static void Blockers(AnalysisContext ctx)
    {
        var t = T(ctx, "Top Blocking Sessions");
        if (t == null || t.Rows.Count == 0) return;
        var sidCol = t.Col("Blocking Sid (Inst)", "Blocking Sid", "Blocking SID");
        if (sidCol < 0) sidCol = 0;
        var blockers = new Dictionary<string, (AwrRow Row, double Pct, List<string> Events)>();
        foreach (var r in t.DataRows)
        {
            var sid = r.StrAt(sidCol);
            if (sid.Length == 0) continue;
            var key = SessionKey(sid);
            var ev = r.Str("Event Caused", "Event");
            if (!blockers.TryGetValue(key, out var b)) b = (r, r.Val("% Activity") ?? 0, []);
            if (ev.Length > 0) b.Events.Add($"{ev} {r.Str("% Event")}%");
            blockers[key] = b;
        }
        var sessions = T(ctx, "Top Sessions");
        var candidates = blockers.Where(b => b.Value.Pct >= 3 && !b.Value.Row.Str("Program").Contains("NOT FOUND", StringComparison.OrdinalIgnoreCase)
                                             && !sidTextNotFound(b.Value.Row.StrAt(sidCol)) && !IsBackground(b.Value.Row.Str("Program")))
            .OrderByDescending(b => b.Value.Pct).Take(5).ToList();
        if (candidates.Count == 0) return;
        var top = candidates[0];
        var pct = top.Value.Pct;
        // was the blocker itself active (working or waiting) in the samples, or idle inside its transaction?
        var own = sessions?.DataRows.Where(r => SessionKey(r.Str("Sid, Serial#", "Sid,Srl# (Inst)", "Sid")) == top.Key).ToList() ?? [];
        var ownPct = own.Select(r => r.Val("% Activity") ?? 0).DefaultIfEmpty(0).Max();
        var idle = ownPct < 1;
        var evidence = candidates.Select(c => new Evidence($"Blocker {c.Value.Row.StrAt(sidCol)}", $"{N(c.Value.Pct, 1)}% of activity blocked", $"{string.Join("; ", c.Value.Events.Take(2))}; {c.Value.Row.Str("User")}; {c.Value.Row.Str("Program")}")).ToList();
        evidence.Add(new Evidence("Was the top blocker itself active?", idle ? "No - not among the busiest sessions" : $"Yes - {N(ownPct, 1)}% of activity", idle ? "typical of a transaction left open (idle in transaction) or a session waiting on the client" : $"it was {string.Join(", ", own.Take(2).Select(r => r.Str("Event")).Where(e => e.Length > 0))}"));
        ctx.Add(new Finding
        {
            Severity = Sev(ctx, pct, Severity.High),
            Category = "Wait events",
            Impact = pct,
            Title = $"Session {top.Value.Row.StrAt(sidCol)} ({top.Value.Row.Str("Program")}) blocked other sessions during {Pc(pct)} of sampled activity",
            Plain = idle
                ? "One connection appears to have held a lock while it showed no work of its own in the samples (it is not among the busiest sessions): possibly a transaction left open without commit. Others queued behind it."
                : "One connection held a lock (or another resource) that others were waiting for, and it was busy itself, so it was probably running a long transaction or waiting on its own resource.",
            WhyItMatters = "Blocked users see screens or jobs freeze until the blocker commits, rolls back or is ended, so one forgotten or slow transaction can stall many people.",
            Technical = $"Top Blocking Sessions: blocker {top.Value.Row.StrAt(sidCol)} caused {string.Join(", ", top.Value.Events.Take(3))}; user {top.Value.Row.Str("User")}; {top.Value.Row.Str("# Samples Active")} samples; open transactions {top.Value.Row.Str("XIDs")}. " + (idle ? "The blocker is absent from the busiest sessions, so it appears to be idle in its transaction (inferred from its absence in the samples, not observed directly)." : "The blocker also appears in Top Sessions: check what it waits for, it may itself be blocked or slow."),
            Evidence = evidence,
            Recommendations = ["Find what the blocker is (program, user) and whether it can commit sooner or be ended.", idle ? "If this is confirmed, fix the application that leaves transactions open (commit/rollback in error paths, no user think-time in transactions)." : "Shorten the blocker's transaction or process data in smaller commits."],
            Sql = [new("What the blockers were doing (ASH)", Limit(ctx, $"SELECT blocking_session, blocking_session_serial#, sql_id, event, {Weight(ctx)} AS approx_db_seconds\nFROM   {View(ctx)}\nWHERE  {Window(ctx)} AND blocking_session IS NOT NULL\nGROUP  BY blocking_session, blocking_session_serial#, sql_id, event\nORDER  BY approx_db_seconds DESC", 15), HistoryCaution),
                new("Open transactions per session (live)", "SELECT s.sid, s.serial#, s.username, s.program, s.status, t.start_time, t.used_ublk\nFROM   v$transaction t JOIN v$session s ON s.taddr = t.addr\nORDER  BY t.start_time;")],
            Docs = [D.Concurrency, D.Transient],
            SourceViews = ["V$ACTIVE_SESSION_HISTORY", "V$TRANSACTION"],
            TableRefs = [t.Id]
        });

        static bool sidTextNotFound(string s) => s.Contains("NOT FOUND", StringComparison.OrdinalIgnoreCase);
    }

    private static void PlSql(AnalysisContext ctx)
    {
        var t = T(ctx, "Top PL/SQL Procedures");
        if (t != null && t.Rows.Count > 0)
        {
            var top = t.DataRows.OrderByDescending(r => r.Val("% Activity") ?? 0).First();
            var pct = top.Val("% Activity") ?? 0;
            if (pct >= 15)
                ctx.Add(new Finding
                {
                    Severity = Severity.Info, Category = "Workload profile", Impact = pct / 2,
                    Title = $"PL/SQL procedure {top.Str("PLSQL Entry Subprogram")} was on the call stack for {Pc(pct)} of sampled activity",
                    Plain = "A stored procedure (PL/SQL code) was responsible for a large part of the load. Its own code and the SQL it runs are both candidates for tuning.",
                    WhyItMatters = "Statement-level views can hide that hundreds of small statements all belong to one procedure; tuning the procedure's logic can remove all of them.",
                    Technical = $"Top PL/SQL Procedures: entry {top.Str("PLSQL Entry Subprogram")} {P(pct)}; current subprogram {top.Str("PLSQL Current Subprogram")} {top.Str("% Current")}%. 'SQL' as current subprogram means the time is in SQL, not PL/SQL code.",
                    Evidence = t.DataRows.Take(5).Select(r => new Evidence(r.Str("PLSQL Entry Subprogram"), r.Str("% Activity") + "%", r.Str("PLSQL Current Subprogram"))).ToList(),
                    Recommendations = ["Profile the procedure with the hierarchical profiler (DBMS_HPROF) to find the slow lines.", "Look for row-by-row loops that can be replaced by a single SQL statement or BULK COLLECT/FORALL."],
                    Docs = [D.Hprof, D.DesignForPerformance], SourceViews = ["V$ACTIVE_SESSION_HISTORY"], TableRefs = [t.Id]
                });
        }
        var j = T(ctx, "Top Java Workload");
        if (j != null && j.Rows.Count > 0 && (j.DataRows.First().Val("% Activity") ?? 0) >= 15)
            ctx.Add(new Finding
            {
                Severity = Severity.Info, Category = "Workload profile", Impact = 5,
                Title = $"Java stored code was active in {Pc(j.DataRows.First().Val("% Activity") ?? 0)} of sampled activity",
                Plain = "Java running inside the database accounted for a visible share of the activity.",
                WhyItMatters = "Java in the database uses the same CPU and memory as SQL, and is rarely tuned.",
                Technical = "Top Java Workload section lists the Java entry subprograms; profile the class with the JVM tools or move heavy logic out of the database.",
                Evidence = j.DataRows.Take(3).Select(r => new Evidence(r.Label, r.Str("% Activity") + "%")).ToList(),
                Docs = [D.Transient], SourceViews = ["V$ACTIVE_SESSION_HISTORY"], TableRefs = [j.Id]
            });
    }

    private static void CallTypes(AnalysisContext ctx)
    {
        var t = T(ctx, "Top Call Types");
        if (t == null) return;
        var logon = t.DataRows.Where(r => Regex.IsMatch(r.Str("Call Type"), "LOGON|LOGOFF", RegexOptions.IgnoreCase)).Sum(r => r.Val("% Activity") ?? 0);
        if (logon < 10) return;
        ctx.Add(new Finding
        {
            Severity = Sev(ctx, logon, Severity.Medium), Category = "Workload profile", Impact = logon,
            Title = $"Logging on and off took {Pc(logon)} of sampled activity",
            Plain = "The application connects and disconnects very frequently, and the database spends a visible share of its time on it.",
            WhyItMatters = "Creating a connection costs far more than running a simple statement; connection storms can exhaust processes and the listener.",
            Technical = $"Top Call Types: LOGON/LOGOFF {P(logon)}. Look for a missing connection pool or a script that connects per statement.",
            Evidence = t.DataRows.Take(5).Select(r => new Evidence(r.Str("Call Type"), r.Str("% Activity") + "%", r.Str("Count") + " samples")).ToList(),
            Recommendations = ["Introduce connection pooling and reuse sessions."],
            Docs = [D.Processes], SourceViews = ["V$ACTIVE_SESSION_HISTORY"], TableRefs = [t.Id]
        });
    }

    private static void Objects(AnalysisContext ctx)
    {
        var t = T(ctx, "Top DB Objects");
        if (t == null || t.Rows.Count == 0) return;
        var rows = Distinct(t, "Object ID").OrderByDescending(r => r.Val("% Activity") ?? 0).ToList();
        if (rows.Count == 0) return; // damaged table: the key cells are blank
        var top = rows[0];
        var pct = top.Val("% Activity") ?? 0;
        if (pct < 10) return;
        var name = top.Str("Object Name (Type)", "Object Name");
        var events = t.DataRows.Where(r => r.Str("Object ID") == top.Str("Object ID") && r.Str("Event").Length > 0).Take(3).Select(r => $"{r.Str("Event")} {r.Str("% Event")}%").ToList();
        ctx.Add(new Finding
        {
            Severity = Sev(ctx, pct, Severity.High), Category = "I/O and objects", Impact = pct,
            Title = $"Object {name} was the hot spot in {Pc(pct)} of sampled activity",
            Plain = $"Waits for reading or locking concentrated on one database object, {name}. When one table or index is the centre of the problem, fixing access to it pays off for everything that touches it.",
            WhyItMatters = "A single hot table or index can limit the whole application: every query that touches it competes for the same blocks and I/O.",
            Technical = $"Top DB Objects (Application, Cluster, User I/O and buffer busy waits only): object id {top.Str("Object ID")} {name} in tablespace {top.Str("Tablespace")}; events {string.Join(", ", events)}.",
            Evidence = rows.Take(5).Select(r => new Evidence(r.Str("Object Name (Type)", "Object Name"), r.Str("% Activity") + "%", r.Str("Tablespace"))).ToList(),
            Recommendations = ["Find the SQL that touches the object (Top SQL) and check for full scans or a missing index.", "For hot-block contention consider partitioning, reverse-key indexes, a larger sequence cache or a smaller block fill."],
            Sql = [new("SQL that waited on this object (ASH)", Limit(ctx, $"SELECT sql_id, event, {Weight(ctx)} AS approx_db_seconds\nFROM   {View(ctx)}\nWHERE  {Window(ctx)} AND current_obj# = {SqlSafe.Digits(Regex.Replace(top.Str("Object ID"), "[^0-9]", ""), "object_id")}\nGROUP  BY sql_id, event\nORDER  BY approx_db_seconds DESC", 10), HistoryCaution)],
            Docs = [D.Transient, D.SchemaSpace, D.Indexes], SourceViews = ["V$ACTIVE_SESSION_HISTORY", "DBA_OBJECTS"], ChartRefs = ["ash-top-objects"], TableRefs = [t.Id]
        });
    }

    private static void Files(AnalysisContext ctx)
    {
        var t = T(ctx, "Top DB Files");
        if (t == null || t.Rows.Count == 0) return;
        var rows = Distinct(t, "File ID").OrderByDescending(r => r.Val("% Activity") ?? 0).ToList();
        if (rows.Count == 0) return; // damaged table: the key cells are blank
        var top = rows[0];
        var pct = top.Val("% Activity") ?? 0;
        if (pct < 20) return;
        ctx.Add(new Finding
        {
            Severity = Sev(ctx, pct, Severity.Medium), Category = "I/O and objects", Impact = pct,
            Title = $"One data file ({top.Str("File Name")}) drew {Pc(pct)} of sampled I/O waits",
            Plain = "Most of the time sessions waited for disk, they were waiting on one file. That points to one hot tablespace or a slow device.",
            WhyItMatters = "Spreading hot data across more files or faster storage can cut waits without any code change.",
            Technical = $"Top DB Files (Cluster and User I/O events only): file {top.Str("File ID")} in {top.Str("Tablespace")} {P(pct)}.",
            Evidence = rows.Take(5).Select(r => new Evidence($"File {r.Str("File ID")}", r.Str("% Activity") + "%", r.Str("File Name") + " (" + r.Str("Tablespace") + ")")).ToList(),
            Recommendations = ["Check the latency of the storage behind this file and whether its tablespace holds the hottest tables or indexes.", "Consider moving the hottest segments to faster storage or spreading them over more files."],
            Docs = [D.IoDesign, D.Tablespaces], SourceViews = ["V$ACTIVE_SESSION_HISTORY", "DBA_DATA_FILES"], TableRefs = [t.Id]
        });
    }

    private static void Latches(AnalysisContext ctx)
    {
        var t = T(ctx, "Top Latches");
        if (t == null || t.Rows.Count == 0) return;
        var top = t.DataRows.OrderByDescending(r => r.Val("% Activity") ?? 0).First();
        var pct = top.Val("% Activity") ?? 0;
        if (pct < 5) return;
        var name = top.Str("Latch", "Latch Name");
        if (name.Length == 0) name = top.Label;
        var k = WaitEventKnowledge.Find("latch: " + name);
        ctx.Add(new Finding
        {
            Severity = Sev(ctx, pct, Severity.High), Category = "Wait events", Impact = pct,
            Title = $"Latch '{name}' was involved in {Pc(pct)} of sampled activity",
            Plain = k?.Plain ?? "Sessions queued for a small internal lock that protects a shared memory structure.",
            WhyItMatters = k?.Why ?? "Latch contention limits how many sessions can work at once and gets worse with more CPUs.",
            Technical = (k?.Technical ?? "Top Latches lists latches by share of samples waiting on them.") + $" Latch {name}: {P(pct)}.",
            Evidence = t.DataRows.Take(5).Select(r => new Evidence(r.Str("Latch", "Latch Name") is { Length: > 0 } n ? n : r.Label, r.Str("% Activity") + "%", r.Str("Event"))).ToList(),
            Recommendations = [.. AshEventRules.AshActions(k?.Actions, "Find the SQL and objects behind the latch with the Top SQL and Top DB Objects sections.")],
            Docs = [.. (k?.Docs ?? [D.InstanceTuning])], SourceViews = ["V$ACTIVE_SESSION_HISTORY", "V$LATCH"], TableRefs = [t.Id]
        });
    }
}
