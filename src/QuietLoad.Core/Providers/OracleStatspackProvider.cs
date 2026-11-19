// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using QuietLoad.Core.Analysis;
using QuietLoad.Core.Model;
using QuietLoad.Core.Parsing;

namespace QuietLoad.Core.Providers;

/// <summary>Oracle Statspack reports (spreport.sql / sprepsql.sql plain-text output, 9i to 19c layouts).</summary>
public sealed class OracleStatspackProvider : IReportProvider
{
    public string Id => "oracle-statspack";
    public string DisplayName => "Oracle Statspack report";
    public string Engine => "Oracle Database";
    public IReadOnlyList<string> FileExtensions { get; } = [".txt", ".lst", ".log", ".rpt"];

    public bool CanHandle(string content, string? fileName)
    {
        var head = content.Length > 20000 ? content[..20000] : content;
        return head.Contains("STATSPACK report for", StringComparison.OrdinalIgnoreCase);
    }

    public ProviderResult Analyze(string content, string sourceName)
    {
        var report = StatspackTextParser.Parse(content, sourceName);
        return new ProviderResult(AnalysisEngine.Analyze(report), report.SanitizedHtml);
    }
}
