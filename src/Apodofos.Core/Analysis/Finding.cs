// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text.Json.Serialization;

namespace Apodofos.Core.Analysis;

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    Critical = 0,
    High = 1,
    Medium = 2,
    Low = 3,
    Info = 4,
    /// <summary>A check that passed - shown so non-DBAs see what is healthy too.</summary>
    Ok = 5
}

/// <summary>How far a finding can be trusted given the quality and size of the data behind it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Confidence>))]
public enum Confidence
{
    High = 0,
    Medium = 1,
    Low = 2
}

public sealed record Evidence(string Label, string Value, string? Note = null);

public sealed record SqlSnippet(string Title, string Code, string? Caution = null);

public sealed record DocRef(string Title, string Url);

/// <summary>
/// One diagnosis. Text fields are written for two audiences: <see cref="Plain"/> and
/// <see cref="WhyItMatters"/> for non-DBAs, <see cref="Technical"/> for DBAs.
/// </summary>
public sealed class Finding
{
    public string Id { get; set; } = "";
    public Severity Severity { get; set; }
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>What we found, in plain English.</summary>
    public string Plain { get; set; } = "";
    /// <summary>Business impact, in plain English.</summary>
    public string WhyItMatters { get; set; } = "";
    /// <summary>DBA-level explanation with metric names.</summary>
    public string Technical { get; set; } = "";
    public List<Evidence> Evidence { get; set; } = [];
    public List<string> Recommendations { get; set; } = [];
    public List<SqlSnippet> Sql { get; set; } = [];
    public List<DocRef> Docs { get; set; } = [];
    /// <summary>AWR repository view(s) the underlying data comes from (e.g. DBA_HIST_SQLSTAT).</summary>
    public List<string> SourceViews { get; set; } = [];
    /// <summary>Ids of report tables this finding is based on (used to scroll the report pane).</summary>
    public List<string> TableRefs { get; set; } = [];
    /// <summary>Ids of dashboard charts that illustrate the finding.</summary>
    public List<string> ChartRefs { get; set; } = [];
    /// <summary>Approximate share of DB time involved (0-100), used for ranking within a severity.</summary>
    public double Impact { get; set; }

    /// <summary>High / Medium / Low: how much the data behind this finding can be trusted (see <see cref="DataQuality"/>).</summary>
    public Confidence Confidence { get; set; } = Confidence.High;
    /// <summary>Why the confidence is not High (short window, idle database, few samples, missing sections, inconsistent numbers). Empty when High.</summary>
    public string DataQuality { get; set; } = "";
    /// <summary>Root-cause group this finding belongs to (see <see cref="AnalysisResult.RootCauses"/>); empty when it stands alone.</summary>
    public string RootCauseGroup { get; set; } = "";
    /// <summary>Ids of related findings (same root cause: symptom, SQL, hot segment ...).</summary>
    public List<string> RelatedIds { get; set; } = [];
}
