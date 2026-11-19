// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using QuietLoad.Core.Model;
using QuietLoad.Core.Parsing;
using static QuietLoad.Core.Analysis.AnalysisContext;
using static QuietLoad.Core.Analysis.Rules.RuleHelpers;
using D = QuietLoad.Core.Knowledge.OracleDocs;

namespace QuietLoad.Core.Analysis.Rules;

/// <summary>Redo log sizing, undo, resource limits, init.ora parameters, release level, report window and ADDM.</summary>
public sealed class ConfigurationRules : IRule
{
    public string Name => "Configuration";

    public void Evaluate(AnalysisContext ctx)
    {
        LogSwitches(ctx);
        Undo(ctx);
        ResourceLimits(ctx);
        Parameters(ctx);
        Release(ctx);
        ReportWindow(ctx);
        Addm(ctx);
        Enqueues(ctx);
    }

    private static void LogSwitches(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("Instance Activity Stats - Thread Activity");
        var perHour = t?.RowByLabel("log switches (derived)")?.Num("per Hour");
        if (t == null || perHour is null) return;
        if (perHour <= 4)
        {
            return;
        }
        var redoPerSec = ctx.LoadPerSec("Redo size (bytes)", "Redo size");
        // Size logs so that a switch happens about every 20 minutes at this redo rate.
        var suggestedMb = redoPerSec is > 0 ? Math.Ceiling(redoPerSec.Value * 1200 / 1024 / 1024 / 512) * 512 : (double?)null;
        // Instance Recovery Stats: 'Log Sz RedoBlks' is derived from the smallest online log (512-byte blocks assumed) and
        // 'Opt Log Sz(M)' is Oracle's own OPTIMAL_LOGFILE_SIZE when FAST_START_MTTR_TARGET is set.
        var irs = ctx.Report.Table("Instance Recovery Stats");
        AwrRow? rec = irs is { Rows.Count: > 0 } ? new AwrRow(irs, irs.Rows[^1]) : null;
        var optMb = rec.Val("Opt Log Sz(M)");
        var curMb = rec.Val("Log Sz RedoBlks") * 512 / 1024 / 1024;
        var switchWaits = (RuleHelpers.ForegroundEventPct(ctx, "log file switch (checkpoint incomplete)") ?? 0) + (RuleHelpers.ForegroundEventPct(ctx, "log file switch (archiving needed)") ?? 0) + (RuleHelpers.ForegroundEventPct(ctx, "log file switch completion") ?? 0);
        var logsAlreadyLarge = curMb is > 0 && suggestedMb is > 0 && curMb >= suggestedMb * 0.8;
        // OPTIMAL_LOGFILE_SIZE answers "how big for the recovery-time target", not "how big for the switch rate": only use it when the logs are small.
        if (optMb is > 0 && !logsAlreadyLarge) suggestedMb = Math.Max(suggestedMb ?? 0, Math.Min(optMb.Value, Math.Max(suggestedMb ?? 0, 4096) * 2));
        var sevSw = switchWaits >= 1 || perHour > 12 ? Severity.Medium : logsAlreadyLarge ? Severity.Info : Severity.Low;
        ctx.Add(new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, sevSw),
            Category = "Configuration",
            Title = logsAlreadyLarge ? $"Redo logs switch {N(perHour, 1)} times per hour although they are already large" : $"Redo logs switch {N(perHour, 1)} times per hour (common target: every 15-30 minutes, i.e. 2-4 per hour)",
            Plain = "The database records every change in redo log files. These files fill up and are swapped often, which triggers extra background work (checkpoints and archiving) each time.",
            WhyItMatters = "Frequent switches add I/O and can make users wait ('log file switch' waits) when the database cannot keep up.",
            Technical = $"log switches (derived) = {N(perHour, 2)}/hour. Common practice (not an Oracle-documented limit) is a switch every 15-30 minutes at peak. Redo rate {Bytes(redoPerSec)}/s suggests online redo logs of about {N(suggestedMb)} MB each. "
                        + (curMb is > 0 ? $"Instance Recovery Stats indicate current logs of roughly {N(curMb)} MB (smallest log, assuming 512-byte redo blocks)" + (logsAlreadyLarge ? ", so size is probably not the cause: look for forced switches (ARCHIVE_LAG_TARGET, manual ALTER SYSTEM SWITCH LOGFILE, RMAN, Data Guard) or bursts of redo. " : ". ") : "")
                        + (switchWaits > 0 ? $"'log file switch' waits cost {P(switchWaits)} of DB time." : "No 'log file switch' waits were reported, so users were not delayed by the switches themselves."),
            Evidence = [new("Log switches per hour", N(perHour, 2)), new("Redo generated per second", Bytes(redoPerSec)), new("Suggested redo log size", suggestedMb is null ? "n/a" : $"{N(suggestedMb)} MB"), new("Approximate current log size", curMb is > 0 ? $"{N(curMb)} MB" : "n/a"), new("'log file switch' wait share", P(switchWaits))],
            Recommendations = logsAlreadyLarge
                ? ["Find out why switches are frequent although the logs are large: check ARCHIVE_LAG_TARGET, manual switches, RMAN or standby settings, and whether redo comes in short bursts (see 'Segments by DB Blocks Changes').", "No resize is needed unless 'log file switch' waits appear."]
                : ["Recreate the online redo log groups with a larger size (add new groups, switch, then drop the old ones when INACTIVE).", "Keep at least 3-4 groups per thread."],
            Sql =
            [
                new("Current redo logs and switch history", """
                SELECT group#, thread#, ROUND(bytes/1024/1024) AS size_mb, members, status FROM v$log ORDER BY thread#, group#;

                SELECT TO_CHAR(first_time, 'YYYY-MM-DD HH24') AS hour, COUNT(*) AS switches
                FROM   v$log_history
                WHERE  first_time > SYSDATE - 2
                GROUP  BY TO_CHAR(first_time, 'YYYY-MM-DD HH24')
                ORDER  BY 1;
                """),
                new("Add larger redo log groups", $"""
                ALTER DATABASE ADD LOGFILE THREAD 1 GROUP 11 ('+REDO') SIZE {(suggestedMb is null ? "4G" : N(suggestedMb).Replace(",", "") + "M")};
                ALTER DATABASE ADD LOGFILE THREAD 1 GROUP 12 ('+REDO') SIZE {(suggestedMb is null ? "4G" : N(suggestedMb).Replace(",", "") + "M")};
                ALTER DATABASE ADD LOGFILE THREAD 1 GROUP 13 ('+REDO') SIZE {(suggestedMb is null ? "4G" : N(suggestedMb).Replace(",", "") + "M")};
                -- switch until the old groups are INACTIVE, then drop them:
                ALTER SYSTEM SWITCH LOGFILE;
                ALTER SYSTEM CHECKPOINT;
                -- ALTER DATABASE DROP LOGFILE GROUP 1;
                """, "Adjust group numbers, thread and location (ASM disk group or file path) to your environment.")
            ],
            Docs = [D.RedoLog, D.ConfiguringDb],
            SourceViews = ["DBA_HIST_THREAD", "DBA_HIST_SYSSTAT"],
            TableRefs = [t.Id]
        });
    }

    private static void Undo(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("Undo Segment Summary");
        var r = t.FirstRow();
        if (t == null || r is null) return;
        var stoOos = r.Value.Str("STO/ OOS", "STO/OOS");
        var parts = stoOos.Split('/');
        var sto = parts.Length > 0 ? NumberParser.Parse(parts[0]) ?? 0 : 0;
        var oos = parts.Length > 1 ? NumberParser.Parse(parts[1]) ?? 0 : 0;
        var maxQry = r.Value.Num("Max Qry Len (s)");
        var retention = NumberParser.Parse(RuleHelpers.Param(ctx, "undo_retention"));
        // Unexpired extents stolen (uS) mean undo that should have been kept for UNDO_RETENTION was overwritten.
        var uParts = r.Value.Str("uS/uR/uU/ eS/eR/eU", "uS/uR/uU/eS/eR/eU").Split('/');
        var unexpiredStolen = uParts.Length > 0 ? NumberParser.Parse(uParts[0]) ?? 0 : 0;
        var tunedMin = NumberParser.Parse(r.Value.Str("Min/Max TR (mins)").Split('/')[0]);
        if (sto > 0 || oos > 0)
        {
            ctx.Add(new Finding
            {
                Severity = SeverityMath.ForLoad(ctx, Severity.High),
                Category = "Configuration",
                Title = $"Queries failed with 'snapshot too old' ({N(sto)}) or undo space errors ({N(oos)})",
                Plain = "Some long-running queries or transactions failed because the database could not keep enough 'undo' history (the before-image of changed data).",
                WhyItMatters = "These are user-visible errors (ORA-01555 / ORA-30036): reports or jobs failed and had to be rerun.",
                Technical = $"Undo Segment Summary STO/OOS = {stoOos}. Max query length {N(maxQry)} s vs UNDO_RETENTION {ctx.Param("undo_retention") ?? "n/a"}.",
                Evidence = [new("Snapshot-too-old errors", N(sto)), new("Out-of-space errors", N(oos)), new("Longest query", Dur(maxQry))],
                Recommendations = ["Size the undo tablespace using the Undo Advisor (or autoextend) to cover the longest query.", "Set UNDO_RETENTION to at least the longest query duration.", "Tune the long-running queries."],
                Sql =
                [
                    new("Undo statistics", "SELECT begin_time, end_time, undoblks, maxquerylen, tuned_undoretention, ssolderrcnt, nospaceerrcnt FROM v$undostat ORDER BY begin_time DESC;", "Shows the most recent 10-minute intervals first; add FETCH FIRST 48 ROWS ONLY (12c and later) to limit the output."),
                    new("Increase undo retention and space", $"ALTER SYSTEM SET undo_retention = {Math.Max(900, Math.Ceiling((maxQry ?? 900) * 1.2 / 60) * 60)} SCOPE = BOTH;\nALTER TABLESPACE &undo_tablespace ADD DATAFILE SIZE 10G AUTOEXTEND ON NEXT 1G MAXSIZE 32767M;", "ADD DATAFILE without a file name requires Oracle Managed Files (DB_CREATE_FILE_DEST); otherwise specify the path.")
                ],
                Docs = [D.Undo, D.Parameter("UNDO_RETENTION")],
                SourceViews = ["DBA_HIST_UNDOSTAT"],
                TableRefs = [t.Id]
            });
        }
        else if (unexpiredStolen > 0)
        {
            ctx.Add(new Finding
            {
                Severity = SeverityMath.ForLoad(ctx, Severity.Low),
                Category = "Configuration",
                Title = $"Undo that should have been kept was overwritten {N(unexpiredStolen)} time(s) (no query failed yet)",
                Plain = "The database ran short of undo space and reused 'undo' history (before-images of changed data) that it had promised to keep for a while. No query failed in this window, but long-running queries or flashback features are at risk.",
                WhyItMatters = "A query that needs older data than is left fails with 'snapshot too old' (ORA-01555) and has to be rerun.",
                Technical = $"Undo Segment Summary uS (unexpired stolen) = {N(unexpiredStolen)}; Max query length {Dur(maxQry)}; tuned retention (min) {N(tunedMin)} minutes; UNDO_RETENTION {RuleHelpers.Param(ctx, "undo_retention") ?? "n/a"}.",
                Evidence = [new("Unexpired extents stolen", N(unexpiredStolen)), new("Longest query", Dur(maxQry))],
                Recommendations = ["Add undo space (or let the undo data files autoextend) so that retention can be honoured.", "Tune the longest-running queries."],
                Docs = [D.Undo, D.Parameter("UNDO_RETENTION")],
                SourceViews = ["DBA_HIST_UNDOSTAT"],
                TableRefs = [t.Id]
            });
        }
        else if (maxQry is > 0 && retention is > 0 && maxQry > Math.Max(retention.Value, (tunedMin ?? 0) * 60) * 2)
        {
            ctx.Add(new Finding
            {
                Severity = SeverityMath.ForLoad(ctx, Severity.Low),
                Category = "Configuration",
                Title = $"Longest query ({Dur(maxQry)}) runs much longer than UNDO_RETENTION ({Dur(retention)})",
                Plain = "One query ran for a long time. If undo space is tight, such queries risk 'snapshot too old' errors.",
                WhyItMatters = "No errors occurred in this window, but this is a risk to keep an eye on.",
                Technical = "Oracle auto-tunes undo retention when space allows; with a fixed-size undo tablespace consider raising UNDO_RETENTION.",
                Evidence = [new("Max query length", Dur(maxQry)), new("UNDO_RETENTION", Dur(retention))],
                Docs = [D.Undo],
                SourceViews = ["DBA_HIST_UNDOSTAT"],
                TableRefs = [t.Id]
            });
        }
    }

    private static void ResourceLimits(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("Resource Limit Stats");
        if (t == null) return;
        var hits = new List<Evidence>();
        foreach (var r in t.DataRows)
        {
            var max = r.Num("Maximum Utilization");
            var limit = r.Num("Limit");
            if (max is null || limit is not > 0) continue;
            var pct = max / limit * 100;
            if (pct >= 80) hits.Add(new(r.Str("Resource Name"), $"{N(max)} of {N(limit)}", P(pct, 0) + " of limit"));
        }
        if (hits.Count == 0) return;
        ctx.Add(new Finding
        {
            Severity = hits.Any(h => h.Note!.StartsWith("9") || h.Note!.StartsWith("100")) ? Severity.High : Severity.Medium,
            Category = "Configuration",
            Title = $"{hits.Count} resource limit(s) are close to their maximum",
            Plain = "The database is configured with fixed maximums (for example, number of processes or sessions), and the peak usage since the instance started came close to them.",
            WhyItMatters = "When a limit is reached, new users simply cannot connect (ORA-00020 / ORA-00018) and applications fail.",
            Technical = "Resource Limit Stats (DBA_HIST_RESOURCE_LIMIT): maximum utilization (a high-water mark since instance startup, not only this window) at least 80% of the limit.",
            Evidence = hits,
            Recommendations = ["Raise the parameter (PROCESSES; SESSIONS is derived as 1.5 x PROCESSES + 22 unless set) with an SPFILE change and restart, or reduce connections through pooling."],
            Sql = [new("Current usage", "SELECT resource_name, current_utilization, max_utilization, limit_value FROM v$resource_limit ORDER BY resource_name;\nALTER SYSTEM SET processes = &new_value SCOPE = SPFILE;  -- requires restart")],
            Docs = [D.Processes, D.Parameter("PROCESSES"), D.Parameter("SESSIONS")],
            SourceViews = ["DBA_HIST_RESOURCE_LIMIT"],
            TableRefs = [t.Id]
        });
    }

    private static void Parameters(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("init.ora Parameters");
        if (t == null || ctx.Params.Count == 0) return;
        var issues = new List<(Evidence E, string Rec, DocRef Doc, Severity Sev)>();

        string? Pm(string n) => RuleHelpers.Param(ctx, n);
        var cs = Pm("cursor_sharing");
        if (cs != null && !cs.Equals("EXACT", StringComparison.OrdinalIgnoreCase))
            issues.Add((new("cursor_sharing", cs, "non-default; usually a workaround for literal SQL"), "CURSOR_SHARING other than EXACT is a workaround; the long-term fix is bind variables. SIMILAR is deprecated.", D.Parameter("CURSOR_SHARING"), Severity.Low));

        var oica = Pm("optimizer_index_cost_adj");
        if (oica != null && oica != "100")
            issues.Add((new("optimizer_index_cost_adj", oica, "default 100"), "Non-default OPTIMIZER_INDEX_COST_ADJ distorts the optimizer's costing; Oracle recommends system statistics and accurate object statistics instead.", D.Parameter("OPTIMIZER_INDEX_COST_ADJ"), Severity.Low));

        var sl = Pm("statistics_level");
        if (sl != null && sl.Equals("BASIC", StringComparison.OrdinalIgnoreCase))
            issues.Add((new("statistics_level", sl), "STATISTICS_LEVEL=BASIC disables AWR, advisories and automatic tuning; use TYPICAL.", D.Parameter("STATISTICS_LEVEL"), Severity.High));

        var ts = Pm("timed_statistics");
        if (ts != null && ts.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            issues.Add((new("timed_statistics", ts), "TIMED_STATISTICS must be TRUE for meaningful wait and SQL timings.", D.Parameter("TIMED_STATISTICS"), Severity.High));

        var mbrc = Pm("db_file_multiblock_read_count");
        if (mbrc != null)
            issues.Add((new("db_file_multiblock_read_count", mbrc, "explicitly set"), "Oracle recommends leaving DB_FILE_MULTIBLOCK_READ_COUNT unset so it is auto-tuned to the platform's maximum efficient I/O size.", D.Parameter("DB_FILE_MULTIBLOCK_READ_COUNT"), Severity.Low));

        var ofe = Pm("optimizer_features_enable");
        static double? MajorMinor(string v) { var p = v.Split('.'); return p.Length >= 2 && double.TryParse(p[0] + "." + p[1].PadLeft(2, '0'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null; }
        if (ofe != null && ctx.Info.Release.Length > 0 && MajorMinor(ofe) is { } ofeV && MajorMinor(ctx.Info.Release) is { } relV && ofeV < relV)
            issues.Add((new("optimizer_features_enable", ofe, $"database release {ctx.Info.Release}"), "OPTIMIZER_FEATURES_ENABLE set to an older release disables newer optimizer features. Keep it only if it was set deliberately to manage an upgrade.", D.Parameter("OPTIMIZER_FEATURES_ENABLE"), Severity.Low));

        var memTarget = NumberParser.Parse(Pm("memory_target"));
        var sgaBytes = NumberParser.Parse(Pm("sga_target")) ?? 0;
        var isLinux = ctx.Info.Platform.Contains("Linux", StringComparison.OrdinalIgnoreCase);
        var hugePages = Pm("use_large_pages");
        if (isLinux && sgaBytes >= 8L << 30 && hugePages != null && hugePages.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            issues.Add((new("use_large_pages", hugePages, $"SGA is {Bytes(sgaBytes)}"), "With a large SGA on Linux, HugePages cut memory page-table overhead (per-process and TLB misses) and make SGA memory non-swappable; consider USE_LARGE_PAGES=ONLY (or TRUE) after configuring vm.nr_hugepages. Changing it needs an instance restart.", D.Parameter("USE_LARGE_PAGES"), Severity.Low));
        var chk = Pm("db_block_checking");
        if (chk != null && !chk.Equals("OFF", StringComparison.OrdinalIgnoreCase) && !chk.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            issues.Add((new("db_block_checking", chk, "default OFF"), "DB_BLOCK_CHECKING above OFF verifies every changed block and costs CPU (Oracle documents about 1% to 10% overhead, higher for DML-heavy loads). Keep FULL/MEDIUM only if corruption protection is a deliberate requirement; LOW is a cheaper compromise.", D.Parameter("DB_BLOCK_CHECKING"), Severity.Low));
        var lwp = Pm("db_lost_write_protect");
        if (lwp != null && !lwp.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            issues.Add((new("db_lost_write_protect", lwp, "default NONE"), "Lost-write protection (TYPICAL/FULL) logs block versions in the redo stream, adding redo volume and some CPU; it is intended for Data Guard configurations.", D.Parameter("DB_LOST_WRITE_PROTECT"), Severity.Info));
        var pgl = Pm("pga_aggregate_limit");
        if (pgl == "0")
            issues.Add((new("pga_aggregate_limit", "0", "no hard limit on total PGA"), "PGA_AGGREGATE_LIMIT=0 disables the hard cap; a few runaway sessions can then exhaust server memory. Consider a limit comfortably above the target.", D.Parameter("PGA_AGGREGATE_LIMIT"), Severity.Info));
        var fso = Pm("filesystemio_options");
        if (fso != null && fso.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            issues.Add((new("filesystemio_options", fso), "FILESYSTEMIO_OPTIONS=NONE disables asynchronous and direct I/O on file systems; SETALL is recommended (not relevant for ASM).", D.Parameter("FILESYSTEMIO_OPTIONS"), Severity.Medium));
        if (memTarget is >= 8L << 30 && isLinux)
            issues.Add((new("memory_target", Pm("memory_target")!), "On Linux, MEMORY_TARGET (AMM) is incompatible with HugePages. For SGAs larger than a few GB Oracle recommends ASMM (SGA_TARGET + PGA_AGGREGATE_TARGET) with HugePages.", D.Parameter("MEMORY_TARGET"), Severity.Low));

        var scc = NumberParser.Parse(Pm("session_cached_cursors"));
        if (scc is < 50)
            issues.Add((new("session_cached_cursors", N(scc), "default 50"), "SESSION_CACHED_CURSORS below the default reduces cursor caching.", D.Parameter("SESSION_CACHED_CURSORS"), Severity.Low));

        var sgaTarget = NumberParser.Parse(Pm("sga_target"));
        if (ctx.Params.ContainsKey("sga_target") && sgaTarget is 0 && (memTarget is null or 0) && !IsPdbReport(ctx))
            issues.Add((new("sga_target", "0", "manual SGA management"), "SGA_TARGET=0 means memory pools are sized manually; Automatic Shared Memory Management lets Oracle rebalance buffer cache and shared pool.", D.Parameter("SGA_TARGET"), Severity.Info));

        var hidden = ctx.Params.Keys.Where(k => k.StartsWith('_')).ToList();
        if (hidden.Count > 0)
            issues.Add((new("Hidden (underscore) parameters", hidden.Count.ToString(), string.Join(", ", hidden.Take(12)) + (hidden.Count > 12 ? ", ..." : "")),
                "Underscore parameters change internal behaviour and should only be set under Oracle Support guidance or when an application vendor mandates them. Review that each one is still required.", D.ConfiguringDb, Severity.Info));

        if (issues.Count == 0) return;
        ctx.Add(new Finding
        {
            Severity = issues.Min(i => i.Sev),
            Category = "Configuration",
            Title = $"{issues.Count} initialization parameter(s) worth reviewing",
            Plain = "Some database settings differ from Oracle's defaults or recommendations. Each one is listed with an explanation below.",
            WhyItMatters = "Non-standard settings can silently degrade performance or disable useful diagnostics.",
            Technical = "Parameters from the init.ora section (DBA_HIST_PARAMETER). Validate each against application vendor requirements before changing.",
            Evidence = issues.Select(i => i.E).ToList(),
            Recommendations = issues.Select(i => i.Rec).ToList(),
            Sql = [new("Non-default parameters", "SELECT name, value, isdefault, ismodified FROM v$parameter WHERE isdefault = 'FALSE' ORDER BY name;")],
            Docs = issues.Select(i => i.Doc).DistinctBy(d => d.Url).ToList(),
            SourceViews = ["DBA_HIST_PARAMETER"],
            TableRefs = [t.Id]
        });
    }

    private static void Release(AnalysisContext ctx)
    {
        var v = ctx.Info.MajorVersion;
        if (v is null || v >= 19) return;
        var t = ctx.Report.TableWithColumns("DB Name", "Release");
        ctx.Add(new Finding
        {
            Severity = Severity.Info, // an upgrade note, not a performance problem: it must not count in the issue totals or lower the health score
            Category = "Configuration",
            Title = $"The database runs Oracle {ctx.Info.Release}, a release older than 19c",
            Plain = "This database runs an older version of Oracle. Releases before 19c are past (or near the end of) Oracle's standard support windows, and newer versions include many automatic performance improvements.",
            WhyItMatters = "Older releases miss optimizer, memory-management and diagnostic improvements and may no longer receive fixes or security patches (check Oracle's published release schedule, My Oracle Support Doc ID 742060.1). Releases 12.1 and earlier are the most exposed.",
            Technical = "This analyzer's rules are calibrated for Oracle Database 19c and later; most apply to earlier releases, but some report sections and column names differ. Check Oracle's support lifecycle documents for the support status of this release.",
            Evidence = [new("Release", ctx.Info.Release)],
            Recommendations = ["Plan an upgrade to a release that is still in Oracle's support lifecycle (19c or later) and use SQL Performance Analyzer / SQL plan baselines to protect plans during the upgrade."],
            Docs = [D.Upgrade, D.PlanManagement],
            TableRefs = t != null ? [t.Id] : []
        });
    }

    private static void ReportWindow(AnalysisContext ctx)
    {
        var min = ctx.Info.ElapsedMinutes;
        if (min is null) return;
        if (min < 10)
        {
            ctx.Add(new Finding
            {
                Severity = Severity.Info,
                Category = "Report scope",
                Title = $"The report covers only {Dur(min * 60)}, so rates and percentages are statistically weak",
                Plain = "Very short reports can be dominated by a single job or a chance event, so percentages may not represent the normal workload.",
                WhyItMatters = "Conclusions drawn from a few minutes of activity can mislead; compare with a longer, representative period before making changes.",
                Technical = "Oracle recommends AWR intervals long enough to contain a representative workload (typically 30-60 minutes) unless investigating one specific event.",
                Recommendations = ["Compare with an AWR report over a representative period (for example the same hour on a normal day)."],
                Docs = [D.Awr, D.Measuring],
                SourceViews = ["DBA_HIST_SNAPSHOT"]
            });
        }
        if (min >= 120)
        {
            ctx.Add(new Finding
            {
                Severity = Severity.Info,
                Category = "Report scope",
                Title = $"The report covers a long window ({Dur(min * 60)}), so short spikes are averaged out",
                Plain = "All numbers in an AWR report are averages over the whole period. Over a long period, a short but severe slowdown can look harmless.",
                WhyItMatters = "If users complained about a specific time, a report focused on that hour gives a much clearer diagnosis.",
                Technical = "Oracle recommends AWR reports covering the problem period, typically 15-60 minutes. Use ASH reports (ashrpt.sql) for transient issues.",
                Recommendations = ["Generate an AWR report for just the problem interval (awrrpt.sql) and an ASH report for spikes (ashrpt.sql)."],
                Sql = [new("Generate a focused AWR report (HTML) from SQL", """
                    SELECT output
                    FROM   TABLE(DBMS_WORKLOAD_REPOSITORY.AWR_REPORT_HTML(
                             l_dbid     => (SELECT dbid FROM v$database),
                             l_inst_num => &inst_num,
                             l_bid      => &begin_snap,
                             l_eid      => &end_snap));
                    """)],
                Docs = [D.Awr, D.Transient, D.DbmsWorkloadRepository],
                SourceViews = ["DBA_HIST_SNAPSHOT"]
            });
        }
    }

    private static void Addm(AnalysisContext ctx)
    {
        var t = ctx.Report.TableStartingWith("Top ADDM Findings");
        if (t == null || t.Rows.Count == 0) return;
        ctx.Add(new Finding
        {
            Severity = Severity.Info,
            Category = "ADDM",
            Title = $"Oracle's own advisor (ADDM) reported {t.Rows.Count} finding(s) in this period",
            Plain = "Oracle's built-in Automatic Database Diagnostic Monitor analysed the same period. Its headline findings are listed below and agree with or complement this analysis.",
            WhyItMatters = "ADDM findings are a second opinion straight from the database engine.",
            Technical = "Top ADDM Findings by Average Active Sessions (DBA_ADDM_FINDINGS). Run the full ADDM report for detailed recommendations.",
            Evidence = t.DataRows.Select(r => new Evidence(r.Str("Finding Name"), $"{r.Str("Percent active sessions of finding")}% of active sessions", $"{r.Str("Task Name")} ({r.Str("Begin Snap Time")} - {r.Str("End Snap Time")})")).ToList(),
            Recommendations = ["Review the full ADDM report for each task (addmrpt.sql or DBMS_ADDM.GET_REPORT)."],
            Sql = [new("Full ADDM report for a task", "SET LONG 1000000 PAGESIZE 0\nSELECT DBMS_ADDM.GET_REPORT('&task_name') FROM dual;", "ADDM requires the Diagnostics Pack license.")],
            Docs = [D.Addm],
            SourceViews = ["DBA_ADDM_FINDINGS", "DBA_ADVISOR_TASKS"],
            TableRefs = [t.Id],
            ChartRefs = ["addm"]
        });
    }

    private static void Enqueues(AnalysisContext ctx)
    {
        var t = ctx.Report.Table("Enqueue Activity");
        if (t == null || ctx.DbTimeSec is not > 0) return;
        var top = t.DataRows
            .Select(r => (Name: r.Str("Enqueue Type (Request Reason)"), Sec: r.Num("Wt Time (s)") ?? 0, Avg: r.Num("Av Wt Time(ms)")))
            .Where(x => x.Sec / ctx.DbTimeSec * 100 >= 1)
            .OrderByDescending(x => x.Sec).Take(5).ToList();
        if (top.Count == 0) return;
        // Rows already attached to a wait-event finding are not repeated.
        var shown = ctx.Findings.SelectMany(f => f.Evidence).Where(e => e.Label == "Enqueue activity").Select(e => e.Value).ToHashSet();
        top = top.Where(x => !shown.Contains(x.Name)).ToList();
        if (top.Count == 0) return;
        ctx.Add(new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, Severity.Low),
            Category = "Wait events",
            Title = "Lock (enqueue) waits are visible in Enqueue Activity",
            Plain = "Sessions waited for various database locks (queues that serialise access to shared resources). The most significant are listed below.",
            WhyItMatters = "Lock waits usually come from application design (concurrent updates) or configuration (sequence caches, block transaction slots).",
            Technical = "Enqueue Activity (DBA_HIST_ENQUEUE_STAT) rows whose wait time is at least 1% of DB time and that are not covered by a wait-event finding.",
            Recommendations = ["Look up each enqueue type in the Database Reference (Descriptions of Wait Events) and apply the matching remedy, for example larger sequence caches for SQ or an index on foreign keys for TM."],
            Evidence = top.Select(x => new Evidence(x.Name, Dur(x.Sec), Ms(x.Avg) + " avg")).ToList(),
            Docs = [D.Concurrency, D.WaitEvents],
            SourceViews = ["DBA_HIST_ENQUEUE_STAT"],
            TableRefs = [t.Id]
        });
    }
}
