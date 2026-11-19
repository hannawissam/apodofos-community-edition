# Coverage matrix

Which part of the analysis consumes each table/section the parsers produce. Source of truth:
`src/QuietLoad.Core/Analysis/CoverageMap.cs`. `CoverageTests` fails when

- a section produced by a parser (checked on every fixture in `EXAMPLES/`, `tests/QuietLoad.Tests/TestData/` and, when present, the git-ignored `EXAMPLES-local/`) has no entry in the map,
- a **core** section has no real consumer (only the generic table explorer),
- a rule class, chart id or KPI named in the map no longer exists in the source.

Consumer codes: **R** rule class (file under `Analysis/Rules/`), **C** chart id (`ChartBuilder` / `AshChartBuilder`), **K** KPI tile, **M** report-header extractor, **S** helper used by many rules, **X** generic table explorer only (the section is still displayed, charted generically and linked to documentation, but no rule or curated chart reads it).

## Matrix

| Section (title prefix) | Report kinds | Core | Consumers | Note |
|---|---|---|---|---|
| Database Instance Information | AWR, Statspack, ASH |  | M:AwrInfoExtractor, AshInfoExtractor |  |
| Host Information | AWR, Statspack |  | M:AwrInfoExtractor |  |
| Snapshot Information | AWR, Statspack | yes | M:AwrInfoExtractor |  |
| Top ADDM Findings | AWR |  | R:ConfigurationRules, C:addm | ADDM findings are echoed as an Info finding |
| Load Profile | AWR, Statspack | yes | R:LoadAndEfficiencyRules, C:load-profile, K:tps, K:execs, K:lio, K:pio, K:hardparse, K:redo |  |
| Instance Efficiency | AWR, Statspack | yes | R:LoadAndEfficiencyRules, C:efficiency | Flattened by AwrReport.EfficiencyPercentages |
| Top 10 Foreground Events | AWR | yes | R:WaitEventRules, C:top-events, K:dbcpu |  |
| Top 5 Timed | Statspack | yes | R:WaitEventRules, C:top-events | Statspack equivalent of the top foreground events |
| Wait Classes by Total Wait Time | AWR | yes | R:WaitEventRules, C:db-time-breakdown |  |
| Host CPU | AWR, Statspack | yes | R:LoadAndEfficiencyRules, C:host-cpu, K:hostcpu |  |
| Instance CPU | AWR, Statspack |  | R:PlatformRules |  |
| IO Profile | AWR, Statspack |  | R:PlatformRules |  |
| Memory Statistics | AWR, Statspack |  | R:IoAndMemoryRules |  |
| Cache Sizes | AWR, Statspack |  | M:AwrInfoExtractor | Header facts (SGA/PGA sizes) |
| Shared Pool Statistics | AWR, Statspack |  | R:IoAndMemoryRules |  |
| Time Model Statistics | AWR, Statspack | yes | R:LoadAndEfficiencyRules, C:time-model |  |
| Operating System Statistics - Detail | AWR, Statspack |  | R:LoadAndEfficiencyRules | CPU peak detection |
| Operating System Statistics | AWR, Statspack |  | R:PlatformRules, R:IoAndMemoryRules |  |
| Foreground Wait Class | AWR, Statspack |  | C:db-time-breakdown, R:WaitEventRules |  |
| Foreground Wait Events | AWR, Statspack | yes | R:WaitEventRules, C:fg-events |  |
| Background Wait Events | AWR, Statspack |  | R:WaitEventRules, C:bg-events |  |
| Wait Event Histogram Detail | AWR |  | X | Drill-down of the histogram buckets above 64 ms |
| Wait Event Histogram | AWR |  | R:CoverageGapRules, C:wait-histogram | Added: tail-latency rule |
| Service Statistics | AWR |  | C:service-dbtime |  |
| Service Wait Class Stats | AWR |  | C:service-userio-wait | Added chart |
| SQL ordered by Elapsed Time | AWR | yes | R:SqlRules, C:top-sql-elapsed |  |
| SQL ordered by CPU Time | AWR | yes | R:SqlRules, C:top-sql-cpu |  |
| SQL ordered by User I/O Wait Time | AWR |  | R:SqlRules, C:top-sql-io |  |
| SQL ordered by Gets | AWR | yes | R:SqlRules, C:top-sql-gets |  |
| SQL ordered by Reads | AWR |  | R:SqlRules, C:top-sql-reads |  |
| SQL ordered by Physical Reads (UnOptimized) | AWR |  | C:top-sql-unoptimized | Added chart |
| SQL ordered by Executions | AWR |  | R:SqlRules, C:top-sql-execs |  |
| SQL ordered by Parse Calls | AWR |  | R:SqlRules, C:top-sql-parses |  |
| SQL ordered by Sharable Memory | AWR |  | C:top-sql-sharable | Added chart |
| SQL ordered by Version Count | AWR |  | R:SqlRules |  |
| SQL ordered by Cluster Wait Time | AWR |  | X | RAC only; supporting detail for the Cluster wait class finding |
| Complete List of SQL Text | AWR, ASH | yes | R:SqlRules, S:SqlTexts | SQL text is attached to every SQL finding |
| Key Instance Activity Stats | AWR |  | R:LoadAndEfficiencyRules |  |
| Instance Activity Stats - Absolute Values | AWR |  | X | Point-in-time counters (open cursors, logons current) |
| Instance Activity Stats - Thread Activity | AWR |  | R:ConfigurationRules | Log switch rate |
| Instance Activity Stats | AWR, Statspack | yes | R:LoadAndEfficiencyRules, R:PlatformRules |  |
| Other Instance Activity Stats | AWR |  | X | PDB-level supplement of Instance Activity Stats |
| IOStat by Function/Filetype summary | AWR |  | X | Drill-down of the two IOStat summaries below |
| IOStat by Function summary | AWR |  | R:IoFunctionAndCheckpointRules, C:iostat-function |  |
| IOStat by Filetype summary | AWR |  | R:IoAndMemoryRules, C:iostat-filetype |  |
| Tablespace IO Stats | AWR, Statspack |  | R:IoAndMemoryRules, C:tablespace-io |  |
| File IO Stats | AWR, Statspack |  | R:CoverageGapRules, C:file-io-latency | Added: slow data files rule and chart |
| Buffer Pool Statistics | AWR, Statspack |  | R:IoFunctionAndCheckpointRules |  |
| Checkpoint Activity | AWR |  | R:IoFunctionAndCheckpointRules |  |
| Instance Recovery Stats | AWR, Statspack |  | R:ConfigurationRules, R:IoFunctionAndCheckpointRules |  |
| MTTR Advisory | AWR |  | X | Instance Recovery Stats carries the actionable numbers |
| Buffer Pool Advisory | AWR, Statspack |  | R:IoAndMemoryRules, C:advisory-buffer |  |
| PGA Aggr Summary | AWR, Statspack |  | R:IoAndMemoryRules |  |
| PGA Aggr Target Stats | AWR |  | X | Supporting detail of PGA Aggr Summary |
| PGA Aggr Target Histogram | AWR, Statspack |  | R:IoAndMemoryRules |  |
| PGA Memory Advisory | AWR, Statspack |  | R:IoAndMemoryRules, C:advisory-pga |  |
| Shared Pool Advisory | AWR, Statspack |  | R:IoAndMemoryRules, C:advisory-shared-pool |  |
| SGA Target Advisory | AWR, Statspack |  | R:IoAndMemoryRules, C:advisory-sga |  |
| Streams Pool Advisory | AWR |  | X | Streams/GoldenGate only |
| Java Pool Advisory | AWR |  | X | Java in the database only |
| Buffer Wait Statistics | AWR, Statspack |  | R:WaitEventRules, C:buffer-waits |  |
| Enqueue Activity | AWR, Statspack |  | R:WaitEventRules, C:enqueues |  |
| Undo Segment Summary | AWR, Statspack |  | R:ConfigurationRules |  |
| Undo Segment Stats | AWR, Statspack |  | C:undo-blocks |  |
| Latch Activity | AWR, Statspack |  | C:latch-wait-time | Added chart |
| Latch Sleep Breakdown | AWR, Statspack |  | R:WaitEventRules, C:latch-sleeps |  |
| Latch Miss Sources | AWR, Statspack |  | X | Drill-down for the latch sleep findings |
| Mutex Sleep Summary | AWR, Statspack |  | R:WaitEventRules, C:mutex-sleeps |  |
| Segments by Logical Reads | AWR, Statspack |  | R:AshAndHotspotRules, R:WaitEventRules, C:seg-logical-reads |  |
| Segments by Physical Reads | AWR, Statspack |  | R:AshAndHotspotRules, R:WaitEventRules, C:seg-physical-reads |  |
| Segments by Physical Read Requests | AWR, Statspack |  | R:WaitEventRules, C:seg-physical-read-requests |  |
| Segments by Row Lock Waits | AWR, Statspack |  | R:WaitEventRules, C:seg-row-lock-waits |  |
| Segments by ITL Waits | AWR, Statspack |  | R:WaitEventRules, C:seg-itl-waits |  |
| Segments by Buffer Busy Waits | AWR, Statspack |  | R:WaitEventRules, C:seg-buffer-busy-waits |  |
| Segments by Table Scans | AWR, Statspack |  | R:AshAndHotspotRules, R:WaitEventRules, C:seg-table-scans |  |
| Segments by DB Blocks Changes | AWR, Statspack |  | R:AshAndHotspotRules, C:seg-db-blocks-changes |  |
| Segments by Direct Physical | AWR, Statspack |  | R:WaitEventRules, C:seg-direct-physical-reads |  |
| Segments by | AWR, Statspack |  | C:seg-* | Remaining segment tables are charted generically (top objects) |
| Dictionary Cache Stats | AWR, Statspack |  | R:CoverageGapRules, C:dictionary-cache-miss | Added rule and chart |
| Library Cache Activity | AWR, Statspack |  | R:IoAndMemoryRules |  |
| Memory Dynamic Components | AWR, Statspack |  | R:IoAndMemoryRules, C:memory-components |  |
| Process Memory Summary | AWR |  | X | Supporting detail of PGA Aggr Summary |
| SGA Memory Summary | AWR |  | X | Supporting detail of Memory Dynamic Components |
| SGA breakdown difference | AWR |  | X | Supporting detail of Memory Dynamic Components |
| Resource Limit Stats | AWR, Statspack |  | R:ConfigurationRules, C:resource-limits |  |
| Streams | AWR |  | X | Streams/GoldenGate/AQ sections (rare) |
| Buffered | AWR |  | X | Streams/GoldenGate/AQ sections (rare) |
| Persistent Queue | AWR |  | X | Streams/GoldenGate/AQ sections (rare) |
| Rule Set | AWR |  | X | Streams/GoldenGate/AQ sections (rare) |
| Shared Servers | AWR |  | X | Shared server (MTS) statistics; rare in modern installations |
| init.ora Parameters | AWR, Statspack | yes | R:ConfigurationRules, S:InitParameters |  |
| init.ora Multi-Valued Parameters | AWR |  | X | Multi-valued parameter list (exclusion lists, event settings) |
| Parameters modified by | AWR |  | X | PDB parameter overrides; supplement of init.ora Parameters |
| Multi-Valued Parameters modified by | AWR |  | X | PDB parameter overrides; supplement of init.ora Parameters |
| WORKLOAD REPOSITORY PDB report | AWR |  | R:IRule | PDB reports are detected to avoid false findings |
| Top SQL with Top Events | AWR, ASH | yes | R:AshSqlRules, R:AshAndHotspotRules, C:ash-top-sql |  |
| Top SQL with Top Row Sources | AWR, ASH |  | R:AshSqlRules |  |
| Top Sessions running PQs | ASH |  | R:AshEventRules |  |
| Top Sessions | AWR, ASH | yes | R:AshSessionRules, R:AshAndHotspotRules |  |
| Top Blocking Sessions | AWR, ASH |  | R:AshSessionRules, R:AshAndHotspotRules |  |
| Top PL/SQL Procedures | AWR, ASH |  | R:AshSessionRules, R:LoadAndEfficiencyRules |  |
| Top Event P1/P2/P3 Values | AWR, ASH |  | R:AshEventRules, R:WaitEventRules |  |
| Top DB Objects | AWR, ASH |  | R:AshSessionRules, R:WaitEventRules |  |
| Top DB Files | ASH |  | R:AshSessionRules |  |
| Top User Events | ASH | yes | R:AshEventRules, R:AshLoadRules, C:ash-top-events |  |
| Top Background Events | ASH |  | R:AshEventRules, R:AshLoadRules |  |
| Top Events | AWR, ASH | yes | R:AshEventRules, C:ash-top-events |  |
| Top Service/Module | ASH |  | R:AshSessionRules |  |
| Top Client IDs | ASH |  | R:AshSessionRules |  |
| Top SQL Command Types | ASH |  | R:AshSqlRules |  |
| Top Phases of Execution | ASH |  | R:AshSqlRules |  |
| Top Call Types | ASH |  | R:AshSessionRules |  |
| Top Parsing Module/Action | ASH |  | X | Parsing share is covered by 'Top Phases of Execution' |
| Activity Over Time | AWR, ASH | yes | R:AshLoadRules, C:ash-activity |  |
| ASH Report For | ASH |  | M:AshInfoExtractor | Sample window and counts |
| ASH Sample Window | ASH |  | M:AshInfoExtractor | Window, sample count and data source drive the data-quality notes |
| CPU and Memory Configuration | ASH |  | M:AshInfoExtractor | CPU count for sessions-vs-CPU checks |
| Background Idle Wait Events | Statspack |  | X | Idle events are excluded from DB time by design |
| Idle Wait Events | Statspack |  | X | Idle events are excluded from DB time by design |
| Top 5 Idle Events | Statspack |  | X | Idle events are excluded from DB time by design |
| Top 5 Timed Foreground Events | Statspack |  | R:WaitEventRules, C:top-events |  |
| Statspack Warnings | Statspack |  | R:StatspackRules |  |
| SQL Capture Summary | Statspack |  | R:StatspackRules |  |
| Instance Activity Stats (from the same AWR) | Statspack |  | R:LoadAndEfficiencyRules |  |
| A guide to the detail sections | Statspack |  | X | Static explanatory text in the Oracle Statspack guide |
| Response Time Analysis | Statspack |  | X | Static explanatory text |

## Gaps found and filled in this review

| Section | What was added |
|---|---|
| Wait Event Histogram | Rule `CoverageGapRules.LatencyTail`: slow tail (32 ms or more) of log file sync / log file parallel write / db file sequential and scattered read, only for events costing at least 1% of DB time |
| File IO Stats | Rule `CoverageGapRules.SlowDataFiles` (files above 20 ms per read that cost at least 0.5% of DB time) and chart `file-io-latency` |
| Dictionary Cache Stats | Rule `CoverageGapRules.DictionaryCache` (request-weighted miss rate of 5% or more over at least 100k gets) and chart `dictionary-cache-miss` |
| Latch Activity | Chart `latch-wait-time` |
| SQL ordered by Sharable Memory, SQL ordered by Physical Reads (UnOptimized) | Charts `top-sql-sharable`, `top-sql-unoptimized` |
| Service Wait Class Stats | Chart `service-userio-wait` |

## Sections intentionally left to the explorer (X)

These are drill-downs of a section that is already analysed, or features that are rare (Streams / GoldenGate / AQ, shared servers, Java pool). They stay visible and searchable; add a rule when a real report shows a need.

## Limitations

- The matrix proves a section is read by named code, not that every column of it is used.
- ASH sections are consumed per report layout; the 10g layout prints fewer sections (no Top Sessions running PQs, no Top Client IDs), which are then simply absent.
- Statspack sections reuse AWR titles after normalisation (`StatspackNormalizer`), so the AWR rules apply; sections that need snapshot level 7 (segments) are reported as missing, not as failures.
