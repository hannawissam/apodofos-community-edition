// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using QuietLoad.Core.Analysis;
using QuietLoad.Core.Parsing;

namespace QuietLoad.Core.Providers;

/// <summary>Oracle Active Session History reports (ashrpt.sql, ASH_REPORT_HTML / ASH_REPORT_TEXT; 19c layout, 11g/12c tolerated).</summary>
public sealed class OracleAshProvider : IReportProvider
{
    public string Id => "oracle-ash";
    public string DisplayName => "Oracle ASH report";
    public string Engine => "Oracle Database";
    public IReadOnlyList<string> FileExtensions { get; } = [".html", ".htm", ".txt", ".lst", ".rpt"];

    public bool CanHandle(string content, string? fileName)
    {
        var head = content.Length > 30000 ? content[..30000] : content;
        if (head.Contains("ASH Report For", StringComparison.OrdinalIgnoreCase)) return true;
        // Header-less excerpts: the distinctive ASH headings together.
        if (head.Contains("WORKLOAD REPOSITORY", StringComparison.OrdinalIgnoreCase) || head.Contains("STATSPACK report for", StringComparison.OrdinalIgnoreCase)) return false;
        return head.Contains("Top User Events", StringComparison.OrdinalIgnoreCase)
               && (head.Contains("Activity Over Time", StringComparison.OrdinalIgnoreCase) || head.Contains("ASH Report", StringComparison.OrdinalIgnoreCase)
                   || head.Contains("Top Event P1/P2/P3", StringComparison.OrdinalIgnoreCase));
    }

    public ProviderResult Analyze(string content, string sourceName)
    {
        var report = AshReportLoader.LoadContent(content, sourceName);
        return new ProviderResult(AnalysisEngine.Analyze(report), report.SanitizedHtml);
    }
}
