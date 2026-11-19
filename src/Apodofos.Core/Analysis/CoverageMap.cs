// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Core.Model;

namespace Apodofos.Core.Analysis;

/// <summary>
/// Coverage matrix: for every table/section the parsers produce, which part of the analysis consumes it.
/// Rendered as docs/coverage-matrix.md and enforced by CoverageTests (a "core" section without a real consumer
/// fails the build; so does a parsed section that is not classified here).
/// Consumer codes: R rule class, C chart id, K KPI key, M report header / metadata, S search key only.
/// X = generic table explorer only (the section is still shown, charted generically and linked to documentation).
/// </summary>
public static class CoverageMap
{
    public sealed record Entry(string Title, string Kinds, bool Core, string Consumers, string? Note)
    {
        public bool IsAwr => Kinds.Contains('A');
        public bool IsAsh => Kinds.Contains('H');
        public bool IsStatspack => Kinds.Contains('S');
        public string[] ConsumerList => Consumers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        /// <summary>True when at least one consumer is a rule, chart, KPI or header extractor (not the explorer).</summary>
        public bool HasRealConsumer => ConsumerList.Any(c => !c.StartsWith("X"));
    }

    // Title prefix | kinds (A=AWR, S=Statspack, H=ASH) | core (1/0) | consumers | note
    private const string Table = """
        Database Instance Information|ASH|0|M:AwrInfoExtractor,AshInfoExtractor|
        Host Information|AS|0|M:AwrInfoExtractor|
        Snapshot Information|AS|1|M:AwrInfoExtractor|
        Top ADDM Findings|A|0|R:ConfigurationRules,C:addm|ADDM findings are echoed as an Info finding
        Load Profile|AS|1|R:LoadAndEfficiencyRules,C:load-profile,K:tps,K:execs,K:lio,K:pio,K:hardparse,K:redo|
        Instance Efficiency|AS|1|R:LoadAndEfficiencyRules,C:efficiency|Flattened by AwrReport.EfficiencyPercentages
        Top 10 Foreground Events|A|1|R:WaitEventRules,C:top-events,K:dbcpu|
        Top 5 Timed|S|1|R:WaitEventRules,C:top-events|Statspack equivalent of the top foreground events
        Wait Classes by Total Wait Time|A|1|R:WaitEventRules,C:db-time-breakdown|
        Host CPU|AS|1|R:LoadAndEfficiencyRules,C:host-cpu,K:hostcpu|
        Instance CPU|AS|0|R:PlatformRules|
        IO Profile|AS|0|R:PlatformRules|
        Memory Statistics|AS|0|R:IoAndMemoryRules|
        Cache Sizes|AS|0|M:AwrInfoExtractor|Header facts (SGA/PGA sizes)
        Shared Pool Statistics|AS|0|R:IoAndMemoryRules|
        Time Model Statistics|AS|1|R:LoadAndEfficiencyRules,C:time-model|
        Operating System Statistics - Detail|AS|0|R:LoadAndEfficiencyRules|CPU peak detection
        Operating System Statistics|AS|0|R:PlatformRules,R:IoAndMemoryRules|
        Foreground Wait Class|AS|0|C:db-time-breakdown,R:WaitEventRules|
        Foreground Wait Events|AS|1|R:WaitEventRules,C:fg-events|
        Background Wait Events|AS|0|R:WaitEventRules,C:bg-events|
        Wait Event Histogram Detail|A|0|X|Drill-down of the histogram buckets above 64 ms
        Wait Event Histogram|A|0|R:CoverageGapRules,C:wait-histogram|Added: tail-latency rule
        Service Statistics|A|0|C:service-dbtime|
        Service Wait Class Stats|A|0|C:service-userio-wait|Added chart
        SQL ordered by Elapsed Time|A|1|R:SqlRules,C:top-sql-elapsed|
        SQL ordered by CPU Time|A|1|R:SqlRules,C:top-sql-cpu|
        SQL ordered by User I/O Wait Time|A|0|R:SqlRules,C:top-sql-io|
        SQL ordered by Gets|A|1|R:SqlRules,C:top-sql-gets|
        SQL ordered by Reads|A|0|R:SqlRules,C:top-sql-reads|
        SQL ordered by Physical Reads (UnOptimized)|A|0|C:top-sql-unoptimized|Added chart
        SQL ordered by Executions|A|0|R:SqlRules,C:top-sql-execs|
        SQL ordered by Parse Calls|A|0|R:SqlRules,C:top-sql-parses|
        SQL ordered by Sharable Memory|A|0|C:top-sql-sharable|Added chart
        SQL ordered by Version Count|A|0|R:SqlRules|
        SQL ordered by Cluster Wait Time|A|0|X|RAC only; supporting detail for the Cluster wait class finding
        Complete List of SQL Text|AH|1|R:SqlRules,S:SqlTexts|SQL text is attached to every SQL finding
        Key Instance Activity Stats|A|0|R:LoadAndEfficiencyRules|
        Instance Activity Stats - Absolute Values|A|0|X|Point-in-time counters (open cursors, logons current)
        Instance Activity Stats - Thread Activity|A|0|R:ConfigurationRules|Log switch rate
        Instance Activity Stats|AS|1|R:LoadAndEfficiencyRules,R:PlatformRules|
        Other Instance Activity Stats|A|0|X|PDB-level supplement of Instance Activity Stats
        IOStat by Function/Filetype summary|A|0|X|Drill-down of the two IOStat summaries below
        IOStat by Function summary|A|0|R:IoFunctionAndCheckpointRules,C:iostat-function|
        IOStat by Filetype summary|A|0|R:IoAndMemoryRules,C:iostat-filetype|
        Tablespace IO Stats|AS|0|R:IoAndMemoryRules,C:tablespace-io|
        File IO Stats|AS|0|R:CoverageGapRules,C:file-io-latency|Added: slow data files rule and chart
        Buffer Pool Statistics|AS|0|R:IoFunctionAndCheckpointRules|
        Checkpoint Activity|A|0|R:IoFunctionAndCheckpointRules|
        Instance Recovery Stats|AS|0|R:ConfigurationRules,R:IoFunctionAndCheckpointRules|
        MTTR Advisory|A|0|X|Instance Recovery Stats carries the actionable numbers
        Buffer Pool Advisory|AS|0|R:IoAndMemoryRules,C:advisory-buffer|
        PGA Aggr Summary|AS|0|R:IoAndMemoryRules|
        PGA Aggr Target Stats|A|0|X|Supporting detail of PGA Aggr Summary
        PGA Aggr Target Histogram|AS|0|R:IoAndMemoryRules|
        PGA Memory Advisory|AS|0|R:IoAndMemoryRules,C:advisory-pga|
        Shared Pool Advisory|AS|0|R:IoAndMemoryRules,C:advisory-shared-pool|
        SGA Target Advisory|AS|0|R:IoAndMemoryRules,C:advisory-sga|
        Streams Pool Advisory|A|0|X|Streams/GoldenGate only
        Java Pool Advisory|A|0|X|Java in the database only
        Buffer Wait Statistics|AS|0|R:WaitEventRules,C:buffer-waits|
        Enqueue Activity|AS|0|R:WaitEventRules,C:enqueues|
        Undo Segment Summary|AS|0|R:ConfigurationRules|
        Undo Segment Stats|AS|0|C:undo-blocks|
        Latch Activity|AS|0|C:latch-wait-time|Added chart
        Latch Sleep Breakdown|AS|0|R:WaitEventRules,C:latch-sleeps|
        Latch Miss Sources|AS|0|X|Drill-down for the latch sleep findings
        Mutex Sleep Summary|AS|0|R:WaitEventRules,C:mutex-sleeps|
        Segments by Logical Reads|AS|0|R:AshAndHotspotRules,R:WaitEventRules,C:seg-logical-reads|
        Segments by Physical Reads|AS|0|R:AshAndHotspotRules,R:WaitEventRules,C:seg-physical-reads|
        Segments by Physical Read Requests|AS|0|R:WaitEventRules,C:seg-physical-read-requests|
        Segments by Row Lock Waits|AS|0|R:WaitEventRules,C:seg-row-lock-waits|
        Segments by ITL Waits|AS|0|R:WaitEventRules,C:seg-itl-waits|
        Segments by Buffer Busy Waits|AS|0|R:WaitEventRules,C:seg-buffer-busy-waits|
        Segments by Table Scans|AS|0|R:AshAndHotspotRules,R:WaitEventRules,C:seg-table-scans|
        Segments by DB Blocks Changes|AS|0|R:AshAndHotspotRules,C:seg-db-blocks-changes|
        Segments by Direct Physical|AS|0|R:WaitEventRules,C:seg-direct-physical-reads|
        Segments by|AS|0|C:seg-*|Remaining segment tables are charted generically (top objects)
        Dictionary Cache Stats|AS|0|R:CoverageGapRules,C:dictionary-cache-miss|Added rule and chart
        Library Cache Activity|AS|0|R:IoAndMemoryRules|
        Memory Dynamic Components|AS|0|R:IoAndMemoryRules,C:memory-components|
        Process Memory Summary|A|0|X|Supporting detail of PGA Aggr Summary
        SGA Memory Summary|A|0|X|Supporting detail of Memory Dynamic Components
        SGA breakdown difference|A|0|X|Supporting detail of Memory Dynamic Components
        Resource Limit Stats|AS|0|R:ConfigurationRules,C:resource-limits|
        Streams|A|0|X|Streams/GoldenGate/AQ sections (rare)
        Buffered|A|0|X|Streams/GoldenGate/AQ sections (rare)
        Persistent Queue|A|0|X|Streams/GoldenGate/AQ sections (rare)
        Rule Set|A|0|X|Streams/GoldenGate/AQ sections (rare)
        Shared Servers|A|0|X|Shared server (MTS) statistics; rare in modern installations
        init.ora Parameters|AS|1|R:ConfigurationRules,S:InitParameters|
        init.ora Multi-Valued Parameters|A|0|X|Multi-valued parameter list (exclusion lists, event settings)
        Parameters modified by|A|0|X|PDB parameter overrides; supplement of init.ora Parameters
        Multi-Valued Parameters modified by|A|0|X|PDB parameter overrides; supplement of init.ora Parameters
        WORKLOAD REPOSITORY PDB report|A|0|R:IRule|PDB reports are detected to avoid false findings
        Top SQL with Top Events|AH|1|R:AshSqlRules,R:AshAndHotspotRules,C:ash-top-sql|
        Top SQL with Top Row Sources|AH|0|R:AshSqlRules|
        Top Sessions running PQs|H|0|R:AshEventRules|
        Top Sessions|AH|1|R:AshSessionRules,R:AshAndHotspotRules|
        Top Blocking Sessions|AH|0|R:AshSessionRules,R:AshAndHotspotRules|
        Top PL/SQL Procedures|AH|0|R:AshSessionRules,R:LoadAndEfficiencyRules|
        Top Event P1/P2/P3 Values|AH|0|R:AshEventRules,R:WaitEventRules|
        Top DB Objects|AH|0|R:AshSessionRules,R:WaitEventRules|
        Top DB Files|H|0|R:AshSessionRules|
        Top User Events|H|1|R:AshEventRules,R:AshLoadRules,C:ash-top-events|
        Top Background Events|H|0|R:AshEventRules,R:AshLoadRules|
        Top Events|AH|1|R:AshEventRules,C:ash-top-events|
        Top Service/Module|H|0|R:AshSessionRules|
        Top Client IDs|H|0|R:AshSessionRules|
        Top SQL Command Types|H|0|R:AshSqlRules|
        Top Phases of Execution|H|0|R:AshSqlRules|
        Top Call Types|H|0|R:AshSessionRules|
        Top Parsing Module/Action|H|0|X|Parsing share is covered by 'Top Phases of Execution'
        Activity Over Time|AH|1|R:AshLoadRules,C:ash-activity|
        ASH Report For|H|0|M:AshInfoExtractor|Sample window and counts
        ASH Sample Window|H|0|M:AshInfoExtractor|Window, sample count and data source drive the data-quality notes
        CPU and Memory Configuration|H|0|M:AshInfoExtractor|CPU count for sessions-vs-CPU checks
        Background Idle Wait Events|S|0|X|Idle events are excluded from DB time by design
        Idle Wait Events|S|0|X|Idle events are excluded from DB time by design
        Top 5 Idle Events|S|0|X|Idle events are excluded from DB time by design
        Top 5 Timed Foreground Events|S|0|R:WaitEventRules,C:top-events|
        Statspack Warnings|S|0|R:StatspackRules|
        SQL Capture Summary|S|0|R:StatspackRules|
        Instance Activity Stats (from the same AWR)|S|0|R:LoadAndEfficiencyRules|
        A guide to the detail sections|S|0|X|Static explanatory text in the Oracle Statspack guide
        Response Time Analysis|S|0|X|Static explanatory text
        """;

    public static IReadOnlyList<Entry> Entries { get; } = Parse();

    private static List<Entry> Parse() =>
        Table.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|'))
            .Where(p => p.Length >= 5)
            .Select(p => new Entry(p[0].Trim(), p[1].Trim(), p[2].Trim() == "1", p[3].Trim(), string.IsNullOrWhiteSpace(p[4]) ? null : p[4].Trim()))
            .ToList();

    /// <summary>The entry that describes a table title: the longest matching prefix wins (so "Segments by Row Lock Waits" beats "Segments by").</summary>
    public static Entry? Find(string tableTitle)
    {
        var n = AwrTable.Normalize(tableTitle);
        Entry? best = null;
        var bestLen = -1;
        foreach (var e in Entries)
        {
            var key = AwrTable.Normalize(e.Title);
            if (key.Length > 0 && n.StartsWith(key, StringComparison.Ordinal) && key.Length > bestLen) { best = e; bestLen = key.Length; }
        }
        return best;
    }

    /// <summary>Core sections whose absence makes a report "partial" (drives the data-quality notes).</summary>
    public static IReadOnlyList<(string Name, string[] Prefixes)> CoreSections(ReportKind kind) => kind switch
    {
        ReportKind.Ash =>
        [
            ("Top events", ["Top User Events", "Top Events"]),
            ("Top SQL with Top Events", ["Top SQL with Top Events"]),
            ("Activity Over Time", ["Activity Over Time"])
        ],
        ReportKind.Statspack =>
        [
            ("Load Profile", ["Load Profile"]),
            ("Top 5 Timed Events", ["Top 5 Timed", "Top 10 Foreground Events"]),
            ("Instance Efficiency", ["Instance Efficiency"]),
            ("SQL ordered by (any)", ["SQL ordered by"])
        ],
        _ =>
        [
            ("Load Profile", ["Load Profile"]),
            ("Top foreground events", ["Top 10 Foreground Events", "Top 5 Timed"]),
            ("Time Model Statistics", ["Time Model Statistics"]),
            ("Instance Efficiency", ["Instance Efficiency"]),
            ("Foreground Wait Events", ["Foreground Wait Events"]),
            ("SQL ordered by Elapsed Time", ["SQL ordered by Elapsed Time"]),
            ("Host CPU", ["Host CPU"]),
            ("init.ora Parameters", ["init.ora Parameters"])
        ]
    };
}
