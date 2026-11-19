// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Core.Analysis;

namespace Apodofos.Core.Providers;

/// <summary>Thrown when a file is not a report any registered provider understands (or is malformed).</summary>
public class ReportFormatException(string message) : Exception(message);

/// <summary>The outcome of analyzing one report: the analysis plus the sanitised original report to show beside it.</summary>
public sealed record ProviderResult(AnalysisResult Analysis, string ReportHtml);

/// <summary>
/// A report family Apodofos can read and analyze (for example Oracle AWR, and in future ASH, Statspack, SQL Server or
/// PostgreSQL reports). The Community edition ships the Oracle AWR provider; further providers plug in through
/// <see cref="ReportProviderRegistry.Register"/> without any change to the hosts (desktop, CLI).
/// </summary>
public interface IReportProvider
{
    /// <summary>Stable identifier, e.g. "oracle-awr".</summary>
    string Id { get; }
    /// <summary>Name shown to users, e.g. "Oracle AWR report".</summary>
    string DisplayName { get; }
    /// <summary>The database engine the reports come from, e.g. "Oracle Database".</summary>
    string Engine { get; }
    /// <summary>File extensions the open dialog should offer (lower case, with dot).</summary>
    IReadOnlyList<string> FileExtensions { get; }
    /// <summary>Cheap content sniff: true if this provider can parse the content.</summary>
    bool CanHandle(string content, string? fileName);
    /// <summary>Parses and analyzes. Throws <see cref="ReportFormatException"/> for unreadable reports.</summary>
    ProviderResult Analyze(string content, string sourceName);
}
