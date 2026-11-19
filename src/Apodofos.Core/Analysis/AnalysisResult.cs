// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

namespace Apodofos.Core.Analysis;

public sealed class ReportMeta
{
    public string SourceFile { get; set; } = "";
    /// <summary>Report family: AWR, ASH or Statspack.</summary>
    public string ReportType { get; set; } = "AWR";
    public string Format { get; set; } = "";
    public string DbName { get; set; } = "";
    public string DbId { get; set; } = "";
    public string Instance { get; set; } = "";
    public string Release { get; set; } = "";
    public string Host { get; set; } = "";
    public string Platform { get; set; } = "";
    public int? Cpus { get; set; }
    public int? Cores { get; set; }
    public double? MemoryGb { get; set; }
    public bool Rac { get; set; }
    public bool? Cdb { get; set; }
    public string BeginSnap { get; set; } = "";
    public string EndSnap { get; set; } = "";
    public string BeginTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public double? ElapsedMinutes { get; set; }
    public double? DbTimeMinutes { get; set; }
    public double? AverageActiveSessions { get; set; }
    public string AnalyzedAt { get; set; } = "";
    public string AnalyzerVersion { get; set; } = "";
    /// <summary>Detected number format, e.g. "Numbers read as 1.234,56 (comma decimal, dot thousands)." (shown beside the report header).</summary>
    public string NumberFormat { get; set; } = "";
}

/// <summary>Headline number for the dashboard tiles.</summary>
public sealed record Kpi(string Key, string Label, string Value, string? Unit, string Status, string Hint);

public sealed class HealthSummary
{
    public int Score { get; set; }
    public string Grade { get; set; } = "";
    public string Headline { get; set; } = "";
    public List<string> Paragraphs { get; set; } = [];
    public Dictionary<string, int> Counts { get; set; } = [];
    /// <summary>False when the score must not be trusted (window under 5 minutes, truncated report, ambiguous numbers): the grade is then "n/a".</summary>
    public bool Reliable { get; set; } = true;
    /// <summary>Why the health score is not reliable (empty when it is).</summary>
    public string ReliabilityNote { get; set; } = "";
}

public sealed class ChartSeries
{
    public string Name { get; set; } = "";
    public List<double?> Values { get; set; } = [];
}

/// <summary>Chart definition rendered by the offline SVG chart library in the web UI.</summary>
public sealed class ChartSpec
{
    public string Id { get; set; } = "";
    public string Group { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    /// <summary>hbar | bar | donut | line | stacked</summary>
    public string Type { get; set; } = "hbar";
    public List<string> Labels { get; set; } = [];
    public List<ChartSeries> Series { get; set; } = [];
    public string? Unit { get; set; }
    /// <summary>Plain-English hint: how to read this chart.</summary>
    public string? Explain { get; set; }
    public string? TableRef { get; set; }
    public bool LogScale { get; set; }
    /// <summary>Optional reference / target line value.</summary>
    public double? Reference { get; set; }
    public string? ReferenceLabel { get; set; }
    public string? XLabel { get; set; }
    public string? YLabel { get; set; }
}

public sealed class TableDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Section { get; set; }
    public string? Summary { get; set; }
    public List<string> Headers { get; set; } = [];
    public List<string[]> Rows { get; set; } = [];
}

public sealed class SqlSummary
{
    public string SqlId { get; set; } = "";
    public string Module { get; set; } = "";
    public double? ElapsedSec { get; set; }
    public double? CpuSec { get; set; }
    public double? IoWaitSec { get; set; }
    public double? Executions { get; set; }
    public double? ElapsedPerExecSec { get; set; }
    public double? BufferGets { get; set; }
    public double? PhysicalReads { get; set; }
    public double? PctDbTime { get; set; }
    public double? PctCpu { get; set; }
    public double? PctIo { get; set; }
    public double? ParseCalls { get; set; }
    public double? RowsProcessed { get; set; }
    public double? VersionCount { get; set; }
    public string Text { get; set; } = "";
    public List<string> AppearsIn { get; set; } = [];
}

/// <summary>Report-level data quality: how much the numbers in this report can be trusted.</summary>
public sealed class DataQualityReport
{
    /// <summary>Good | Fair | Poor</summary>
    public string Level { get; set; } = "Good";
    public List<string> Notes { get; set; } = [];
    /// <summary>Top-of-report warnings that apply to the whole report (truncated, impossible values, ambiguous number format, unreliable health).</summary>
    public List<string> Alerts { get; set; } = [];
    /// <summary>Why the health score cannot be trusted (window under 5 minutes, truncated or ambiguous report); empty when it can.</summary>
    public string HealthNote { get; set; } = "";
}

/// <summary>Findings that share one underlying cause (e.g. row lock contention -> blocking SQL -> hot segment).</summary>
public sealed class RootCauseGroup
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Plain-English summary of how the findings connect.</summary>
    public string Summary { get; set; } = "";
    public List<string> FindingIds { get; set; } = [];
    /// <summary>Id of the symptom finding that defines the cause (its recommendation is the group's action).</summary>
    public string LeadFindingId { get; set; } = "";
    /// <summary>Share of DB time of the causal event or metric (0-100): the largest symptom, never the share of a member SQL statement.</summary>
    public double ImpactPct { get; set; }
}

/// <summary>One entry of the ranked "Fix first" plan.</summary>
public sealed class ActionItem
{
    public int Rank { get; set; }
    public string Action { get; set; } = "";
    public string Why { get; set; } = "";
    /// <summary>Estimated share of DB time that could be removed (0-100), a rough upper bound.</summary>
    public double ExpectedImpactPct { get; set; }
    /// <summary>Plain wording of the expected gain, e.g. "up to ~35% of DB time".</summary>
    public string ExpectedImpact { get; set; } = "";
    /// <summary>Low | Medium | High</summary>
    public string Effort { get; set; } = "Medium";
    /// <summary>Low | Medium | High</summary>
    public string Risk { get; set; } = "Low";
    public Confidence Confidence { get; set; } = Confidence.High;
    public List<string> FindingIds { get; set; } = [];
}

public sealed class AnalysisResult
{
    public ReportMeta Meta { get; set; } = new();
    public HealthSummary Health { get; set; } = new();
    public List<Kpi> Kpis { get; set; } = [];
    public List<Finding> Findings { get; set; } = [];
    public List<ChartSpec> Charts { get; set; } = [];
    public List<TableDto> Tables { get; set; } = [];
    public List<SqlSummary> TopSql { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    /// <summary>Offline Oracle documentation catalog entries relevant to this report kind (searchable Documentation tab).</summary>
    public List<Apodofos.Core.Knowledge.DocDto> Docs { get; set; } = [];
    /// <summary>Report table id to catalog doc ids ("Learn more" links beside each report table).</summary>
    public Dictionary<string, List<string>> SectionDocs { get; set; } = [];

    /// <summary>Report-level data-quality assessment (short window, idle DB, partial report, inconsistent numbers ...).</summary>
    public DataQualityReport DataQuality { get; set; } = new();
    /// <summary>Related findings grouped by root cause; each member finding also carries RootCauseGroup and RelatedIds.</summary>
    public List<RootCauseGroup> RootCauses { get; set; } = [];
    /// <summary>Ranked "Fix first" plan (top 5 actions with expected impact, effort and risk).</summary>
    public List<ActionItem> ActionPlan { get; set; } = [];
}
