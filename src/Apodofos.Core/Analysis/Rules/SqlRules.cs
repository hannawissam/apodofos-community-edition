// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Core.Model;
using static Apodofos.Core.Analysis.AnalysisContext;
using static Apodofos.Core.Analysis.Rules.RuleHelpers;
using D = Apodofos.Core.Knowledge.OracleDocs;

namespace Apodofos.Core.Analysis.Rules;

/// <summary>Top SQL analysis based on the "SQL ordered by ..." sections (DBA_HIST_SQLSTAT / DBA_HIST_SQLTEXT).</summary>
public sealed class SqlRules : IRule
{
    public string Name => "SQL";

    public void Evaluate(AnalysisContext ctx)
    {
        TopElapsed(ctx);
        VersionCount(ctx);
        ParseCalls(ctx);
        Executions(ctx);
    }

    private static void TopElapsed(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("SQL ordered by Elapsed Time");
        if (t == null || t.Rows.Count == 0) return;
        var gets = ctx.Report.Table("SQL ordered by Gets");
        var reads = ctx.Report.Table("SQL ordered by Reads");

        var rank = 0;
        // PL/SQL wrappers that call the same program often appear twice with the same module, runtime and call count
        // (one entry per top-level call); report them once and list the twin in the evidence.
        var plsqlTwins = new List<(string Module, double? Elapsed, double? Execs, Finding Finding)>();
        foreach (var r in t.DataRows)
        {
            rank++;
            var pct = r.Num("%Total") ?? 0;
            if (pct < 5 && !(rank <= 3 && pct >= 2)) continue;
            if (rank > 10) break;

            var sqlId = r.Str("SQL Id");
            var elapsed = r.Num("Elapsed Time (s)", "Elapsed Time");
            var execs = r.Num("Executions");
            var perExec = r.Num("Elapsed Time per Exec (s)", "Elapsed per Exec");
            var pctCpu = r.Num("%CPU");
            var pctIo = r.Num("%IO");
            var module = r.Str("SQL Module", "Module");
            var text = ctx.SqlTexts.TryGetValue(sqlId, out var full) ? full : r.Str("SQL Text");
            var isPlsql = RuleHelpers.IsPlsqlText(text);
            var getsRow = gets.Find(x => x.Str("SQL Id") == sqlId);
            var readsRow = reads.Find(x => x.Str("SQL Id") == sqlId);
            var getsPerExec = getsRow.Val("Gets per Exec");
            var readsPerExec = readsRow.Val("Reads per Exec");

            var profile = pctCpu >= 50 ? "cpu" : pctIo >= 50 ? "io" : "wait";
            var sev = SeverityMath.FromDbTimePct(pct);
            // A PL/SQL block's elapsed time includes the SQL it runs, so it is not directly tunable: one step less urgent.
            if (isPlsql) sev = SeverityMath.Lower(sev);
            sev = SeverityMath.ForLoad(ctx, sev);

            // ASH view of the same statement (12c+ reports)
            var ashRows = ctx.Report.Table("Top SQL with Top Events")?.DataRows.Where(x => x.Str("SQL ID") == sqlId).ToList() ?? [];
            var ashEvent = ashRows.OrderByDescending(x => x.Num("% Event") ?? 0).FirstOrDefault();
            var ashEventName = ashRows.Count > 0 ? ashEvent.Str("Event") : "";
            var ashActivity = ashRows.Count > 0 ? ashEvent.Num("% Activity") : null;
            var ashRowSource = ashRows.Count > 0 ? ashEvent.Str("Top Row Source") : "";
            // For a PL/SQL block, the statements of the same program (module) that actually burn the time
            var children = isPlsql && module.Length > 0
                ? t.DataRows.Where(x => x.Str("SQL Id") != sqlId && x.Str("SQL Module", "Module") == module && !RuleHelpers.IsPlsqlText(ctx.SqlTexts.TryGetValue(x.Str("SQL Id"), out var ct) ? ct : x.Str("SQL Text")))
                            .Take(3).ToList()
                : [];

            if (isPlsql)
            {
                var twin = plsqlTwins.FirstOrDefault(x => x.Module == module && x.Elapsed is > 0 && elapsed is > 0 && Math.Abs(x.Elapsed.Value - elapsed.Value) / x.Elapsed.Value < 0.03
                                                          && x.Execs is > 0 && execs is > 0 && Math.Abs(x.Execs.Value - execs.Value) / x.Execs.Value < 0.01);
                if (twin.Finding != null)
                {
                    twin.Finding.Evidence.Add(new("Same program, near-identical call", sqlId, $"{P(pct)} of DB time, {N(execs)} executions; counted once, do not add the two percentages"));
                    continue;
                }
            }

            var recs = new List<string>();
            if (isPlsql)
                recs.Add("This is a PL/SQL call: its time includes the SQL it runs. Look for the child statements from the same module in the other 'SQL ordered by' sections and in 'Top PL/SQL Procedures'.");
            if (profile == "cpu")
                recs.Add($"The statement is CPU-bound ({P(pctCpu)} CPU). Reduce logical reads: check the execution plan for nested loops over large row sources, missing selective indexes, or functions on indexed columns.");
            if (profile == "io")
                recs.Add($"The statement is I/O-bound ({P(pctIo)} I/O). Reduce physical reads: better indexes, partition pruning, or avoiding full scans of large tables.");
            if (profile == "wait")
                recs.Add(ashEventName.Length > 0 && !ashEventName.StartsWith("CPU", StringComparison.Ordinal)
                    ? $"Most of its time is neither CPU nor disk. ASH shows it mainly waited on '{ashEventName}': see the matching wait-event finding."
                    : "Most of its time is neither CPU nor I/O: check ASH for the events it waited on (locks, contention, network).");
            if (children.Count > 0)
                recs.Add($"Tune the statements it runs instead: {string.Join(", ", children.Select(c => c.Str("SQL Id")))} (same module, see evidence).");
            if (execs is > 0 && perExec is < 0.01 && execs > 100000)
                recs.Add($"It runs very often ({N(execs)} times). Even small savings per execution matter, and the application may be able to call it fewer times (caching, array processing).");
            else if (perExec is >= 1)
                recs.Add($"Each execution takes {Dur(perExec)}. Compare the current plan with historical plans (DBMS_XPLAN.DISPLAY_AWR) to detect plan regressions.");
            recs.Add("Run the SQL Tuning Advisor on this SQL_ID and review its recommendations (indexes, SQL profile, restructuring).");
            recs.Add("Verify that optimizer statistics on the tables involved are current.");
            if (!isPlsql) recs.Add("If query 2 shows an earlier, faster plan_hash_value, fix it with a SQL plan baseline (SQL Plan Management).");

            var taskName = $"TUNE_{sqlId}";
            var neverFinished = execs is null or 0;
            var parallelNote = elapsed is > 0 && ctx.ElapsedSec is > 0 && elapsed > ctx.ElapsedSec * 1.05
                ? $" Its elapsed time ({Dur(elapsed)}) is longer than the {Dur(ctx.ElapsedSec)} of the report because the time of every parallel-query process or session running it is added up."
                : "";
            var execText = neverFinished
                ? $"{Dur(elapsed)}, but it had not finished even one execution in this period (a statement is counted only when it ends, so it was probably still running, for example a long report or parallel query)"
                : $"{Dur(elapsed)} over {Plural(execs, "execution")}" + (perExec is not null ? $", about {DurFine(perExec)} each" : "");
            var added = ctx.Add(new Finding
            {
                Severity = sev,
                Category = "SQL",
                Impact = pct,
                Title = $"SQL {sqlId} used {P(pct)} of all database time" + (module.Length > 0 ? $" (module {module})" : ""),
                Plain = $"One {(isPlsql ? "program call (a stored program or batch job, whose time includes the SQL statements it runs)" : "SQL statement")} is responsible for {P(pct)} of the time the database spent on user requests: {execText}." + parallelNote + " "
                        + (profile == "cpu" ? "It spends its time mostly working on the CPU, typically by reading far more data from memory than it needs."
                           : profile == "io" ? "It spends its time mostly reading from disk."
                           : (ashEventName.Length > 0 && !ashEventName.StartsWith("CPU", StringComparison.Ordinal) ? $"It spends much of its time waiting ('{ashEventName}')." : "It spends much of its time waiting on something other than CPU or disk.")),
                WhyItMatters = $"Improving this one statement could recover up to {P(pct)} of database time. That is usually the fastest win available.",
                Technical = $"From DBA_HIST_SQLSTAT: elapsed {N(elapsed, 1)} s, executions {N(execs)}, elapsed/exec {N(perExec, 3)} s, %CPU {P(pctCpu)}, %IO {P(pctIo)}"
                            + (getsPerExec is not null ? $", buffer gets/exec {N(getsPerExec)}" : "")
                            + (readsPerExec is not null ? $", physical reads/exec {N(readsPerExec)}" : "") + ".",
                Evidence =
                [
                    new("SQL Id", sqlId),
                    new("Module", module.Length > 0 ? module : "n/a"),
                    new("Share of DB time", P(pct)),
                    new("Elapsed time", Dur(elapsed)),
                    new("Executions", N(execs), neverFinished ? "none completed in the report period" : null),
                    new("Elapsed per execution", DurFine(perExec)),
                    new("CPU / I/O share", $"{P(pctCpu)} / {P(pctIo)}"),
                    new("Buffer gets per execution", N(getsPerExec)),
                    new("SQL text", text.Length > 600 ? text[..600] + " ..." : text),
                    .. (ashRows.Count > 0 ? new[] { new Evidence("ASH view", $"{N(ashActivity, 1)}% of all sampled activity", $"mostly '{ashEventName}'" + (ashRowSource.Length > 0 && !ashRowSource.StartsWith("**", StringComparison.Ordinal) ? $"; top row source {ashRowSource}" : "")) } : []),
                    .. children.Select(c => new Evidence("Statement run by it", c.Str("SQL Id"), $"{c.Str("%Total")}% of DB time, {c.Str("Executions")} executions: {(c.Str("SQL Text").Length > 90 ? c.Str("SQL Text")[..90] : c.Str("SQL Text"))}"))
                ],
                Recommendations = recs,
                Sql = [.. BuildSql(ctx, sqlId, module, isPlsql, taskName)],
                Docs = [D.SqlTuningAdvisor, D.DbmsXplan, D.GatherStats, D.PlanManagement, D.DbmsSpm],
                SourceViews = ["DBA_HIST_SQLSTAT", "DBA_HIST_SQLTEXT", "DBA_HIST_SQL_PLAN"],
                TableRefs = [t.Id],
                ChartRefs = ["top-sql-elapsed"]
            });
            if (isPlsql) plsqlTwins.Add((module, elapsed, execs, added));
        }
    }

    private static List<SqlSnippet> BuildSql(AnalysisContext ctx, string sqlId, string module, bool isPlsql, string taskName)
    {
        var history = new SqlSnippet("2. Plan and performance history across snapshots", $"""
                        SELECT s.snap_id,
                               TO_CHAR(s.end_interval_time, 'YYYY-MM-DD HH24:MI') AS snap_end,
                               q.plan_hash_value,
                               q.executions_delta AS execs,
                               ROUND(q.elapsed_time_delta / 1e6 / NULLIF(q.executions_delta, 0), 3) AS ela_per_exec_s,
                               ROUND(q.buffer_gets_delta / NULLIF(q.executions_delta, 0)) AS gets_per_exec,
                               ROUND(q.disk_reads_delta / NULLIF(q.executions_delta, 0)) AS reads_per_exec
                        FROM   dba_hist_sqlstat q
                               JOIN dba_hist_snapshot s
                                 ON s.snap_id = q.snap_id AND s.dbid = q.dbid AND s.instance_number = q.instance_number
                        WHERE  q.sql_id = '{sqlId}'
                        ORDER  BY s.snap_id;
                        """);
        if (isPlsql)
        {
            var list = new List<SqlSnippet>();
            if (module.Length > 0)
                list.Add(new("1. The SQL statements this program runs (same module, from AWR)", $"""
                        SELECT *
                        FROM  (SELECT q.sql_id,
                                      ROUND(SUM(q.elapsed_time_delta) / 1e6) AS elapsed_s,
                                      SUM(q.executions_delta)                AS execs,
                                      SUM(q.buffer_gets_delta)               AS buffer_gets
                               FROM   dba_hist_sqlstat q
                               WHERE  q.snap_id BETWEEN &begin_snap AND &end_snap
                               AND    q.module = '{module.Replace("'", "''")}'
                               GROUP  BY q.sql_id
                               ORDER  BY elapsed_s DESC)
                        WHERE ROWNUM <= 10;
                        """, "Tune these child statements; the PL/SQL call itself has no plan of its own."));
            list.Add(history);
            return list;
        }
        var spm = RuleHelpers.AtLeast(ctx, 12, 2)
            ? new SqlSnippet("5. Fix a known good plan with a SQL plan baseline (from AWR)", $"""
                        DECLARE
                          n PLS_INTEGER;
                        BEGIN
                          n := DBMS_SPM.LOAD_PLANS_FROM_AWR(
                                 begin_snap   => &begin_snap,
                                 end_snap     => &end_snap,
                                 basic_filter => q'[sql_id = '{sqlId}' AND plan_hash_value = &good_plan_hash_value]');
                          DBMS_OUTPUT.PUT_LINE(n || ' plan(s) loaded');
                        END;
                        /
                        """, "Use only after confirming which plan_hash_value performs best (query 2). DBMS_SPM.LOAD_PLANS_FROM_AWR is available from 12.2.")
            : new SqlSnippet("5. Fix a known good plan with a SQL plan baseline (via a SQL tuning set; releases before 12.2)", $"""
                        DECLARE
                          cur DBMS_SQLTUNE.SQLSET_CURSOR;
                          n   PLS_INTEGER;
                        BEGIN
                          DBMS_SQLTUNE.CREATE_SQLSET(sqlset_name => 'STS_{sqlId}');
                          OPEN cur FOR
                            SELECT VALUE(p)
                            FROM   TABLE(DBMS_SQLTUNE.SELECT_WORKLOAD_REPOSITORY(
                                           begin_snap     => &begin_snap,
                                           end_snap       => &end_snap,
                                           basic_filter   => q'[sql_id = '{sqlId}' AND plan_hash_value = &good_plan_hash_value]',
                                           attribute_list => 'ALL')) p;
                          DBMS_SQLTUNE.LOAD_SQLSET(sqlset_name => 'STS_{sqlId}', populate_cursor => cur);
                          n := DBMS_SPM.LOAD_PLANS_FROM_SQLSET(sqlset_name => 'STS_{sqlId}');
                          DBMS_OUTPUT.PUT_LINE(n || ' plan(s) loaded');
                        END;
                        /
                        """, "DBMS_SPM.LOAD_PLANS_FROM_AWR only exists from 12.2, so this release goes through a SQL tuning set. Use only after confirming which plan_hash_value performs best (query 2). Requires the Tuning Pack.");
        return
        [
                    new("1. Show the execution plan(s) captured in AWR", $"SELECT * FROM TABLE(DBMS_XPLAN.DISPLAY_AWR(sql_id => '{sqlId}', format => 'TYPICAL +PEEKED_BINDS'));"),
                    history,
                    new("3. Run SQL Tuning Advisor on this statement (from AWR)", $"""
                        DECLARE
                          l_task VARCHAR2(128);
                        BEGIN
                          l_task := DBMS_SQLTUNE.CREATE_TUNING_TASK(
                                      begin_snap  => &begin_snap,
                                      end_snap    => &end_snap,
                                      sql_id      => '{sqlId}',
                                      scope       => DBMS_SQLTUNE.SCOPE_COMPREHENSIVE,
                                      time_limit  => 1800,
                                      task_name   => '{taskName}');
                          DBMS_SQLTUNE.EXECUTE_TUNING_TASK(task_name => '{taskName}');
                        END;
                        /
                        SET LONG 1000000 LONGCHUNKSIZE 1000000 LINESIZE 200 PAGESIZE 0
                        SELECT DBMS_SQLTUNE.REPORT_TUNING_TASK('{taskName}') FROM dual;
                        """, "SQL Tuning Advisor requires the Oracle Tuning Pack license."),
                    new("4. Check optimizer statistics of the tables used by the plan", $"""
                        SELECT DISTINCT t.owner, t.table_name, t.num_rows, t.last_analyzed, t.stale_stats
                        FROM   dba_hist_sql_plan p
                               JOIN dba_tab_statistics t ON t.owner = p.object_owner AND t.table_name = p.object_name
                        WHERE  p.sql_id = '{sqlId}'
                        AND    t.partition_name IS NULL
                        ORDER  BY t.last_analyzed NULLS FIRST;
                        -- Refresh if stale:  EXEC DBMS_STATS.GATHER_TABLE_STATS('OWNER', 'TABLE_NAME', cascade => TRUE);
                        """),
                    spm
        ];
    }

    private static void VersionCount(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("SQL ordered by Version Count");
        if (t == null) return;
        var high = t.DataRows.Where(r => r.Num("Version Count") >= 100).Take(5).ToList();
        if (high.Count == 0) return;
        ctx.Add(new Finding
        {
            Severity = high.Any(r => r.Num("Version Count") >= 500) ? Severity.High : Severity.Medium,
            Category = "Parsing",
            Title = $"{high.Count} statement(s) have hundreds of versions in memory",
            Plain = "Some statements exist in memory as many separate copies (child cursors) instead of one shared copy.",
            WhyItMatters = "Many copies waste shared memory and make every execution slower to look up, which can cause 'cursor: mutex' and 'library cache' contention.",
            Technical = "High version counts are caused by bind mismatches, optimizer environment differences, adaptive cursor sharing, or bugs. V$SQL_SHARED_CURSOR shows the reason per child.",
            Evidence = high.Select(r => new Evidence(r.Str("SQL Id"), r.Str("Version Count") + " versions", r.Str("SQL Text"))).ToList(),
            Recommendations = ["Query V$SQL_SHARED_CURSOR for these SQL_IDs to see why child cursors are not shared.", "Check application bind types/lengths and session-level optimizer settings."],
            Sql = [new("Why cursors are not shared", $"SELECT * FROM v$sql_shared_cursor WHERE sql_id = '{high[0].Str("SQL Id")}';")],
            Docs = [D.CursorSharing, D.SharedPool],
            SourceViews = ["DBA_HIST_SQLSTAT (version_count)"],
            TableRefs = [t.Id]
        });
    }

    private static void ParseCalls(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("SQL ordered by Parse Calls");
        if (t == null || ctx.ElapsedSec is not > 0) return;
        var hot = t.DataRows
            .Where(r => r.Num("Parse Calls") is { } p && r.Num("Executions") is { } e && e > 0 && p / e > 0.9 && p / ctx.ElapsedSec > 10)
            .Take(5).ToList();
        if (hot.Count == 0) return;
        ctx.Add(new Finding
        {
            Severity = Severity.Low,
            Category = "Parsing",
            Title = $"{hot.Count} frequently run statement(s) are re-parsed on every execution",
            Plain = "The application closes and re-opens these statements each time it runs them.",
            WhyItMatters = "Every parse call costs CPU and shared memory access; keeping statements open removes this overhead.",
            Technical = "Parse calls ≈ executions. Enable statement caching in the driver/connection pool or PL/SQL cursor caching; check SESSION_CACHED_CURSORS.",
            Evidence = hot.Select(r => new Evidence(r.Str("SQL Id"), $"{r.Str("Parse Calls")} parses / {r.Str("Executions")} execs", r.Str("SQL Text"))).ToList(),
            Recommendations = ["Enable statement caching in the application driver (JDBC implicit cache, ODP.NET statement cache).", "Increase SESSION_CACHED_CURSORS if 'session cursor cache hits' is low."],
            Docs = [D.SharedPool, D.Parameter("SESSION_CACHED_CURSORS")],
            SourceViews = ["DBA_HIST_SQLSTAT (parse_calls)"],
            TableRefs = [t.Id]
        });
    }

    private static void Executions(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("SQL ordered by Executions");
        if (t == null || ctx.ElapsedSec is not > 0) return;
        var chatty = t.DataRows
            .Where(r => r.Num("Executions") is { } e && e / ctx.ElapsedSec >= 200 && (r.Num("Rows per Exec") ?? 1) < 1.5)
            .Take(5).ToList();
        if (chatty.Count == 0) return;
        ctx.Add(new Finding
        {
            Severity = Severity.Low,
            Category = "Application",
            Title = $"{chatty.Count} statement(s) run hundreds of times per second, each returning about one row or none",
            Plain = "The application asks the database many tiny questions one at a time, for example checking a status in a loop, instead of fewer bigger requests.",
            WhyItMatters = "Each call carries fixed overhead (network round trip, CPU). 'Row-by-row' processing scales poorly and is a common cause of slow batch jobs.",
            Technical = "Very high execution rates with ~1 row per execution indicate row-by-row (slow-by-slow) processing or polling. Prefer set-based SQL, array fetch/bind, or caching.",
            Evidence = chatty.Select(r => new Evidence(r.Str("SQL Id"), $"{N(r.Num("Executions") / ctx.ElapsedSec, 0)} executions/s", $"{r.Str("Rows per Exec")} rows/exec; {r.Str("SQL Text")}")).ToList(),
            Recommendations = ["Review the calling code for loops that can become a single set-based statement.", "Use array (bulk) operations and result caching where appropriate.", "Replace polling loops with event-driven designs (e.g. Advanced Queuing)."],
            Docs = [D.DesignForPerformance],
            SourceViews = ["DBA_HIST_SQLSTAT (executions)"],
            TableRefs = [t.Id],
            ChartRefs = ["top-sql-execs"]
        });
    }

    /// <summary>Merges the "SQL ordered by" sections into one list per SQL_ID for the SQL tab.</summary>
    public static List<SqlSummary> BuildSqlSummaries(AnalysisContext ctx)
    {
        var map = new Dictionary<string, SqlSummary>(StringComparer.OrdinalIgnoreCase);
        SqlSummary Get(string id) => map.TryGetValue(id, out var s) ? s : map[id] = new SqlSummary { SqlId = id };

        void Merge(string title, Action<SqlSummary, AwrRow> apply)
        {
            var t = ctx.Report.Table(title);
            if (t == null) return;
            foreach (var r in t.DataRows)
            {
                var id = r.Str("SQL Id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var s = Get(id);
                if (s.Module.Length == 0) s.Module = r.Str("SQL Module", "Module");
                if (!s.AppearsIn.Contains(title)) s.AppearsIn.Add(title);
                apply(s, r);
            }
        }

        Merge("SQL ordered by Elapsed Time", (s, r) =>
        {
            s.ElapsedSec ??= r.Num("Elapsed Time (s)"); s.Executions ??= r.Num("Executions");
            s.ElapsedPerExecSec ??= r.Num("Elapsed Time per Exec (s)"); s.PctDbTime ??= r.Num("%Total");
            s.PctCpu ??= r.Num("%CPU"); s.PctIo ??= r.Num("%IO");
        });
        Merge("SQL ordered by CPU Time", (s, r) => { s.CpuSec ??= r.Num("CPU Time (s)"); s.ElapsedSec ??= r.Num("Elapsed Time (s)"); s.Executions ??= r.Num("Executions"); s.PctCpu ??= r.Num("%CPU"); s.PctIo ??= r.Num("%IO"); });
        Merge("SQL ordered by User I/O Wait Time", (s, r) => { s.IoWaitSec ??= r.Num("User I/O Time (s)"); s.ElapsedSec ??= r.Num("Elapsed Time (s)"); });
        Merge("SQL ordered by Gets", (s, r) => { s.BufferGets ??= r.Num("Buffer Gets"); s.Executions ??= r.Num("Executions"); s.ElapsedSec ??= r.Num("Elapsed Time (s)"); });
        Merge("SQL ordered by Reads", (s, r) => { s.PhysicalReads ??= r.Num("Physical Reads"); s.Executions ??= r.Num("Executions"); });
        Merge("SQL ordered by Executions", (s, r) => { s.Executions ??= r.Num("Executions"); s.RowsProcessed ??= r.Num("Rows Processed"); s.ElapsedSec ??= r.Num("Elapsed Time (s)"); });
        Merge("SQL ordered by Parse Calls", (s, r) => { s.ParseCalls ??= r.Num("Parse Calls"); s.Executions ??= r.Num("Executions"); });
        Merge("SQL ordered by Version Count", (s, r) => { s.VersionCount ??= r.Num("Version Count"); });
        Merge("SQL ordered by Sharable Memory", (_, _) => { });

        foreach (var s in map.Values)
        {
            s.Text = ctx.SqlTexts.TryGetValue(s.SqlId, out var txt) ? txt : "";
            if (s.ElapsedPerExecSec is null && s.ElapsedSec is not null && s.Executions is > 0) s.ElapsedPerExecSec = s.ElapsedSec / s.Executions;
        }
        return map.Values.OrderByDescending(s => s.ElapsedSec ?? 0).ThenByDescending(s => s.BufferGets ?? 0).ToList();
    }
}
