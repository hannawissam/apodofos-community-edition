// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Core.Model;
using static Apodofos.Core.Analysis.AnalysisContext;
using static Apodofos.Core.Analysis.Rules.AshHelpers;
using static Apodofos.Core.Analysis.Rules.RuleHelpers;
using D = Apodofos.Core.Knowledge.OracleDocs;

namespace Apodofos.Core.Analysis.Rules;

/// <summary>ASH SQL sections: Top SQL with Top Events / Row Sources, literals, plan changes, command types and execution phases.</summary>
public sealed class AshSqlRules : IRule
{
    public string Name => "ASH SQL";
    public ReportKind[] Kinds => [ReportKind.Ash];

    public void Evaluate(AnalysisContext ctx)
    {
        TopSql(ctx);
        PlanMismatch(ctx);
        RowSources(ctx);
        Literals(ctx);
        Phases(ctx);
        CommandTypes(ctx);
    }

    private static void TopSql(AnalysisContext ctx)
    {
        var t = T(ctx, "Top SQL with Top Events", "Top SQL Statements");
        if (t == null || t.Rows.Count == 0) return;
        var sqls = Distinct(t, "SQL ID").Where(r => (r.Val("% Activity") ?? 0) >= 5).OrderByDescending(r => r.Val("% Activity")).Take(4).ToList();
        foreach (var r in sqls)
        {
            var id = r.Str("SQL ID");
            var pct = r.Val("% Activity") ?? 0;
            var plan = r.Str("Planhash", "PlanHash");
            var events = t.DataRows.Where(x => x.Str("SQL ID") == id && x.Str("Event").Length > 0).OrderByDescending(Pct).Take(3).ToList();
            var top = events.Count > 0 ? events[0] : default;
            var topEv = top.Str("Event");
            var cpu = IsCpu(topEv);
            var rowSource = events.Select(e => e.Str("Top Row Source")).FirstOrDefault(s => s.Length > 0) ?? "";
            var text = SqlText(ctx, id, r.Str("SQL Text"));
            var evText = string.Join(", ", events.Select(e => $"{e.Str("Event")} {N(Pct(e), 1)}%"));
            var execs = r.Val("Sampled # of Executions");
            var sess = Sessions(ctx, pct);
            ctx.Add(new Finding
            {
                Severity = Sev(ctx, pct),
                Category = "SQL",
                Impact = pct,
                Title = $"SQL {id} accounted for {Pc(pct)} of sampled activity" + (topEv.Length > 0 ? $", mostly {(cpu ? "on CPU" : "waiting on '" + topEv + "'")}" : ""),
                Plain = cpu
                    ? $"One statement was responsible for {Pc(pct)} of everything the database did, and most of that time it was working on the processor. Making this statement cheaper is the quickest way to free capacity."
                    : $"One statement was responsible for {Pc(pct)} of all activity, and most of its time was spent waiting on '{topEv}'. Fixing the cause of that wait for this statement gives the biggest single gain.",
                WhyItMatters = "A single statement that dominates the load usually dominates response time too. Improving one query is far cheaper than tuning the whole database.",
                Technical = $"Top SQL with Top Events: SQL_ID {id}, plan hash {plan}, {(execs is null ? "" : N(execs) + " sampled executions, ")}events: {evText}." + (rowSource.Length > 0 ? $" Top row source: {rowSource}." : "") + $" About {N(sess, 2)} average active sessions. Check the plan for full scans, bad join methods or missing indexes; for wait-bound statements find the object behind the wait.",
                Evidence = [new("SQL_ID", id, plan.Length > 0 ? "plan hash " + plan : null), new("Share of sampled activity", P(pct)), new("Events", evText), .. (rowSource.Length > 0 ? new[] { new Evidence("Top row source", rowSource) } : Array.Empty<Evidence>()), new("SQL text", Short(text), null)],
                Recommendations =
                [
                    cpu ? "Look at the execution plan for full scans and large joins; reduce logical reads with indexes, better join order or fresh statistics."
                    : topEv.StartsWith("PX ", StringComparison.OrdinalIgnoreCase) ? "The parallel-execution wait is the query coordinator or its servers waiting for each other, not a wait on a database object: tune the statement itself (plan, join order, partition pruning) and check whether its degree of parallelism is larger than the work needs (V$PQ_TQSTAT shows skew between servers)."
                    : $"Find which object the '{topEv}' wait hits (Top DB Objects) and whether an index or a different plan would avoid it.",
                    "Run SQL Tuning Advisor on this statement (needs the Tuning Pack) or compare against an earlier plan.",
                    "If the statement runs only a few times for a long time, tune the statement; if it runs very often, look at the application calling it."
                ],
                Sql = [Plan(id, int.TryParse(plan, out var ph) ? ph : null), ByEvent(ctx, topEv.Length > 0 ? topEv : "CPU + Wait for CPU") with { Title = $"Sessions and plans for {id} (ASH)", Code = Limit(ctx, $"SELECT sql_plan_hash_value, sql_plan_operation, sql_plan_options, event, {Weight(ctx)} AS approx_db_seconds\nFROM   {View(ctx)}\nWHERE  {Window(ctx)} AND sql_id = '{SqlSafe.SqlId(id)}'\nGROUP  BY sql_plan_hash_value, sql_plan_operation, sql_plan_options, event\nORDER  BY approx_db_seconds DESC", 15) },
                    new($"Run the SQL Tuning Advisor on {id}", $"DECLARE\n  l_task VARCHAR2(64);\nBEGIN\n  l_task := DBMS_SQLTUNE.CREATE_TUNING_TASK(sql_id => '{SqlSafe.SqlId(id)}', time_limit => 300, task_name => 'apodofos_{SqlSafe.SqlId(id)}');\n  DBMS_SQLTUNE.EXECUTE_TUNING_TASK('apodofos_{SqlSafe.SqlId(id)}');\nEND;\n/\nSELECT DBMS_SQLTUNE.REPORT_TUNING_TASK('apodofos_{SqlSafe.SqlId(id)}') FROM dual;", "Needs the Oracle Tuning Pack license; the statement must still be in the cursor cache or AWR.")],
                Docs = [D.SqlTuningAdvisor, D.ReadingPlans, D.Transient],
                SourceViews = ["V$ACTIVE_SESSION_HISTORY", "V$SQL"],
                ChartRefs = ["ash-top-sql"],
                TableRefs = [t.Id]
            });
        }
    }

    /// <summary>The same SQL_ID with more than one plan hash value in the window: plan instability.</summary>
    private static void PlanMismatch(AnalysisContext ctx)
    {
        var t = T(ctx, "Top SQL with Top Events", "Top SQL Statements");
        if (t == null) return;
        var groups = t.DataRows.Where(r => r.Str("SQL ID").Length > 0 && r.Str("Planhash", "PlanHash").Length > 0)
            .GroupBy(r => r.Str("SQL ID")).Select(g => (Id: g.Key, Plans: g.Select(x => x.Str("Planhash", "PlanHash")).Distinct().ToList(), Pct: g.Max(x => x.Val("% Activity") ?? 0)))
            .Where(g => g.Plans.Count > 1 && g.Pct >= 5).ToList();
        if (groups.Count == 0) return;
        var g0 = groups.OrderByDescending(g => g.Pct).First();
        ctx.Add(new Finding
        {
            Severity = Sev(ctx, g0.Pct, Severity.Medium),
            Category = "SQL",
            Impact = g0.Pct,
            Title = $"SQL {g0.Id} ran with {g0.Plans.Count} different execution plans in this window",
            Plain = "The same statement was executed with more than one execution plan. When the database switches plans, the statement can be fast one minute and slow the next, and it is hard to predict which.",
            WhyItMatters = "Unstable plans cause unpredictable response times and are a common reason for 'it was fine yesterday'.",
            Technical = $"SQL_ID {g0.Id}: plan hashes {string.Join(", ", g0.Plans)} within the window ({P(g0.Pct)} of activity). Typical causes: bind peeking / adaptive cursor sharing, statistics changes, partition pruning differences. Compare the plans and, if one is clearly better, pin it with a SQL plan baseline.",
            Evidence = groups.Select(g => new Evidence(g.Id, string.Join(", ", g.Plans), $"{N(g.Pct, 1)}% of activity")).ToList(),
            Recommendations = ["Compare the plans (see SQL below) and find what changed: statistics, bind values, parameters.", "Load the good plan into a SQL plan baseline so the optimizer keeps using it."],
            Sql = [new("Plans seen for this statement in the AWR repository", $"SELECT plan_hash_value, COUNT(*) AS snapshots, SUM(elapsed_time_delta)/1e6 AS elapsed_s, SUM(executions_delta) AS execs\nFROM   dba_hist_sqlstat\nWHERE  sql_id = '{SqlSafe.SqlId(g0.Id)}'\nGROUP  BY plan_hash_value\nORDER  BY elapsed_s DESC;"),
                new("Load the better plan as a baseline", $"DECLARE n PLS_INTEGER;\nBEGIN\n  n := DBMS_SPM.LOAD_PLANS_FROM_CURSOR_CACHE(sql_id => '{SqlSafe.SqlId(g0.Id)}', plan_hash_value => &good_plan_hash);\nEND;\n/", "Changes optimizer behaviour for this statement; test first.")],
            Docs = [D.PlanManagement, D.Baselines, D.DbmsSpm],
            SourceViews = ["V$ACTIVE_SESSION_HISTORY", "DBA_HIST_SQLSTAT"],
            TableRefs = [t.Id]
        });
    }

    private static void RowSources(AnalysisContext ctx)
    {
        var t = T(ctx, "Top SQL with Top Row Sources");
        if (t == null || t.Rows.Count == 0) return;
        var scans = t.DataRows.Where(r => r.Str("Row Source").Contains("FULL", StringComparison.OrdinalIgnoreCase) && !r.Str("Row Source").Contains("FIXED TABLE", StringComparison.OrdinalIgnoreCase)).ToList();
        var pct = scans.Sum(r => r.Val("% RwSrc") ?? 0);
        if (pct < 10) return;
        var worst = scans.OrderByDescending(r => r.Val("% RwSrc") ?? 0).First();
        ctx.Add(new Finding
        {
            Severity = Sev(ctx, pct, Severity.Medium),
            Category = "SQL",
            Impact = pct,
            Title = $"Full scans (table or index) took {Pc(pct)} of sampled activity",
            Plain = "A noticeable part of the time went into reading entire tables or indexes instead of picking out a few rows. For small tables this is fine; for large ones it is often the reason a query is slow.",
            WhyItMatters = "Full scans read far more data than necessary, using CPU and disk that other users need.",
            Technical = $"Top SQL with Top Row Sources: TABLE ACCESS FULL / INDEX FAST FULL SCAN entries add up to {P(pct)}; the largest is {worst.Str("Row Source")} in SQL {worst.Str("SQL ID")} ({P(worst.Val("% RwSrc"))}, event {worst.Str("Top Event")}). Verify whether the table is large and whether a selective predicate could use an index.",
            Evidence = scans.OrderByDescending(r => r.Val("% RwSrc") ?? 0).Take(5).Select(r => new Evidence(r.Str("SQL ID"), r.Str("Row Source"), $"{r.Str("% RwSrc")}% of activity; {r.Str("Top Event")}")).ToList(),
            Recommendations = ["Check the plan of these statements and whether a suitable index exists.", "Make sure optimizer statistics are current; consider partitioning for large scanned tables."],
            Sql = [Plan(worst.Str("SQL ID"))],
            Docs = [D.AccessPaths, D.ReadingPlans],
            SourceViews = ["V$ACTIVE_SESSION_HISTORY"],
            TableRefs = [t.Id]
        });
    }

    private static void Literals(AnalysisContext ctx)
    {
        var t = T(ctx, "Top SQL using literals");
        if (t == null || t.Rows.Count == 0) return;
        var rows = Distinct(t, "SQL ID", "FORCE_MATCHING_SIGNATURE", "Force Matching Signature");
        var pct = t.DataRows.Sum(r => r.Val("% Activity") ?? 0);
        pct = Math.Min(100, pct);
        ctx.Add(new Finding
        {
            Severity = Sev(ctx, Math.Max(pct, 5), Severity.Medium),
            Category = "SQL",
            Impact = pct,
            Title = $"Statements with hard-coded values used {Pc(pct)} of sampled activity",
            Plain = "The application builds SQL with the values written into the text (for example WHERE id = 4711) instead of using placeholders. Every different value looks like a brand-new statement that the database must analyse again.",
            WhyItMatters = "Constant re-analysis burns CPU, floods the shared memory with near-identical statements, and is a classic scalability limit. It is also a security risk (SQL injection).",
            Technical = $"Top SQL using literals lists SQL sharing a FORCE_MATCHING_SIGNATURE ({rows.Count} entries). Fix in the application with bind variables; CURSOR_SHARING=FORCE is a stop-gap with side effects.",
            Evidence = rows.Take(5).Select(r => new Evidence(r.Str("SQL ID"), Short(r.Str("SQL Text"), 100), $"{r.Str("% Activity")}% of activity")).ToList(),
            Recommendations = ["Change the application to use bind variables.", "As a temporary measure only, consider CURSOR_SHARING=FORCE after testing."],
            Sql = [new("Statements that differ only by literal values", "SELECT force_matching_signature, COUNT(*) AS versions, MIN(sql_id) AS example_sql_id\nFROM   v$sqlarea\nWHERE  force_matching_signature <> 0\nGROUP  BY force_matching_signature\nHAVING COUNT(*) > 20\nORDER  BY versions DESC;")],
            Docs = [D.CursorSharing, D.SqlProcessing],
            SourceViews = ["V$ACTIVE_SESSION_HISTORY", "V$SQLAREA"],
            TableRefs = [t.Id]
        });
    }

    private static void Phases(AnalysisContext ctx)
    {
        var t = T(ctx, "Top Phases of Execution");
        if (t == null) return;
        double Phase(params string[] names) => t.DataRows.Where(r => names.Any(n => r.Str("Phase of Execution").Equals(n, StringComparison.OrdinalIgnoreCase))).Sum(r => r.Val("% Activity") ?? 0);
        var hard = Phase("Hard Parse");
        var parse = Phase("Parse");
        var conn = Phase("Connection Management");
        var seq = Phase("Sequence Load");
        void Add(double pct, double min, string title, string plain, string why, string tech, string[] recs, Severity cap)
        {
            if (pct < min) return;
            ctx.Add(new Finding
            {
                Severity = Sev(ctx, pct, cap), Category = "Workload profile", Impact = pct,
                Title = title.Replace("{p}", Pc(pct)), Plain = plain.Replace("{p}", Pc(pct)), WhyItMatters = why, Technical = tech,
                Evidence = t.DataRows.Where(r => (r.Val("% Activity") ?? 0) >= 1).Select(r => new Evidence(r.Str("Phase of Execution"), r.Str("% Activity") + "%")).ToList(),
                Recommendations = [.. recs], Docs = [D.SqlProcessing, D.Transient], SourceViews = ["V$ACTIVE_SESSION_HISTORY"], TableRefs = [t.Id]
            });
        }
        Add(hard, 5, "Hard parsing took {p} of sampled activity", "Sessions spent {p} of the time compiling SQL statements from scratch instead of running them.",
            "Compiling is expensive and serialises on shared memory; high levels signal literal SQL or a shared pool that is too small.",
            "Phase 'Hard Parse' from SQL_EXEC_OPCODE/IN_HARD_PARSE flags. See Top Parsing Module/Action and Top SQL using literals.",
            ["Use bind variables and check the Top Parsing Module/Action section for the program responsible.", "Check the shared pool size and cursor invalidations (DDL, statistics gathering)."], Severity.High);
        Add(parse, 10, "Parsing took {p} of sampled activity", "A large share of the time went into parsing (soft) SQL statements.", "Frequent soft parses mean the application re-submits the same statements instead of reusing prepared ones.",
            "Phase 'Parse' (soft parse and parse-call overhead). Use SESSION_CACHED_CURSORS and application-side statement caching.", ["Enable statement caching in the connection pool or driver; raise SESSION_CACHED_CURSORS."], Severity.Medium);
        Add(conn, 5, "Connection management took {p} of sampled activity", "Sessions spent {p} of the time logging on and off the database.", "Opening a new connection for every request is expensive and can saturate the listener and server.",
            "Phase 'Connection Management' (logon/logoff). Look for connection storms and missing connection pooling.", ["Use a connection pool and keep connections open between requests."], Severity.Medium);
        Add(seq, 5, "Sequence loading took {p} of sampled activity", "Sessions spent {p} of the time waiting to refill sequence number caches.", "A sequence with a small cache becomes a serialisation point under concurrency.",
            "Phase 'Sequence Load' (enq: SQ - contention / row cache lock).", ["Increase the sequence CACHE (for example 1000) for busy sequences."], Severity.Medium);
    }

    private static void CommandTypes(AnalysisContext ctx)
    {
        var t = T(ctx, "Top SQL Command Types");
        if (t == null || t.Rows.Count == 0) return;
        var top = t.DataRows.OrderByDescending(r => r.Val("% Activity") ?? 0).First();
        var pct = top.Val("% Activity") ?? 0;
        if (pct < 40) return;
        var cmd = top.Str("SQL Command Type");
        ctx.Add(new Finding
        {
            Severity = Severity.Info,
            Category = "Workload profile",
            Impact = pct / 2,
            Title = $"{cmd} statements made up {Pc(pct)} of sampled activity",
            Plain = $"Most of the active time was spent on {cmd} statements ({top.Str("Distinct SQLIDs")} different statements), which tells you what kind of workload this was.",
            WhyItMatters = "The mix of statement types decides which tuning levers apply: query tuning for SELECT, commit/redo and locking for INSERT/UPDATE/DELETE, parsing for PL/SQL and CALL.",
            Technical = $"Top SQL Command Types: {cmd} {P(pct)} over {top.Str("Distinct SQLIDs")} distinct SQL_IDs, {top.Str("Avg Active Sessions")} average active sessions.",
            Evidence = t.DataRows.Take(5).Select(r => new Evidence(r.Str("SQL Command Type"), r.Str("% Activity") + "%", r.Str("Distinct SQLIDs") + " distinct SQL_IDs")).ToList(),
            Docs = [D.Transient],
            SourceViews = ["V$ACTIVE_SESSION_HISTORY"],
            TableRefs = [t.Id]
        });
    }
}
