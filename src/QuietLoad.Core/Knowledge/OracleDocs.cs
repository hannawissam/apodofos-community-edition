// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using QuietLoad.Core.Analysis;

namespace QuietLoad.Core.Knowledge;

/// <summary>
/// References to the official Oracle Database 19c documentation the rules are based on.
/// The application is offline: these are shown as citations (and copied on click), never fetched.
/// </summary>
public static class OracleDocs
{
    private const string Base = "https://docs.oracle.com/en/database/oracle/oracle-database/19/";

    // Database Performance Tuning Guide (TGDBA)
    public static readonly DocRef Addm = new("Performance Tuning Guide: Automatic Performance Diagnostics (ADDM)", Base + "tgdba/automatic-performance-diagnostics.html");
    public static readonly DocRef Awr = new("Performance Tuning Guide: Gathering Database Statistics (AWR)", Base + "tgdba/gathering-database-statistics.html");
    public static readonly DocRef Measuring = new("Performance Tuning Guide: Measuring Database Performance (DB time, wait events)", Base + "tgdba/measuring-database-performance.html");
    public static readonly DocRef InstanceTuning = new("Performance Tuning Guide: Instance Tuning Using Performance Views", Base + "tgdba/instance-tuning-using-performance-views.html");
    public static readonly DocRef BufferCache = new("Performance Tuning Guide: Tuning the Database Buffer Cache", Base + "tgdba/tuning-database-buffer-cache.html");
    public static readonly DocRef SharedPool = new("Performance Tuning Guide: Tuning the Shared Pool and the Large Pool", Base + "tgdba/tuning-shared-pool-and-large-pool.html");
    public static readonly DocRef Pga = new("Performance Tuning Guide: Tuning the Program Global Area", Base + "tgdba/tuning-program-global-area.html");
    public static readonly DocRef Sga = new("Performance Tuning Guide: Tuning the System Global Area", Base + "tgdba/tuning-system-global-area.html");
    public static readonly DocRef RedoBuffer = new("Performance Tuning Guide: Configuring a Database for Performance (redo logs, undo, temp)", Base + "tgdba/configuring-database-for-performance.html");
    public static readonly DocRef MemoryAllocation = new("Performance Tuning Guide: Database Memory Allocation", Base + "tgdba/database-memory-allocation.html");
    public static readonly DocRef IoDesign = new("Performance Tuning Guide: I/O Configuration and Design", Base + "tgdba/IO-configuration-and-design.html");
    public static readonly DocRef OsResources = new("Performance Tuning Guide: Managing Operating System Resources", Base + "tgdba/managing-operating-system-resources.html");
    public static readonly DocRef Transient = new("Performance Tuning Guide: Analyzing Sampled Data (ASH)", Base + "tgdba/analyzing-sampled-data.html");
    public static readonly DocRef PerformanceMethod = new("Performance Tuning Guide: Performance Improvement Methods (Oracle Performance Method)", Base + "tgdba/performance-improvement-methods.html");
    public static readonly DocRef ConfiguringDb = new("Performance Tuning Guide: Configuring a Database for Performance", Base + "tgdba/configuring-database-for-performance.html");
    public static readonly DocRef DesignForPerformance = new("Performance Tuning Guide: Designing and Developing for Performance", Base + "tgdba/designing-and-developing-for-performance.html");

    // Database Reference (REFRN)
    public static readonly DocRef WaitEvents = new("Database Reference: Descriptions of Wait Events", Base + "refrn/descriptions-of-wait-events.html");
    public static readonly DocRef WaitClasses = new("Database Reference: Classes of Wait Events", Base + "refrn/classes-of-wait-events.html");
    public static readonly DocRef Statistics = new("Database Reference: Statistics Descriptions", Base + "refrn/statistics-descriptions-2.html");
    public static DocRef Parameter(string name) => new($"Database Reference: {name.ToUpperInvariant()} parameter", Base + "refrn/" + name.ToUpperInvariant() + ".html");

    // SQL Tuning Guide (TGSQL)
    public static readonly DocRef SqlTuningAdvisor = new("SQL Tuning Guide: Analyzing SQL with SQL Tuning Advisor", Base + "tgsql/sql-tuning-advisor.html");
    public static readonly DocRef SqlProfiles = new("SQL Tuning Guide: Managing SQL Profiles", Base + "tgsql/managing-sql-profiles.html");
    public static readonly DocRef Baselines = new("SQL Tuning Guide: Managing SQL Plan Baselines", Base + "tgsql/managing-sql-plan-baselines.html");
    public static readonly DocRef PlanManagement = new("SQL Tuning Guide: Overview of SQL Plan Management", Base + "tgsql/overview-of-sql-plan-management.html");
    public static readonly DocRef OptimizerStats = new("SQL Tuning Guide: Optimizer Statistics Concepts", Base + "tgsql/optimizer-statistics-concepts.html");
    public static readonly DocRef GatherStats = new("SQL Tuning Guide: Gathering Optimizer Statistics", Base + "tgsql/gathering-optimizer-statistics.html");
    public static readonly DocRef ReadingPlans = new("SQL Tuning Guide: Generating and Displaying Execution Plans", Base + "tgsql/generating-and-displaying-execution-plans.html");
    public static readonly DocRef CursorSharing = new("SQL Tuning Guide: Improving Real-World Performance Through Cursor Sharing", Base + "tgsql/improving-rwp-cursor-sharing.html");
    public static readonly DocRef AccessPaths = new("SQL Tuning Guide: Optimizer Access Paths", Base + "tgsql/optimizer-access-paths.html");
    public static readonly DocRef SqlProcessing = new("SQL Tuning Guide: SQL Processing (parsing)", Base + "tgsql/sql-processing.html");

    // PL/SQL Packages and Types Reference (ARPLS)
    public static readonly DocRef DbmsXplan = new("PL/SQL Packages Reference: DBMS_XPLAN", Base + "arpls/DBMS_XPLAN.html");
    public static readonly DocRef DbmsSqltune = new("PL/SQL Packages Reference: DBMS_SQLTUNE", Base + "arpls/DBMS_SQLTUNE.html");
    public static readonly DocRef DbmsStats = new("PL/SQL Packages Reference: DBMS_STATS", Base + "arpls/DBMS_STATS.html");
    public static readonly DocRef DbmsSpm = new("PL/SQL Packages Reference: DBMS_SPM", Base + "arpls/DBMS_SPM.html");
    public static readonly DocRef DbmsLock = new("PL/SQL Packages Reference: DBMS_LOCK", Base + "arpls/DBMS_LOCK.html");
    public static readonly DocRef DbmsWorkloadRepository = new("PL/SQL Packages Reference: DBMS_WORKLOAD_REPOSITORY", Base + "arpls/DBMS_WORKLOAD_REPOSITORY.html");

    // Administrator's Guide (ADMIN)
    public static readonly DocRef Undo = new("Administrator's Guide: Managing Undo", Base + "admin/managing-undo.html");
    public static readonly DocRef RedoLog = new("Administrator's Guide: Managing the Redo Log", Base + "admin/managing-the-redo-log.html");
    public static readonly DocRef ResourceManager = new("Administrator's Guide: Managing Resources with Oracle Database Resource Manager", Base + "admin/managing-resources-with-oracle-database-resource-manager.html");
    public static readonly DocRef ManagingMemory = new("Administrator's Guide: Managing Memory", Base + "admin/managing-memory.html");
    public static readonly DocRef Processes = new("Administrator's Guide: Managing Processes", Base + "admin/managing-processes.html");
    public static readonly DocRef SchemaSpace = new("Administrator's Guide: Managing Space for Schema Objects", Base + "admin/managing-space-for-schema-objects.html");
    public static readonly DocRef Indexes = new("Administrator's Guide: Managing Indexes", Base + "admin/managing-indexes.html");
    public static readonly DocRef Tablespaces = new("Administrator's Guide: Managing Tablespaces", Base + "admin/managing-tablespaces.html");

    // Active Session History (ASH reports)
    public static readonly DocRef VActiveSessionHistory = new("Database Reference: V$ACTIVE_SESSION_HISTORY", Base + "refrn/V-ACTIVE_SESSION_HISTORY.html");
    public static readonly DocRef DbaHistActiveSessHistory = new("Database Reference: DBA_HIST_ACTIVE_SESS_HISTORY", Base + "refrn/DBA_HIST_ACTIVE_SESS_HISTORY.html");
    public static readonly DocRef VEventName = new("Database Reference: V$EVENT_NAME (wait event parameters)", Base + "refrn/V-EVENT_NAME.html");
    public static readonly DocRef VPxSession = new("Database Reference: V$PX_SESSION", Base + "refrn/V-PX_SESSION.html");
    public static readonly DocRef ParallelExecution = new("VLDB and Partitioning Guide: Parallel Execution Concepts", Base + "vldbg/parallel-exec-intro.html");
    public static readonly DocRef Hprof = new("PL/SQL Packages Reference: DBMS_HPROF (hierarchical profiler)", Base + "arpls/DBMS_HPROF.html");

    // Concepts / RAC
    public static readonly DocRef Concurrency = new("Concepts: Data Concurrency and Consistency", Base + "cncpt/data-concurrency-and-consistency.html");
    public static readonly DocRef Rac = new("RAC Administration and Deployment Guide: Monitoring Performance", Base + "racad/monitoring-performance.html");
    public static readonly DocRef Upgrade = new("Database Upgrade Guide", Base + "upgrd/index.html");

    // Statspack (docs verified with HTTP 200; the Statspack chapter is only published for older releases, the current reference is ?/rdbms/admin/spdoc.txt)
    public static readonly DocRef StatspackGuide = new("Oracle9i Performance Guide: Using Statspack (snapshot levels, thresholds, spreport.sql)", "https://docs.oracle.com/cd/A97385_01/server.920/a96533/statspac.htm");
    public static readonly DocRef Licensing = new("Database Licensing Information User Manual (options and management packs)", Base + "dblic/Licensing-Information.html");
    public static readonly DocRef ControlManagementPackAccess = new("Database Reference: CONTROL_MANAGEMENT_PACK_ACCESS parameter", Base + "refrn/CONTROL_MANAGEMENT_PACK_ACCESS.html");
    public static readonly DocRef VLatch = new("Database Reference: V$LATCH", Base + "refrn/V-LATCH.html");
    public static readonly DocRef VWaitStat = new("Database Reference: V$WAITSTAT", Base + "refrn/V-WAITSTAT.html");
    public static readonly DocRef VSystemEvent = new("Database Reference: V$SYSTEM_EVENT", Base + "refrn/V-SYSTEM_EVENT.html");
}
