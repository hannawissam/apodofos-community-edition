// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Core.Analysis;
using Apodofos.Core.Parsing;

namespace Apodofos.Core.Providers;

/// <summary>Oracle Database AWR reports (HTML or text, 19c and above; earlier layouts tolerated).</summary>
public sealed class OracleAwrProvider : IReportProvider
{
    public string Id => "oracle-awr";
    public string DisplayName => "Oracle AWR report";
    public string Engine => "Oracle Database";
    public IReadOnlyList<string> FileExtensions { get; } = [".html", ".htm", ".txt", ".lst"];

    public bool CanHandle(string content, string? fileName)
    {
        var head = content.Length > 20000 ? content[..20000] : content;
        // ASH and Statspack reports have their own providers.
        if (head.Contains("ASH Report For", StringComparison.OrdinalIgnoreCase) || head.Contains("STATSPACK report for", StringComparison.OrdinalIgnoreCase))
            return false;
        return head.Contains("WORKLOAD REPOSITORY", StringComparison.OrdinalIgnoreCase)
               || head.Contains("AWR Report for DB", StringComparison.OrdinalIgnoreCase)
               || head.Contains("DBA_HIST", StringComparison.OrdinalIgnoreCase);
    }

    public ProviderResult Analyze(string content, string sourceName)
    {
        var report = AwrReportLoader.LoadContent(content, sourceName);
        return new ProviderResult(AnalysisEngine.Analyze(report), report.SanitizedHtml);
    }
}
