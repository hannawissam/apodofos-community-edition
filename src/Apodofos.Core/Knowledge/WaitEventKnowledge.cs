// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Core.Analysis;
using D = Apodofos.Core.Knowledge.OracleDocs;

namespace Apodofos.Core.Knowledge;

/// <summary>Plain-English and DBA knowledge about a wait event (or a family of events).</summary>
public sealed class WaitEventInfo
{
    public required string Pattern { get; init; }
    /// <summary>True: Pattern is a prefix (e.g. "gc " for all RAC global cache events).</summary>
    public bool Prefix { get; init; }
    public required string Friendly { get; init; }
    public required string Plain { get; init; }
    public required string Why { get; init; }
    public required string Technical { get; init; }
    public string[] Actions { get; init; } = [];
    public SqlSnippet[] Sql { get; init; } = [];
    public DocRef[] Docs { get; init; } = [];
    /// <summary>Healthy upper bound for the average wait in ms (null = not latency-driven).</summary>
    public double? HealthyAvgMs { get; init; }
}

/// <summary>
/// Knowledge base for the wait events most often seen at the top of AWR reports.
/// All explanations are original wording written for this tool; they cite the Oracle Database documentation
/// by link only (see <see cref="OracleDocs"/>) and do not reproduce its text.
/// </summary>
public static class WaitEventKnowledge
{
    private static readonly SqlSnippet AshByEvent = new("Find which SQL and objects suffered this wait (AWR ASH history)",
        """
        -- Replace &event, and the snapshot range if needed
        SELECT *
        FROM  (SELECT ash.sql_id,
                      ash.current_obj#,
                      o.owner || '.' || o.object_name AS object_name,
                      COUNT(*) * 10                     AS approx_seconds   -- DBA_HIST ASH keeps 1 sample in 10
               FROM   dba_hist_active_sess_history ash
                      LEFT JOIN dba_objects o ON o.object_id = ash.current_obj#
               WHERE  ash.event = '&event'
               AND    ash.snap_id BETWEEN &begin_snap AND &end_snap
               GROUP  BY ash.sql_id, ash.current_obj#, o.owner, o.object_name
               ORDER  BY approx_seconds DESC)
        WHERE ROWNUM <= 20;
        """, "Querying ASH (DBA_HIST_ACTIVE_SESS_HISTORY) requires the Oracle Diagnostics Pack license.");

    private static readonly SqlSnippet EventHistogram = new("Check the latency distribution of this wait right now",
        """
        SELECT event, wait_time_milli AS bucket_upper_ms, wait_count
        FROM   v$event_histogram
        WHERE  event = '&event'
        ORDER  BY wait_time_milli;
        """);

    private static readonly SqlSnippet EventTrend = new("Trend of this wait across snapshots (DBA_HIST_SYSTEM_EVENT)",
        """
        SELECT s.snap_id,
               TO_CHAR(s.end_interval_time, 'YYYY-MM-DD HH24:MI') AS snap_end,
               e.total_waits - LAG(e.total_waits) OVER (ORDER BY s.snap_id) AS waits,
               ROUND((e.time_waited_micro - LAG(e.time_waited_micro) OVER (ORDER BY s.snap_id)) / 1e6, 1) AS seconds_waited
        FROM   dba_hist_system_event e
               JOIN dba_hist_snapshot s
                 ON s.snap_id = e.snap_id AND s.dbid = e.dbid AND s.instance_number = e.instance_number
        WHERE  e.event_name = '&event'
        AND    e.instance_number = &inst_num
        ORDER  BY s.snap_id;
        """);

    public static readonly IReadOnlyList<WaitEventInfo> All =
    [
        new()
        {
            Pattern = "DB CPU", Friendly = "Working on the CPU",
            Plain = "This is time the database spent actively working (calculating, reading data from memory, sorting). It is not waiting, it is busy.",
            Why = "CPU time is normal and good when it is used efficiently. When it dominates and the server's processors are close to full, users queue for CPU and everything slows down. The usual cause is a handful of SQL statements doing far more work than needed.",
            Technical = "DB CPU is the foreground CPU time from the time model (V$SYS_TIME_MODEL). High DB CPU % of DB time with high logical reads usually means inefficient SQL (excessive buffer gets from poor plans, missing indexes, or row-by-row processing). Judge saturation by CPU demand (DB CPU + background CPU per second in the Load Profile) and host CPU busy %, compared with the CPU count and, on hyper-threaded servers, the physical core count; Average Active Sessions also counts sessions that are waiting and is not a CPU measure.",
            Actions =
            [
                "Tune the top statements in 'SQL ordered by CPU Time' and 'SQL ordered by Gets'. They usually account for most of the CPU.",
                "Look for statements with very high buffer gets per execution: they often have a poor execution plan or a missing index.",
                "Check that optimizer statistics are current so the optimizer chooses efficient plans.",
                "If CPU is really saturated (host CPU busy above roughly 85-90%, or CPU demand close to the number of CPUs) and the SQL is already efficient, consider Resource Manager or more CPU capacity."
            ],
            Sql =
            [
                new("Top SQL by CPU in the AWR window (DBA_HIST_SQLSTAT)",
                """
                SELECT *
                FROM  (SELECT sql_id,
                              ROUND(SUM(cpu_time_delta) / 1e6)     AS cpu_seconds,
                              SUM(executions_delta)                AS executions,
                              ROUND(SUM(buffer_gets_delta) / NULLIF(SUM(executions_delta), 0)) AS gets_per_exec
                       FROM   dba_hist_sqlstat
                       WHERE  snap_id BETWEEN &begin_snap AND &end_snap
                       GROUP  BY sql_id
                       ORDER  BY cpu_seconds DESC)
                WHERE ROWNUM <= 15;
                """)
            ],
            Docs = [D.Measuring, D.OsResources, D.SqlTuningAdvisor]
        },
        new()
        {
            Pattern = "db file sequential read", Friendly = "Reading single blocks from disk", HealthyAvgMs = 10,
            Plain = "Sessions are waiting for single blocks of data to be fetched from disk. This usually happens when the database looks up rows through an index.",
            Why = "Every millisecond of disk latency is multiplied by the number of reads. Large volumes mean SQL is reading more rows than necessary. High average latency means the storage is slow or overloaded.",
            Technical = "Single-block physical reads (index range/unique scans, table access by ROWID, undo). Assess both count (SQL efficiency: 'SQL ordered by Reads' / 'User I/O Wait Time') and latency (storage: average wait above ~10 ms on spinning disk, or above 1-2 ms on flash, is slow). The 'Segments by Physical Reads' section shows which objects are read.",
            Actions =
            [
                "Tune the SQL in 'SQL ordered by User I/O Wait Time' and 'SQL ordered by Reads': look for unselective index range scans and missing composite indexes.",
                "If the average wait is high (more than 10 ms), ask the storage team to check disk latency and queueing for the busiest data files (see File IO Stats).",
                "If the Buffer Pool Advisory shows a much lower 'Est Phys Read Factor' at a larger size, increase the buffer cache.",
                "Consider index compression or partitioning for very large, frequently read indexes."
            ],
            Sql = [AshByEvent, EventHistogram],
            Docs = [D.WaitEvents, D.IoDesign, D.BufferCache, D.AccessPaths]
        },
        new()
        {
            Pattern = "db file scattered read", Friendly = "Reading many blocks at once (full scans)", HealthyAvgMs = 20,
            Plain = "Sessions are reading large chunks of tables or indexes from disk at once. This is what a full table scan does.",
            Why = "Full scans of large tables are expensive. If they run often during the business day they consume disk and memory bandwidth that other users need.",
            Technical = "Multiblock reads into the buffer cache (full table scans / index fast full scans). Check 'Segments by Table Scans' and the SQL in 'SQL ordered by Reads'. A missing index, stale statistics, or an implicit datatype conversion that disables an index are common causes.",
            Actions =
            [
                "Identify the statements doing full scans and check whether an index would serve them better.",
                "Verify optimizer statistics are current for the scanned tables.",
                "For legitimately large scans (reporting), consider partitioning, parallel query, or scheduling outside peak hours.",
                "Small, frequently scanned lookup tables can be cached (Buffer Pool Advisory / KEEP pool)."
            ],
            Sql = [AshByEvent, EventHistogram],
            Docs = [D.WaitEvents, D.AccessPaths, D.IoDesign]
        },
        new()
        {
            Pattern = "db file parallel read", Friendly = "Reading several blocks in parallel", HealthyAvgMs = 20,
            Plain = "The database is fetching several separate blocks from disk in one batch, typically while visiting many table rows found through an index.",
            Why = "This is more efficient than one-by-one reads, but large amounts still mean a lot of disk work for the SQL involved.",
            Technical = "Batched/prefetched single-block reads (index-driven table access, recovery). Tune like db file sequential read: reduce rows visited and check storage latency.",
            Actions = ["Tune the SQL with the most physical reads.", "Check storage latency for the involved data files."],
            Sql = [AshByEvent],
            Docs = [D.WaitEvents, D.IoDesign]
        },
        new()
        {
            Pattern = "direct path read temp", Friendly = "Reading temporary work data from disk", HealthyAvgMs = 20,
            Plain = "Sorts, hash joins or other operations ran out of memory and spilled their work to the temporary tablespace on disk. Now the data is being read back.",
            Why = "Disk is thousands of times slower than memory. Operations that spill to disk can take many times longer than they would in memory.",
            Technical = "Work areas (sort, hash-join, bitmap merge) that did not fit in PGA wrote to TEMP (one-pass/multi-pass executions). Check 'PGA Aggr Target Histogram' for 1-pass/M-pass executions, 'PGA Memory Advisory', and SQL with large sorts.",
            Actions =
            [
                "Check the PGA Memory Advisory: if the estimated cache hit % improves at a larger size, increase PGA_AGGREGATE_TARGET.",
                "Tune the SQL with large sorts or hash joins (reduce rows earlier, avoid unnecessary ORDER BY/DISTINCT).",
                "Make sure the TEMP tablespace is on fast storage."
            ],
            Sql =
            [
                new("Who is using TEMP right now", """
                SELECT s.sid, s.username, u.sql_id, u.segtype,
                       ROUND(u.blocks * t.block_size / 1024 / 1024) AS mb_used
                FROM   v$tempseg_usage u
                       JOIN v$session s ON s.saddr = u.session_addr
                       JOIN dba_tablespaces t ON t.tablespace_name = u.tablespace
                ORDER  BY mb_used DESC;
                """),
                new("PGA advice", """
                SELECT ROUND(pga_target_for_estimate / 1024 / 1024) AS target_mb, pga_target_factor,
                       estd_pga_cache_hit_percentage, estd_overalloc_count
                FROM   v$pga_target_advice
                ORDER  BY pga_target_factor;
                """)
            ],
            Docs = [D.Pga, D.WaitEvents]
        },
        new()
        {
            Pattern = "direct path write temp", Friendly = "Writing temporary work data to disk", HealthyAvgMs = 20,
            Plain = "Sorts or joins did not fit in memory and are being written to temporary disk space.",
            Why = "Work that spills to disk is much slower than in-memory work and adds load on storage.",
            Technical = "Work area spill to TEMP. Same remedies as direct path read temp: PGA sizing and SQL tuning.",
            Actions = ["Review PGA Memory Advisory and increase PGA_AGGREGATE_TARGET if advised.", "Tune SQL with large sorts / hash joins."],
            Docs = [D.Pga, D.WaitEvents]
        },
        new()
        {
            Pattern = "direct path read", Friendly = "Reading data directly from disk (bypassing cache)", HealthyAvgMs = 20,
            Plain = "Large scans are reading data straight from disk into the session's private memory, bypassing the shared cache. This is typical for big full table scans and parallel queries.",
            Why = "This is efficient for one-off large scans. When it happens constantly it means large tables are scanned repeatedly, which consumes heavy disk bandwidth.",
            Technical = "Serial direct path reads (11g+ adaptive direct reads for large segments) or parallel query. Check 'Segments by Direct Physical Reads' and the SQL in 'SQL ordered by Reads'. Repeated full scans of large tables often signal a missing index or partition pruning.",
            Actions =
            [
                "Find the segments and SQL doing direct reads and check whether full scans are really necessary.",
                "Consider partitioning (with pruning) or indexes for selective queries.",
                "Validate storage throughput (MB/s) for the volume of data scanned."
            ],
            Sql = [AshByEvent],
            Docs = [D.WaitEvents, D.IoDesign]
        },
        new()
        {
            Pattern = "direct path write", Friendly = "Writing data directly to disk", HealthyAvgMs = 20,
            Plain = "The database is writing data straight to disk, bypassing the cache. This is common for bulk loads, CREATE TABLE AS SELECT and parallel operations.",
            Why = "Usually expected during batch loads. Slow writes extend batch windows.",
            Technical = "Direct-path writes (APPEND inserts, CTAS, parallel DML, LOB NOCACHE). Check write latency in File IO Stats.",
            Actions = ["Check write latency of the target data files.", "Review batch jobs that run during this window."],
            Docs = [D.WaitEvents, D.IoDesign]
        },
        new()
        {
            Pattern = "log file sync", Friendly = "Waiting for COMMIT to be saved", HealthyAvgMs = 5,
            Plain = "Each time an application saves (commits) a transaction, it waits until the change is safely written to the redo log on disk. These waits are that confirmation time.",
            Why = "Every user action that saves data pays this cost. Slow commits directly slow down order entry, payments and other transactional work. Applications that commit after every row multiply the cost.",
            Technical = "Foreground wait for LGWR to post after writing redo. Compare 'log file sync' average with background 'log file parallel write': if parallel write is also slow, the redo storage is slow; if parallel write is fast but sync is slow, look at commit rate, CPU starvation of LGWR, or redo log switches. High 'user commits' per second indicates excessive committing.",
            Actions =
            [
                "Check commit frequency: batch jobs should commit every N rows, not every row.",
                "Place online redo logs on the fastest, low-latency storage. Do not put them on RAID-5 or share devices with heavy data file I/O.",
                "Compare with 'log file parallel write' in Background Wait Events to separate storage latency from CPU/scheduling delays.",
                "Make sure LGWR is not starved of CPU: check Host CPU and run queue."
            ],
            Sql =
            [
                new("Commit rate and redo write latency", """
                SELECT name, value
                FROM   v$sysstat
                WHERE  name IN ('user commits', 'user rollbacks', 'redo synch writes',
                                'redo synch time', 'redo size', 'redo writes');

                SELECT event, total_waits, ROUND(time_waited_micro / NULLIF(total_waits, 0) / 1000, 2) AS avg_ms
                FROM   v$system_event
                WHERE  event IN ('log file sync', 'log file parallel write');
                """),
                EventHistogram
            ],
            Docs = [D.WaitEvents, D.RedoLog, D.IoDesign, D.DesignForPerformance]
        },
        new()
        {
            Pattern = "log file parallel write", Friendly = "Redo log writer writing to disk", HealthyAvgMs = 5,
            Plain = "The background process that records every change (the log writer) is writing to the redo log files.",
            Why = "This write speed sets the lower limit for how fast any transaction can commit.",
            Technical = "LGWR I/O latency to online redo log members. Averages above a few ms indicate slow redo storage or contention with other I/O. Multiple members per group are written in parallel; the slowest device dominates.",
            Actions = ["Move redo logs to dedicated low-latency storage.", "Check OS I/O statistics (iostat) on the redo devices.", "Avoid host-based mirroring overhead where storage already mirrors."],
            Sql = [EventHistogram],
            Docs = [D.WaitEvents, D.RedoLog, D.IoDesign]
        },
        new()
        {
            Pattern = "log file switch", Prefix = true, Friendly = "Waiting for the redo log to switch",
            Plain = "The database filled its current redo log file and had to wait before it could reuse the next one.",
            Why = "While this wait lasts, no session can make any change. All write activity stops briefly.",
            Technical = "'checkpoint incomplete': DBWR has not finished writing the blocks protected by the next log, so logs are too small or too few. 'archiving needed': ARCn cannot keep up, or the archive destination is full or slow. 'private strand flush incomplete' is usually benign.",
            Actions =
            [
                "Size the online redo logs so that switches occur roughly every 15-20 minutes at peak (Oracle guideline), and add more log groups.",
                "For 'archiving needed': check archive destination space and speed, and increase LOG_ARCHIVE_MAX_PROCESSES.",
                "For 'checkpoint incomplete': add log groups and check DBWR write throughput."
            ],
            Sql =
            [
                new("Current redo log configuration", "SELECT group#, thread#, ROUND(bytes/1024/1024) AS size_mb, members, status FROM v$log ORDER BY thread#, group#;"),
                new("Add larger redo log groups (example: 4 GB)", """
                -- Add new, larger groups (use your ASM disk group or path); then drop the old small groups once INACTIVE
                ALTER DATABASE ADD LOGFILE THREAD 1 GROUP 11 ('+REDO') SIZE 4G;
                ALTER DATABASE ADD LOGFILE THREAD 1 GROUP 12 ('+REDO') SIZE 4G;
                ALTER DATABASE ADD LOGFILE THREAD 1 GROUP 13 ('+REDO') SIZE 4G;
                ALTER SYSTEM SWITCH LOGFILE;
                ALTER SYSTEM CHECKPOINT;
                -- ALTER DATABASE DROP LOGFILE GROUP 1;   -- only when its STATUS is INACTIVE
                """, "Plan this change; dropping a log group that is CURRENT or ACTIVE is not allowed.")
            ],
            Docs = [D.RedoLog, D.ConfiguringDb, D.WaitEvents]
        },
        new()
        {
            Pattern = "log buffer space", Friendly = "Waiting for room in the redo buffer",
            Plain = "Sessions generate change records faster than they can be written to disk, so they wait for space in the in-memory redo buffer.",
            Why = "Write-heavy activity stalls until the log writer catches up.",
            Technical = "Redo generation exceeds LGWR throughput. Remedies: faster redo storage, larger LOG_BUFFER, reduce redo (NOLOGGING for reloadable bulk loads, avoid unnecessary updates).",
            Actions = ["Put redo logs on faster storage.", "Consider increasing LOG_BUFFER (requires restart).", "Reduce redo volume in batch jobs where recoverability allows."],
            Docs = [D.WaitEvents, D.Parameter("LOG_BUFFER"), D.RedoLog]
        },
        new()
        {
            Pattern = "buffer busy waits", Friendly = "Many sessions fighting over the same data block",
            Plain = "Several sessions want the same block of data in memory at the same moment, and they have to take turns. Picture many cashiers sharing a single pen.",
            Why = "This is a contention problem: adding users makes it worse, not better. It typically hits busy tables that receive inserts from many sessions at once, or 'hot' index entries.",
            Technical = "A session waits because another session is reading the block into the cache or modifying it. Check 'Buffer Wait Statistics' for the block class (data block, segment header, undo header) and 'Segments by Buffer Busy Waits' for the objects. Data blocks on insert-heavy tables point to space-management (freelist) contention in non-ASSM tablespaces or to right-growing index hot spots (sequence-based keys); data blocks that are both read and modified by many sessions also show here when readers must build read-consistent copies.",
            Actions =
            [
                "Identify the hot segments in 'Segments by Buffer Busy Waits'.",
                "For indexes on sequence or timestamp keys with concurrent inserts, consider a reverse-key index or hash-partitioning the index, and increase the sequence CACHE.",
                "For hot tables, ensure ASSM tablespaces are used (SEGMENT SPACE MANAGEMENT AUTO), and consider hash partitioning to spread inserts.",
                "For undo header or undo block classes, verify automatic undo management and undo tablespace sizing."
            ],
            Sql =
            [
                new("Hot segments for buffer busy waits", """
                SELECT *
                FROM  (SELECT owner, object_name, subobject_name, object_type, value AS buffer_busy_waits
                       FROM   v$segment_statistics
                       WHERE  statistic_name = 'buffer busy waits'
                       ORDER  BY value DESC)
                WHERE ROWNUM <= 10;
                """),
                new("Spread a hot right-growing index (choose one approach)", """
                -- Option A: hash-partition the index (Enterprise Edition + Partitioning option)
                CREATE INDEX owner.idx_name_hp ON owner.table_name (key_col)
                  GLOBAL PARTITION BY HASH (key_col) PARTITIONS 16 ONLINE;
                -- Option B: reverse key index (range scans on key_col can no longer use it)
                ALTER INDEX owner.idx_name REBUILD REVERSE ONLINE;
                -- Feeding sequence: increase cache
                ALTER SEQUENCE owner.seq_name CACHE 1000;
                """, "Test in a non-production environment first; reverse-key indexes disable index range scans. Not for index-organized-table primary keys (SYS_IOT_TOP_*) or Oracle E-Business Suite seeded objects.")
            ],
            Docs = [D.WaitEvents, D.InstanceTuning, D.Indexes]
        },
        new()
        {
            Pattern = "read by other session", Friendly = "Waiting while another session reads the same block from disk",
            Plain = "Two or more sessions need the same data, and one of them is already fetching it from disk. The others wait for it to arrive.",
            Why = "This usually means many users are running the same expensive query at the same time.",
            Technical = "Concurrent physical reads of the same block (formerly part of buffer busy waits). Tune the SQL that reads the hot segments and check I/O latency.",
            Actions = ["Find the SQL and segments involved and reduce their physical reads.", "Check storage latency."],
            Sql = [AshByEvent],
            Docs = [D.WaitEvents, D.BufferCache]
        },
        new()
        {
            Pattern = "enq: TX - row lock contention", Friendly = "Waiting for another user's locked row",
            Plain = "One user (or program) changed a row and hasn't saved or cancelled yet. Others who want to change the same row must wait. This is like waiting for someone to finish editing a shared document.",
            Why = "This is an application design or behaviour issue. Users see screens that 'hang' until the other transaction finishes. Long-running or forgotten transactions can block many people.",
            Technical = "TX enqueue in mode 6: a session is waiting for a row locked by another uncommitted transaction. Look at 'Segments by Row Lock Waits' and ASH blocking_session. Common causes: long transactions, missing commits, SELECT ... FOR UPDATE held too long, concurrent updates of the same 'counter' rows, or unindexed foreign keys (the latter usually shows as TM contention).",
            Actions =
            [
                "Identify the blocking sessions and the SQL holding the locks (see SQL below).",
                "Work with the application team to shorten transactions and commit promptly.",
                "Avoid 'hot' rows that every session updates (counters, status rows). Use sequences or partition the work.",
                "Make sure batch jobs do not run concurrently on the same rows."
            ],
            Sql =
            [
                new("Who is blocking whom right now", """
                SELECT s.sid, s.serial#, s.username, s.program, s.sql_id, s.event,
                       s.blocking_session, s.seconds_in_wait,
                       o.owner || '.' || o.object_name AS locked_object
                FROM   v$session s
                       LEFT JOIN dba_objects o ON o.object_id = s.row_wait_obj#
                WHERE  s.blocking_session IS NOT NULL
                ORDER  BY s.seconds_in_wait DESC;
                """),
                new("Historical blockers in the AWR window (ASH)", """
                SELECT *
                FROM  (SELECT ash.blocking_session, ash.blocking_session_serial#, ash.sql_id,
                              o.owner || '.' || o.object_name AS object_name, COUNT(*) * 10 AS approx_seconds
                       FROM   dba_hist_active_sess_history ash
                              LEFT JOIN dba_objects o ON o.object_id = ash.current_obj#
                       WHERE  ash.event = 'enq: TX - row lock contention'
                       AND    ash.snap_id BETWEEN &begin_snap AND &end_snap
                       GROUP  BY ash.blocking_session, ash.blocking_session_serial#, ash.sql_id, o.owner, o.object_name
                       ORDER  BY approx_seconds DESC)
                WHERE ROWNUM <= 20;
                """, "Requires the Diagnostics Pack license.")
            ],
            Docs = [D.Concurrency, D.WaitEvents, D.DesignForPerformance]
        },
        new()
        {
            Pattern = "enq: TX - index contention", Friendly = "Contention while splitting index blocks",
            Plain = "Many sessions are inserting into the same part of an index at once (typically the 'newest' end of an index on an increasing number or date), and they queue while the index reorganises itself.",
            Why = "Insert throughput stops scaling. The more concurrent users, the worse it gets.",
            Technical = "Waiting for an index block split by another transaction. Classic right-hand index growth on sequence/timestamp keys. Remedies: hash-partitioned global index, reverse key index, larger sequence cache (with RAC ordering considerations), or fewer/redundant indexes.",
            Actions =
            [
                "Identify the index from 'Segments by Buffer Busy Waits' / ASH current_obj#.",
                "Hash-partition the index or use a reverse-key index (if range scans on the key are not needed).",
                "Increase the CACHE of the sequence that feeds the key.",
                "Drop unused indexes on the table to reduce maintenance."
            ],
            Sql = [AshByEvent],
            Docs = [D.Indexes, D.WaitEvents]
        },
        new()
        {
            Pattern = "enq: TX - allocate ITL entry", Friendly = "No free 'transaction slot' in a data block",
            Plain = "Each data block has a small number of 'slots' that track who is changing it. Too many sessions changed the same block at once and ran out of slots, so they had to wait.",
            Why = "Concurrent updates to the same table or index blocks stall even when they touch different rows.",
            Technical = "Interested Transaction List exhausted: more concurrent transactions touch one block than it has ITL slots (INITRANS) and the block has no free space (PCTFREE) to add slots; each slot costs 24 bytes per block. See 'Segments by ITL Waits' (often one segment, frequently an index or a hot partition). Remedy: increase INITRANS (typically 8-20, based on peak concurrent transactions per block) and PCTFREE, and rebuild/move the segment, because the new value applies only to newly formatted blocks.",
            Actions =
            [
                "Increase INITRANS on the segments listed in 'Segments by ITL Waits' (SQL below is generated for the hottest one).",
                "Rebuild or move the segment so that existing blocks pick up the new INITRANS value.",
                "Consider a higher PCTFREE for heavily updated tables."
            ],
            Sql =
            [
                new("Raise INITRANS and re-organise the segment", """
                -- Table (MOVE ONLINE exists from 12.2; on earlier releases drop the ONLINE keyword and rebuild the indexes afterwards)
                ALTER TABLE owner.table_name INITRANS 16 PCTFREE 20;
                ALTER TABLE owner.table_name MOVE ONLINE;
                -- Partition (MOVE PARTITION ... ONLINE also needs 12.2)
                ALTER TABLE owner.table_name MODIFY PARTITION part_name INITRANS 16;
                ALTER TABLE owner.table_name MOVE PARTITION part_name ONLINE UPDATE INDEXES;
                -- Index
                ALTER INDEX owner.index_name REBUILD INITRANS 16 ONLINE;
                """, "Run during a quiet period; moves generate redo and need free space.")
            ],
            Docs = [D.SchemaSpace, D.WaitEvents]
        },
        new()
        {
            Pattern = "enq: UL - contention", Friendly = "Waiting for an application-defined lock",
            Plain = "Sessions are waiting for a lock that the application itself requested by name (through the DBMS_LOCK package), not a row or table lock. It works like a 'reserved' sign that only one program at a time may hold.",
            Why = "The wait is by design: some program holds the named lock for a long time, or many programs queue for the same one, so batch jobs or screens that need it stand still. Adding hardware does not help.",
            Technical = "UL enqueue = user lock taken with DBMS_LOCK.REQUEST (E-Business Suite and other packaged applications use it for serialising concurrent programs, workflow or session cleanup). Waits are application-level serialisation; find the lock name and the holder in ASH (blocking_session) and in the application code.",
            Actions =
            [
                "Identify which program requests the lock and how long it holds it (ASH blocking_session for this event, then the code calling DBMS_LOCK).",
                "Shorten the section of code that runs while the lock is held, or reduce how many concurrent workers contend for the same lock name.",
                "Ask the application vendor whether a patch or configuration option reduces this serialisation."
            ],
            Sql = [AshByEvent],
            Docs = [D.Concurrency, D.WaitEvents, D.DbmsLock]
        },
        new()
        {
            Pattern = "undo segment extension", Friendly = "Waiting for undo space to grow",
            Plain = "Sessions had to wait while the database made the undo storage bigger (or reorganised it) before a change could continue.",
            Why = "Every such wait pauses the transaction that needed room for its before-images. With many large transactions at once the pauses add up.",
            Technical = "Undo tablespace extension or segment extent allocation under heavy, bursty undo generation (large batch transactions, undo data file growing through AUTOEXTEND in small steps). Check Undo Segment Summary, the undo tablespace free space and the AUTOEXTEND increment.",
            Actions =
            [
                "Pre-size the undo tablespace for the peak batch load instead of relying on repeated small autoextends.",
                "If AUTOEXTEND is used, make the increment larger so growth happens less often.",
                "Spread large batch transactions in time or commit in smaller units."
            ],
            Docs = [D.Undo, D.WaitEvents]
        },
        new()
        {
            Pattern = "enq: TM - contention", Friendly = "Table-level lock contention",
            Plain = "Sessions are waiting for a lock on a whole table, often because a related (child) table's foreign key has no index.",
            Why = "A single delete or update on a parent table can block everyone working on the child table.",
            Technical = "TM enqueue waits. Most often unindexed foreign keys: DML on the parent requires a share lock on the child table. Also explicit LOCK TABLE or DDL.",
            Actions = ["Index foreign-key columns on child tables (see SQL).", "Avoid explicit LOCK TABLE in application code."],
            Sql =
            [
                new("Find unindexed foreign keys", """
                SELECT c.owner, c.table_name, c.constraint_name, cc.column_name
                FROM   dba_constraints c
                       JOIN dba_cons_columns cc ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name
                WHERE  c.constraint_type = 'R'
                AND    c.owner NOT IN ('SYS','SYSTEM')
                AND    NOT EXISTS (SELECT 1 FROM dba_ind_columns ic
                                   WHERE ic.table_owner = cc.owner AND ic.table_name = cc.table_name
                                   AND   ic.column_name = cc.column_name AND ic.column_position = cc.position)
                ORDER  BY c.owner, c.table_name;
                """)
            ],
            Docs = [D.Concurrency, D.Indexes]
        },
        new()
        {
            Pattern = "enq: HW - contention", Friendly = "Waiting to extend a table's storage",
            Plain = "Many sessions are inserting into the same table and need it to grow, so they queue to extend its storage.",
            Why = "Bulk inserts and LOB-heavy workloads stall while space is allocated.",
            Technical = "High-water mark enqueue: concurrent space allocation above the HWM. Common with LOB segments and heavy concurrent inserts. Remedies: preallocate extents, use larger uniform extents, SecureFiles LOBs, partition the segment.",
            Actions = ["Preallocate space (ALTER TABLE ... ALLOCATE EXTENT).", "For LOBs, use SecureFiles.", "Spread inserts across partitions."],
            Docs = [D.SchemaSpace, D.WaitEvents]
        },
        new()
        {
            Pattern = "enq: SQ - contention", Friendly = "Waiting for a sequence number",
            Plain = "Sessions are queuing for new numbers from a database sequence because it hands them out in small batches.",
            Why = "Inserts that need new IDs are throttled by the sequence.",
            Technical = "Sequence cache refill contention. Increase the sequence CACHE (default 20) for high-insert sequences; avoid ORDER in RAC unless required.",
            Actions = ["Increase CACHE on hot sequences (e.g. 1000).", "In RAC, use NOORDER unless strict ordering is required."],
            Sql = [new("Sequences with small cache", "SELECT sequence_owner, sequence_name, cache_size, order_flag FROM dba_sequences WHERE cache_size < 100 AND sequence_owner NOT IN ('SYS','SYSTEM') ORDER BY 1, 2;")],
            Docs = [D.WaitEvents]
        },
        new()
        {
            Pattern = "enq: US - contention", Friendly = "Undo segment management contention",
            Plain = "Sessions are waiting while the database brings undo (rollback) storage online or offline.",
            Why = "Bursty transaction volume causes short stalls while undo space is reorganised.",
            Technical = "Undo segment (US) enqueue: online/offline of undo segments under automatic undo management, often with very bursty concurrency or undo tablespace pressure.",
            Actions = ["Check undo tablespace sizing and 'Undo Segment Summary'.", "Ensure the undo tablespace has enough space so segments are not repeatedly offlined/onlined."],
            Docs = [D.Undo, D.WaitEvents]
        },
        new()
        {
            Pattern = "enq: CF - contention", Friendly = "Control file lock contention",
            Plain = "Processes are queuing to update the database's control file (its 'table of contents').",
            Why = "Frequent log switches, checkpoints or NOLOGGING operations can cause this.",
            Technical = "CF enqueue: control file transaction serialization. Check log switch frequency, NOLOGGING direct loads (unrecoverable SCN updates), and RMAN activity.",
            Actions = ["Reduce log switch frequency (bigger redo logs).", "Review RMAN and NOLOGGING operations during the window."],
            Docs = [D.WaitEvents, D.RedoLog]
        },
        new()
        {
            Pattern = "latch: cache buffers chains", Friendly = "Contention on hot data in memory",
            Plain = "Many sessions are repeatedly reading the same few blocks in memory at the same time and have to take turns.",
            Why = "This is usually caused by inefficient SQL that re-reads the same data millions of times, such as nested loops over a hot index. It burns CPU and limits scalability.",
            Technical = "Cache buffers chains latch protects buffer hash chains. Contention means hot blocks or very high logical reads. Tune the SQL with the highest buffer gets ('SQL ordered by Gets'). Check 'Latch Sleep Breakdown' and 'Segments by Logical Reads'.",
            Actions =
            [
                "Tune the statements with the most buffer gets; this almost always resolves the latch contention.",
                "Identify hot segments ('Segments by Logical Reads') and look for hot lookup tables or index root blocks.",
                "Consider hash partitioning for hot indexes and smaller block density (higher PCTFREE) for hot small tables."
            ],
            Sql =
            [
                new("Hottest cache buffers chains child latches", """
                SELECT *
                FROM  (SELECT addr, gets, misses, sleeps
                       FROM   v$latch_children
                       WHERE  name = 'cache buffers chains'
                       ORDER  BY sleeps DESC)
                WHERE ROWNUM <= 10;
                """)
            ],
            Docs = [D.InstanceTuning, D.WaitEvents]
        },
        new()
        {
            Pattern = "latch: shared pool", Friendly = "Contention in the shared SQL memory",
            Plain = "Sessions are competing to allocate memory for new SQL statements. Usually the application builds many unique SQL statements instead of reusing them.",
            Why = "Parsing new statements is expensive and serialised; it limits throughput and wastes CPU.",
            Technical = "Shared pool latch contention from heavy hard parsing (literal SQL), shared pool fragmentation, or undersized shared pool. Check Hard parses/s in Load Profile, Soft Parse %, and the Shared Pool Advisory.",
            Actions = ["Use bind variables in the application.", "Review Shared Pool Advisory for sizing.", "As a temporary workaround consider CURSOR_SHARING=FORCE (test first)."],
            Docs = [D.SharedPool, D.CursorSharing]
        },
        new()
        {
            Pattern = "library cache: mutex X", Friendly = "Contention on shared SQL definitions",
            Plain = "Many sessions are running the same statement or package at a very high rate and contend for its shared definition in memory.",
            Why = "Very high execution rates of a few statements or PL/SQL objects can serialise on memory structures, wasting CPU.",
            Technical = "Library cache mutex contention: hot objects/cursors (very high execution rates), high version counts, or invalidations. Check 'Mutex Sleep Summary', 'SQL ordered by Executions' and 'SQL ordered by Version Count'.",
            Actions = ["Reduce the execution rate of the hottest statements (cache results, array processing).", "Investigate high version counts.", "For hot PL/SQL objects consider DBMS_SHARED_POOL.MARKHOT."],
            Docs = [D.SharedPool, D.WaitEvents]
        },
        new()
        {
            Pattern = "library cache lock", Friendly = "Waiting for a lock on a database object definition",
            Plain = "Sessions are waiting because a table, package or view definition is being changed or compiled while others try to use it.",
            Why = "Running DDL or compiling code during business hours can freeze users of that object.",
            Technical = "Library cache lock: DDL/compilation versus use of the object (and, in some versions, bursts of failed logons).",
            Actions = ["Avoid DDL, gathering statistics with invalidation, or package recompiles during peak.", "If no DDL explains it, check the audit trail for bursts of failed logons."],
            Docs = [D.SharedPool, D.WaitEvents]
        },
        new()
        {
            Pattern = "cursor: pin S wait on X", Friendly = "Waiting for a statement to finish compiling",
            Plain = "Sessions want to run a statement that another session is still compiling (parsing), so they wait.",
            Why = "Indicates heavy or slow parsing of popular statements. Can cause sudden pile-ups.",
            Technical = "A session needs the cursor pin in share mode while another holds it exclusive (hard parse/build). Look for high version counts, invalidations, and slow hard parses.",
            Actions = ["Check 'SQL ordered by Version Count' and V$SQL_SHARED_CURSOR for the reasons cursors are not shared.", "Avoid invalidating popular cursors (statistics gathering with NO_INVALIDATE=>FALSE, DDL) during peak."],
            Sql =
            [
                new("Statements with many child cursors", "SELECT sql_id, version_count, executions, SUBSTR(sql_text, 1, 80) AS sql_text FROM v$sqlarea WHERE version_count > 50 ORDER BY version_count DESC;")
            ],
            Docs = [D.SharedPool, D.CursorSharing]
        },
        new()
        {
            Pattern = "cursor: mutex", Prefix = true, Friendly = "Contention on cursor memory",
            Plain = "Sessions are competing for the same shared statement in memory.",
            Why = "Wastes CPU and limits scalability for very hot statements.",
            Technical = "Cursor mutex contention, often related to high version counts or very hot cursors.",
            Actions = ["Investigate version counts and execution rates of the hottest SQL."],
            Docs = [D.SharedPool]
        },
        new()
        {
            Pattern = "row cache lock", Friendly = "Contention on the data dictionary cache",
            Plain = "Sessions are waiting to read or update the database's internal catalog, which describes users, tables and sequences.",
            Why = "Usually caused by sequences with small caches, frequent DDL, or many logons.",
            Technical = "Dictionary cache (row cache) enqueue. Check 'Dictionary Cache Stats' for the cache with the most misses/mod reqs (dc_sequences means sequence cache too small; dc_users or dc_objects mean DDL or logons).",
            Actions = ["Increase sequence caches if dc_sequences is busy.", "Reduce DDL and logon storms."],
            Docs = [D.SharedPool, D.WaitEvents]
        },
        new()
        {
            Pattern = "resmgr:cpu quantum", Friendly = "Throttled by Resource Manager",
            Plain = "The database's Resource Manager deliberately paused sessions to share the CPU according to a plan.",
            Why = "Either the server is CPU-saturated and Resource Manager is doing its job, or a plan (often the automatic maintenance-window plan) is limiting work unexpectedly.",
            Technical = "Sessions waiting for their CPU quantum under an active resource plan. Check which plan was active (DEFAULT_MAINTENANCE_PLAN during maintenance windows is a common surprise) and whether CPU is actually saturated (Host CPU).",
            Actions =
            [
                "Check which resource plan was active during the snapshot window.",
                "If the server was not CPU-bound, review the plan's CPU limits (utilization_limit) for the affected consumer group.",
                "If the server was CPU-bound, reduce CPU demand by tuning the top SQL."
            ],
            Sql =
            [
                new("Active plan and consumer group CPU waits", """
                SELECT name, is_top_plan, cpu_managed FROM v$rsrc_plan;
                SELECT name, consumed_cpu_time, cpu_waits, cpu_wait_time FROM v$rsrc_consumer_group ORDER BY cpu_wait_time DESC;
                SELECT window_name, resource_plan, enabled FROM dba_scheduler_windows;
                """)
            ],
            Docs = [D.ResourceManager, D.OsResources]
        },
        new()
        {
            Pattern = "free buffer waits", Friendly = "No free memory buffers to read data into",
            Plain = "Sessions want to read data into memory but there is no free space, because changed data has not been written back to disk fast enough.",
            Why = "Indicates the database writer (DBWR) cannot keep up, or the cache is too small for the workload.",
            Technical = "DBWR throughput insufficient or buffer cache too small. Check write latency on data files, DB_WRITER_PROCESSES, asynchronous I/O (FILESYSTEMIO_OPTIONS=SETALL on file systems), and the Buffer Pool Advisory.",
            Actions = ["Verify asynchronous I/O is enabled.", "Check data file write latency.", "Consider more DBWR processes or a larger buffer cache."],
            Docs = [D.BufferCache, D.IoDesign, D.Parameter("DB_WRITER_PROCESSES")]
        },
        new()
        {
            Pattern = "write complete waits", Friendly = "Waiting for a block to finish being written",
            Plain = "Sessions want to change a block that is currently being written to disk.",
            Why = "Indicates slow writes by the database writer.",
            Technical = "DBWR write latency. Check data file write performance and asynchronous I/O.",
            Actions = ["Check storage write latency and asynchronous I/O settings."],
            Docs = [D.BufferCache, D.IoDesign]
        },
        new()
        {
            Pattern = "control file sequential read", Friendly = "Reading the control file",
            Plain = "Processes are reading the database control file, which happens for backups, monitoring queries and log switches.",
            Why = "Usually small. If high, monitoring tools or backups may be polling too often.",
            Technical = "Control file reads (V$ views over control file data, RMAN, ARCH). High counts often come from monitoring queries on V$ARCHIVED_LOG/V$BACKUP* or RMAN catalog maintenance.",
            Actions = ["Check monitoring agents and RMAN schedules.", "Purge old records in the control file (CONTROL_FILE_RECORD_KEEP_TIME, RMAN catalog)."],
            Docs = [D.WaitEvents]
        },
        new()
        {
            Pattern = "control file parallel write", Friendly = "Writing to the control file",
            Plain = "The database is updating all copies of its control file.",
            Why = "High values suggest very frequent log switches or checkpoints, or slow storage.",
            Technical = "Writes to all control file copies (log switches, checkpoints, NOLOGGING operations). Check log switch rate and storage latency.",
            Actions = ["Reduce log switches with larger redo logs.", "Check storage latency of control file copies."],
            Docs = [D.WaitEvents, D.RedoLog]
        },
        new()
        {
            Pattern = "SQL*Net more data from client", Friendly = "Receiving data from the application",
            Plain = "The database is waiting for the application to send the rest of a large request over the network.",
            Why = "Points to network latency or large statements/LOB uploads.",
            Technical = "Network round trips for large inbound payloads. Consider SDU size, array binds, and network latency.",
            Actions = ["Check network latency between application and database.", "Consider larger SDU/TDU and array processing."],
            Docs = [D.WaitEvents]
        },
        new()
        {
            Pattern = "SQL*Net more data to client", Friendly = "Sending data to the application",
            Plain = "The database is sending large results to the application over the network.",
            Why = "Large results or a slow network extend response times.",
            Technical = "Large result sets or LOBs. Increase fetch array size in the client and SDU; avoid fetching unnecessary columns or rows.",
            Actions = ["Increase the client's fetch/array size.", "Fetch only the columns and rows needed."],
            Docs = [D.WaitEvents]
        },
        new()
        {
            Pattern = "SQL*Net message from dblink", Friendly = "Waiting for a remote database",
            Plain = "Queries are waiting for a remote database (over a database link) to respond.",
            Why = "Performance depends on the other database and the network in between.",
            Technical = "Distributed queries via DB links. Tune the remote SQL (check the remote AWR), reduce round trips, and consider DRIVING_SITE hints or materialized views.",
            Actions = ["Analyze the SQL executed through the database link on the remote side.", "Reduce row-by-row remote calls."],
            Docs = [D.WaitEvents]
        },
        new()
        {
            Pattern = "gc ", Prefix = true, Friendly = "RAC cluster cache transfer",
            Plain = "In a clustered (RAC) database, a node had to fetch a block of data from another node's memory over the cluster network.",
            Why = "Cluster traffic adds latency to every affected operation. Heavy traffic means the same data is being modified on several nodes.",
            Technical = "Global cache waits. Check the interconnect latency (rule of thumb: gc cr/current block receive times of about 1 ms or less on a healthy 10 GbE/InfiniBand interconnect; 2 ms or more deserves investigation), and look for hot blocks across instances ('gc buffer busy'). Remedies: application/service partitioning by instance, sequence caching (NOORDER), and hash partitioning of hot indexes.",
            Actions = ["Check private interconnect health and latency.", "Use services to route related work to the same instance.", "Address hot blocks (sequences, right-growing indexes)."],
            Docs = [D.Rac, D.WaitEvents]
        },
        new()
        {
            Pattern = "PX ", Prefix = true, Friendly = "Parallel query coordination",
            Plain = "Parallel query processes are waiting on each other while working on a large operation together.",
            Why = "Usually a symptom of parallel execution. Excess indicates too much parallelism or skewed work distribution.",
            Technical = "Parallel execution message waits (mostly idle-ish). Review DOP settings and data skew.",
            Actions = ["Review the degree of parallelism used by the statements.", "Check for data skew between parallel slaves."],
            Docs = [D.WaitEvents]
        },
        new()
        {
            Pattern = "latch free", Friendly = "Generic latch contention",
            Plain = "Sessions are competing for small internal memory locks.",
            Why = "Wastes CPU and limits scalability.",
            Technical = "Check 'Latch Sleep Breakdown' to identify which latch is affected.",
            Actions = ["Identify the specific latch in 'Latch Sleep Breakdown' and address its root cause."],
            Docs = [D.InstanceTuning]
        },
        new()
        {
            Pattern = "latch: redo allocation", Friendly = "Contention for redo buffer space",
            Plain = "Many sessions generating change records at the same moment are competing for space in the redo buffer.",
            Why = "Limits transaction throughput on write-heavy systems.",
            Technical = "Redo allocation latch contention due to very high redo generation rate from many concurrent sessions. Reduce redo volume, commit less often per row, and ensure LGWR writes are fast.",
            Actions = ["Reduce unnecessary DML/redo (e.g. avoid updating unchanged columns).", "Ensure redo storage is fast."],
            Docs = [D.RedoLog, D.InstanceTuning]
        },
        new()
        {
            Pattern = "latch: In memory undo latch", Friendly = "Contention on in-memory undo",
            Plain = "Many concurrent transactions are competing for in-memory undo structures.",
            Why = "Typical of very high concurrent DML; limits scalability.",
            Technical = "IMU latch contention under high transaction concurrency. Usually resolved by reducing contention on the same objects / transaction concurrency.",
            Actions = ["Review concurrent DML hot spots (same tables/blocks)."],
            Docs = [D.Undo, D.InstanceTuning]
        },
    ];

    public static WaitEventInfo? Find(string eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return null;
        var e = eventName.Trim();
        var exact = All.FirstOrDefault(k => !k.Prefix && string.Equals(k.Pattern, e, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        // Longest prefix wins ("direct path read temp" before "direct path read").
        return All.Where(k => e.StartsWith(k.Pattern, StringComparison.OrdinalIgnoreCase))
                  .OrderByDescending(k => k.Pattern.Length)
                  .FirstOrDefault();
    }

    /// <summary>Plain-English description of the Oracle wait classes (Database Reference: Classes of Wait Events).</summary>
    public static (string Plain, string Advice) WaitClass(string waitClass) => waitClass.Trim().ToLowerInvariant() switch
    {
        "user i/o" => ("Waiting for data to be read from disk by user sessions.", "Tune SQL with the most physical reads and check storage latency."),
        "system i/o" => ("Background processes writing logs and data files to disk.", "Check redo and data file write latency."),
        "concurrency" => ("Sessions competing for the same internal resources (blocks, latches, mutexes).", "Find the hot objects and SQL; this usually needs design changes."),
        "application" => ("Waits caused by application behaviour, mostly row locks held by other users.", "Shorten transactions and fix locking patterns in the application."),
        "commit" => ("Waiting for COMMIT to be durably written.", "Reduce commit frequency and use fast redo storage."),
        "configuration" => ("Waits caused by database configuration (log sizes, ITL slots, space).", "Adjust the related configuration (redo log size, INITRANS, sequence cache)."),
        "network" => ("Time sending/receiving data to and from applications or other databases.", "Check network latency, fetch sizes and database links."),
        "cluster" => ("RAC nodes exchanging data blocks across the interconnect.", "Check interconnect latency and application partitioning across nodes."),
        "scheduler" => ("Sessions held back by Resource Manager to share CPU.", "Check the active resource plan and CPU capacity."),
        "administrative" => ("Waits caused by DBA commands (e.g. index rebuilds).", "Schedule maintenance outside peak hours."),
        "queueing" => ("Waits in Advanced Queuing / Streams.", "Check queue consumers and propagation."),
        "other" => ("Miscellaneous internal waits.", "Check the individual events listed in Foreground Wait Events."),
        "db cpu" => ("Time spent working on the CPU.", "Tune the SQL using the most CPU and logical reads."),
        _ => ("Time spent waiting in this class.", "Review the individual events of this class.")
    };
}
