// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using QuietLoad.Core.Model;
using static QuietLoad.Core.Analysis.AnalysisContext;
using D = QuietLoad.Core.Knowledge.OracleDocs;

namespace QuietLoad.Core.Analysis.Rules;

/// <summary>
/// Resource Manager throttling, judged together with host CPU: throttling while CPUs sit idle is a configuration
/// problem (usually the automatic maintenance-window plan), throttling on a saturated server is Resource Manager doing its job.
/// </summary>
public sealed class ResourceManagerRules : IRule
{
    public string Name => "Resource Manager";

    public void Evaluate(AnalysisContext ctx)
    {
        var evPct = RuleHelpers.ForegroundEventPct(ctx, "resmgr:cpu quantum") ?? 0;
        var inst = ctx.Report.Table("Instance CPU");
        var rm = inst.FirstRow().Val("%DB time waiting for CPU (Resource Manager)") ?? 0;
        var share = Math.Max(evPct, rm);
        if (share < 1) return;

        var idle = RuleHelpers.HostIdle(ctx);
        var cpuBusy = idle < 15;
        var plan = RuleHelpers.Param(ctx, "resource_manager_plan") ?? "";
        var maintenance = plan.Contains("MAINTENANCE_PLAN", StringComparison.OrdinalIgnoreCase);
        var scheduled = plan.StartsWith("SCHEDULER[", StringComparison.OrdinalIgnoreCase);
        var ev = RuleHelpers.ForegroundEvent(ctx, "resmgr:cpu quantum");

        var sev = cpuBusy ? Severity.Low : share >= 5 ? Severity.High : share >= 2 ? Severity.Medium : Severity.Low;
        var wio = ctx.Report.Table("Operating System Statistics")?.RowByLabel("RSRC_MGR_CPU_WAIT_TIME").Val("Value");

        var plain = $"The database's Resource Manager (a built-in traffic controller that shares CPU between groups of users) deliberately paused sessions for about {P(share)} of the database time. "
                    + (cpuBusy ? "The server's CPUs were nearly full, so this is Resource Manager doing its job." : idle is null ? "This report has no host CPU figures, so it cannot say whether the CPUs were full; if they were not, the pauses come from a limit in the active resource plan."
                       : $"The server still had {P(idle, 0)} of its CPU idle, so the pauses came from a limit set in the active resource plan, not from a shortage of CPUs.")
                    + (maintenance ? $" The active plan ('{plan}') is the one Oracle switches on during its automatic maintenance windows (nightly by default), which reserves CPU for maintenance jobs and caps user work." : "");

        ctx.Add(new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, sev),
            Category = "CPU",
            Impact = share,
            Title = cpuBusy ? $"Resource Manager held sessions back for {P(share)} of DB time (CPU was busy)" : $"Resource Manager held sessions back for {P(share)} of DB time while CPUs were idle",
            Plain = plain,
            WhyItMatters = cpuBusy
                ? "The throttling is expected when the server is full, but it is a sign that CPU demand exceeds what the plan lets this workload use."
                : "Users and batch jobs are slowed down on purpose although the hardware has spare capacity; the fix is a configuration change, not new hardware.",
            Technical = $"'resmgr:cpu quantum' {N(ev.Val("Waits"))} waits, {Dur(ev.Val("Total Wait Time (sec)", "Total Wait Time (s)", "Time(s)"))}, avg {Ms(ev.Val("Wait Avg(ms)", "Avg wait (ms)"))}; Instance CPU '%DB time waiting for CPU (Resource Manager)' = {P(rm)}"
                        + (wio is > 0 ? $"; OS statistic RSRC_MGR_CPU_WAIT_TIME = {N(wio)} (centiseconds)" : "")
                        + $". RESOURCE_MANAGER_PLAN = '{(plan.Length > 0 ? plan : "n/a")}'" + (scheduled ? " (set automatically by a Scheduler window)." : "."),
            Evidence = [new("DB time waiting for Resource Manager", P(share)), new("Host CPU idle", P(idle)), new("Active resource plan", plan.Length > 0 ? plan : "n/a")],
            Recommendations = cpuBusy
                ? ["Check that the plan gives the busy consumer groups the CPU share they need.", "Reduce CPU demand by tuning the top SQL (see the CPU and SQL findings)."]
                :
                [
                    maintenance ? "If business batch work must run during the maintenance window, either move the window (DBMS_SCHEDULER.SET_ATTRIBUTE on the window's START_DATE / DURATION), assign your own plan to the window, or stop Oracle from switching plans by setting the plan with the FORCE: prefix."
                                : "Review the active plan's directives: look for utilization_limit or low mgmt_p1 values on the consumer groups of the affected users.",
                    "Compare the throttled period with the busiest slice in the report: if the slowdown only happens inside a maintenance window, fix the window rather than the application."
                ],
            Sql =
            [
                new("Active plan, plan directives and CPU waits per consumer group", "SELECT name, is_top_plan, cpu_managed FROM v$rsrc_plan;\nSELECT name, consumed_cpu_time, cpu_waits, cpu_wait_time FROM v$rsrc_consumer_group ORDER BY cpu_wait_time DESC;\nSELECT plan, group_or_subplan, mgmt_p1, utilization_limit FROM dba_rsrc_plan_directives WHERE plan = (SELECT name FROM v$rsrc_plan WHERE is_top_plan = 'TRUE');"),
                new("Scheduler windows that activate resource plans", "SELECT window_name, resource_plan, enabled, active, repeat_interval, duration FROM dba_scheduler_windows ORDER BY window_name;"),
                new("Keep a chosen plan active even during maintenance windows", "ALTER SYSTEM SET resource_manager_plan = 'FORCE:&plan_name' SCOPE = BOTH;", "FORCE: prevents Scheduler windows from switching the plan, which also stops the maintenance plan from running; use only after deciding that is acceptable.")
            ],
            Docs = [D.ResourceManager, D.Parameter("RESOURCE_MANAGER_PLAN"), D.OsResources],
            SourceViews = ["DBA_HIST_SYSTEM_EVENT", "DBA_HIST_OSSTAT", "DBA_HIST_RSRC_PLAN"],
            TableRefs = new[] { inst?.Id, ctx.Report.Table("Foreground Wait Events")?.Id }.OfType<string>().ToList()
        });
    }
}

/// <summary>Real Application Clusters: global cache latency and interconnect traffic (only when the report is from a RAC database).</summary>
public sealed class RacRules : IRule
{
    public string Name => "RAC";

    public void Evaluate(AnalysisContext ctx)
    {
        if (!ctx.Info.IsRac) return;
        var fg = ctx.Report.Table("Foreground Wait Events");
        var gcRows = fg?.DataRows.Where(r => r.Label.StartsWith("gc ", StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        var gcPct = gcRows.Sum(r => r.Num("% DB time", "%DB time") ?? 0);

        // Oracle's own latency statistics when the report has them, otherwise the average of the gc block wait events.
        double? crMs = FindStat(ctx, "Avg global cache cr block receive time");
        double? curMs = FindStat(ctx, "Avg global cache current block receive time");
        double? enqMs = FindStat(ctx, "Avg global enqueue get time");
        double? EventAvg(string prefix)
        {
            var rows = gcRows.Where(r => r.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (r.Num("Waits") ?? 0) > 1000).ToList();
            var w = rows.Sum(r => r.Num("Waits") ?? 0);
            return w > 0 ? rows.Sum(r => (r.Num("Waits") ?? 0) * (r.Num("Avg wait (ms)", "Wait Avg(ms)") ?? 0)) / w : null;
        }
        crMs ??= EventAvg("gc cr block");
        curMs ??= EventAvg("gc current block");
        var worst = Math.Max(crMs ?? 0, curMs ?? 0);

        if (worst > 2 && gcPct >= 1)
        {
            ctx.Add(new Finding
            {
                Severity = SeverityMath.ForLoad(ctx, worst > 5 || gcPct >= 10 ? Severity.Medium : Severity.Low),
                Category = "RAC",
                Impact = gcPct,
                Title = $"Fetching data blocks from other cluster nodes is slow ({Ms(worst)} per block, {P(gcPct)} of DB time)",
                Plain = "In a cluster, a node often needs a data block that is in the memory of another node and asks for it over the private cluster network. Here those transfers take longer than they should.",
                WhyItMatters = "Every slow transfer delays the query or transaction that needs the block, and the delays multiply when many sessions share the same hot data across nodes.",
                Technical = $"gc cr block receive {Ms(crMs)}, gc current block receive {Ms(curMs)}; global cache wait events total {P(gcPct)} of DB time. Rule of thumb on a healthy dedicated interconnect (10 GbE or InfiniBand): about 1 ms or less, 2 ms or more deserves investigation, above 5 ms is poor. Slow averages with a healthy network usually mean busy LMS processes (CPU starvation) or very hot blocks.",
                Evidence = [new("gc cr block receive time", Ms(crMs)), new("gc current block receive time", Ms(curMs)), new("Global enqueue get time", Ms(enqMs)), new("gc events share of DB time", P(gcPct))],
                Recommendations =
                [
                    "Check the private interconnect: dedicated network, jumbo frames, no packet loss (netstat -s, ping latency), and that the cluster uses the intended interface.",
                    "Make sure the LMS background processes get CPU (they run at high priority; a saturated node delays every transfer).",
                    "Reduce cross-node traffic: route related work to the same instance with services, and fix hot blocks (sequences with NOORDER and large caches, hash-partitioned hot indexes).",
                    "Review 'Segments by Global Cache Buffer Busy' and the 'Cluster' wait class findings for the objects involved."
                ],
                Sql = [new("Global cache block counts and receive times per instance (cumulative since startup)", "SELECT inst_id, name, value FROM gv$sysstat WHERE name IN ('gc cr blocks received', 'gc cr block receive time', 'gc current blocks received', 'gc current block receive time') ORDER BY name, inst_id;", "The 'receive time' statistics are in centiseconds: average ms per block = time x 10 / blocks received.")],
                Docs = [D.Rac, D.WaitClasses, D.WaitEvents],
                SourceViews = ["DBA_HIST_SYSSTAT", "DBA_HIST_SYSTEM_EVENT", "DBA_HIST_INST_CACHE_TRANSFER"],
                TableRefs = fg != null ? [fg.Id] : []
            });
        }

        // interconnect traffic: blocks received + served per second x block size
        var recv = FindStat(ctx, "Global Cache blocks received");
        var served = FindStat(ctx, "Global Cache blocks served");
        if (recv is not null || served is not null)
        {
            var blockSize = NumberParserLocal(RuleHelpers.Param(ctx, "db_block_size")) ?? 8192;
            var mbps = ((recv ?? 0) + (served ?? 0)) * blockSize / 1048576;
            if (mbps >= 100)
            {
                ctx.Add(new Finding
                {
                    Severity = SeverityMath.ForLoad(ctx, mbps >= 400 ? Severity.Medium : Severity.Low),
                    Category = "RAC",
                    Title = $"Heavy data-block traffic between cluster nodes (about {N(mbps, 0)} MB/s)",
                    Plain = "The nodes of the cluster exchange a lot of data blocks over their private network every second.",
                    WhyItMatters = "A gigabit network carries about 100 MB/s in total; as the traffic approaches the capacity of the interconnect, every transfer slows down and the whole cluster is affected.",
                    Technical = $"Global Cache blocks received {N(recv, 1)}/s + served {N(served, 1)}/s x {N(blockSize)} bytes = {N(mbps, 0)} MB/s (data blocks only; messages and parallel query traffic come on top).",
                    Evidence = [new("Blocks received per second", N(recv, 1)), new("Blocks served per second", N(served, 1)), new("Approximate data traffic", N(mbps, 0) + " MB/s")],
                    Recommendations = ["Check the interconnect speed and utilisation (10 GbE or better is typical for busy clusters).", "Reduce traffic by partitioning the workload across instances with services."],
                    Docs = [D.Rac],
                    SourceViews = ["DBA_HIST_SYSSTAT", "DBA_HIST_INTERCONNECT_PINGS"]
                });
            }
        }
    }

    private static double? NumberParserLocal(string? s) => Parsing.NumberParser.Parse(s);

    /// <summary>Value (first numeric column) of a labelled row in the RAC sections.</summary>
    private static double? FindStat(AnalysisContext ctx, string labelFragment)
    {
        var n = AwrTable.Normalize(labelFragment);
        foreach (var t in ctx.Report.Tables.Where(t => t.TitleContains("Global Cache") || t.TitleContains("Interconnect") || t.TitleContains("Cluster")))
            foreach (var r in t.Rows)
                if (r.Length > 1 && AwrTable.Normalize(r[0]).Contains(n, StringComparison.Ordinal))
                    for (var i = 1; i < r.Length; i++)
                        if (Parsing.NumberParser.Parse(r[i]) is { } v) return v;
        return null;
    }
}

/// <summary>Container database (CDB/PDB) context for the report.</summary>
public sealed class CdbNoteRules : IRule
{
    public string Name => "Multitenant note";

    public void Evaluate(AnalysisContext ctx)
    {
        if (ctx.Info.IsCdb != true) return;
        var containers = ctx.Report.Tables
            .SelectMany(t => t.Headers.Select((h, i) => (t, i, h)).Where(x => x.h is "PDB Name" or "Container Name").SelectMany(x => t.Rows.Where(r => r.Length > x.i).Select(r => r[x.i].Trim())))
            .Where(v => v.Length > 0 && v != "N/A")
            .GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(8).ToList();
        ctx.Add(new Finding
        {
            Severity = Severity.Info,
            Category = "Report scope",
            Title = containers.Count > 0 ? $"This is a multitenant (CDB) report; the activity belongs to pluggable database(s) {string.Join(", ", containers)}" : "This is a multitenant (CDB) report",
            Plain = "The database is a 'container' that hosts one or more pluggable databases (PDBs). The statements, tables and parameters in this report are labelled with the pluggable database they belong to.",
            WhyItMatters = "To investigate a SQL statement or object from this report you must connect to the right container; queries run in the wrong one return nothing.",
            Technical = "The queries in the findings use DBA_HIST_* views. Run them inside the PDB when PDB-level AWR snapshots exist (AWR_PDB_AUTOFLUSH_ENABLED=TRUE, snapshots visible in DBA_HIST_SNAPSHOT of the PDB), otherwise connect to CDB$ROOT and use the CDB_HIST_* views filtered on CON_ID. Parameters can differ per PDB: the report lists a CDB-level and a PDB-level row for some of them.",
            Evidence = containers.Select(c => new Evidence("Container", c)).ToList(),
            Recommendations = ["Connect to the container named in the 'PDB Name' / 'Container Name' column before running the SQL from these findings."],
            Sql = [new("Pluggable databases and the AWR snapshot setting", "SELECT con_id, name, open_mode FROM v$pdbs;\nSELECT name, value FROM v$parameter WHERE name = 'awr_pdb_autoflush_enabled';")],
            Docs = [D.Awr, D.DbmsWorkloadRepository],
            SourceViews = ["CDB_HIST_SNAPSHOT", "DBA_HIST_SNAPSHOT"]
        });
    }
}

/// <summary>Exadata / flash cache: smart I/O ("Optimized Requests") when the report shows any.</summary>
public sealed class StorageOptimizationRules : IRule
{
    public string Name => "Storage optimization";

    public void Evaluate(AnalysisContext ctx)
    {
        var io = ctx.Report.Table("IO Profile");
        var opt = io?.RowByLabel("Optimized Requests");
        var total = io?.RowByLabel("Total Requests");
        var optTotal = opt.Val("Read+Write Per Second");
        if (optTotal is not > 0) return;
        var readPct = total.Val("Read per Second") is > 0 ? opt.Val("Read per Second") / total.Val("Read per Second") * 100 : null;
        // A trickle of optimized requests (under 5% of reads) is not worth a finding of its own.
        if (readPct is < 5) return;
        var eligible = ctx.InstanceStat("cell physical IO bytes eligible for predicate offload");
        var returned = ctx.InstanceStat("cell physical IO interconnect bytes returned by smart scan");
        var saved = ctx.InstanceStat("cell physical IO bytes saved by storage index");
        var flashHits = ctx.InstanceStat("cell flash cache read hits");
        ctx.Add(new Finding
        {
            Severity = Severity.Info,
            Category = "I/O",
            Title = readPct is not null ? $"{P(readPct, 0)} of read requests were served by flash or storage-cell optimizations" : "Storage-level I/O optimizations (flash / Exadata smart I/O) were used",
            Plain = "Part of the disk reads were answered by fast flash storage or by the storage servers themselves (Exadata), instead of by ordinary disks. This is good news for read speed.",
            WhyItMatters = "Reads served from flash take a fraction of the time of disk reads, so the unoptimized (disk) reads that remain are the ones worth looking at.",
            Technical = $"IO Profile 'Optimized Requests' {N(optTotal, 1)}/s" + (readPct is not null ? $" ({P(readPct, 1)} of read requests)" : "")
                        + (eligible is > 0 ? $"; smart scan: {Bytes(eligible)} eligible for offload, {Bytes(returned)} returned over the interconnect, {Bytes(saved)} saved by storage indexes" : "")
                        + (flashHits is > 0 ? $"; cell flash cache read hits {N(flashHits)}" : "")
                        + ". See 'SQL ordered by Physical Reads (UnOptimized)' and 'Segments by UnOptimized Reads' for the reads that still went to disk.",
            Evidence = [new("Optimized requests per second", N(optTotal, 1)), new("Optimized share of read requests", P(readPct, 1))],
            Docs = [D.IoDesign],
            SourceViews = ["DBA_HIST_IOSTAT_FUNCTION", "DBA_HIST_SYSSTAT"],
            TableRefs = io != null ? [io.Id] : []
        });
    }
}

/// <summary>
/// Parallel query on a small server: every parallel-query process is an active session, so a few parallel statements can
/// multiply the active-session count far beyond the CPU count and turn a scan into a queue.
/// </summary>
public sealed class ParallelExecutionRules : IRule
{
    public string Name => "Parallel execution";

    public void Evaluate(AnalysisContext ctx)
    {
        var trees = ctx.InstanceStat("DFO trees parallelized");
        var queries = ctx.InstanceStat("queries parallelized");
        if (trees is not >= 10 || ctx.Cpus is not > 0 || ctx.Aas is not { } aas) return;
        var ratio = aas / ctx.Cpus.Value;
        if (ratio < 1.5) return;
        var downgraded = new[] { "Parallel operations downgraded to serial", "Parallel operations downgraded 1 to 25 pct", "Parallel operations downgraded 25 to 50 pct", "Parallel operations downgraded 50 to 75 pct", "Parallel operations downgraded 75 to 99 pct" }
            .Sum(n => ctx.InstanceStat(n) ?? 0);
        var policy = RuleHelpers.Param(ctx, "parallel_degree_policy");
        var maxServers = RuleHelpers.Param(ctx, "parallel_max_servers");
        var directPct = (RuleHelpers.ForegroundEventPct(ctx, "direct path read") ?? 0) + (RuleHelpers.ForegroundEventPct(ctx, "db file scattered read") ?? 0);
        ctx.Add(new Finding
        {
            Severity = SeverityMath.ForLoad(ctx, ratio >= 3 ? Severity.Medium : Severity.Low),
            Category = "SQL",
            Impact = Math.Max(directPct, 0),
            Title = $"Parallel queries ran on a small server ({N(trees)} parallel executions, {N(aas, 1)} active sessions on {N(ctx.Cpus)} CPUs)",
            Plain = "Some queries were split into several helper processes that work side by side. Each helper counts as a busy session, so a handful of these queries fills a small server and everything else queues behind them.",
            WhyItMatters = "Parallel query shortens one query by borrowing the whole machine. On a server with few CPUs the borrowed capacity comes from other users, so overall response time gets worse.",
            Technical = $"'DFO trees parallelized' {N(trees)}" + (queries is > 0 ? $", 'queries parallelized' {N(queries)}" : "") + $"; AAS {N(aas, 1)} vs {N(ctx.Cpus)} CPUs ({N(ratio, 1)}x). PARALLEL_DEGREE_POLICY = {policy ?? "n/a"}, PARALLEL_MAX_SERVERS = {maxServers ?? "n/a"}. Elapsed time of a parallel statement in 'SQL ordered by Elapsed Time' adds up all its processes, so it can exceed the report window.",
            Evidence = [new("Parallel executions (DFO trees)", N(trees)), new("Queries parallelized", N(queries)), new("Downgraded operations", N(downgraded)), new("Active sessions per CPU", N(ratio, 1)), new("Scan-type reads share of DB time", P(directPct))],
            Recommendations =
            [
                "Lower the degree of parallelism of the big scans (hint, table DEGREE, or PARALLEL_DEGREE_LIMIT) so that the helpers of all concurrent queries together do not exceed the CPU count.",
                "Cap parallel capacity with PARALLEL_MAX_SERVERS or a Resource Manager directive (PARALLEL_SERVER_LIMIT) and run the large reports outside business hours.",
                "Reduce the data scanned instead (partition pruning, summary tables) so that less parallelism is needed."
            ],
            Sql = [new("Parallel parameters and current parallel servers", "SELECT name, value FROM v$parameter WHERE name LIKE 'parallel%' ORDER BY name;\nSELECT statistic, value FROM v$pq_sysstat WHERE statistic IN ('Servers Busy', 'Servers Idle', 'Servers Started');")],
            Docs = [D.Parameter("PARALLEL_MAX_SERVERS"), D.Parameter("PARALLEL_DEGREE_POLICY"), D.ResourceManager],
            SourceViews = ["DBA_HIST_SYSSTAT"],
            TableRefs = ctx.Report.Table("Instance Activity Stats") is { } ia ? [ia.Id] : []
        });
    }
}
