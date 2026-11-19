// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using QuietLoad.Core.Analysis;

namespace QuietLoad.Core.Providers;

public enum ReportLoadStatus { Ok, Partial, Unsupported, Unreadable }

/// <summary>Outcome of opening one file. <see cref="Message"/> is empty for <see cref="ReportLoadStatus.Ok"/>.</summary>
public sealed record ReportLoadOutcome(ReportLoadStatus Status, string Message, ProviderResult? Result, string? ProviderId);

/// <summary>
/// One path for opening a report from the button, drag and drop or the command line: the same checks and the same
/// plain-English messages everywhere. Never throws for bad files; it returns a status instead.
/// </summary>
public static class ReportLoader
{
    public const long MaxFileBytes = 400L * 1024 * 1024;

    public const string UnsupportedMessage =
        "This file isn't a supported Oracle report. QuietLoad can read AWR, ASH and Statspack reports saved as HTML or text. Check that you exported the full report, then try again.";
    public const string UnreadableMessage = "Couldn't read this file. It may be open in another program or too large.";

    public static string PartialMessage(string reportType) =>
        "We recognized " + (reportType.StartsWith('A') ? "an " : "a ") + reportType + " report, but some sections are missing. Showing what could be read.";

    /// <summary>True when the parser had to skip or could not read parts of the report.</summary>
    public static bool IsPartial(AnalysisResult a) =>
        a.Warnings.Any(w => w.Contains("could not be", StringComparison.OrdinalIgnoreCase)
                            || w.Contains("No tables were recognised", StringComparison.OrdinalIgnoreCase)
                            || w.Contains("skipped", StringComparison.OrdinalIgnoreCase)
                            || w.Contains("not found", StringComparison.OrdinalIgnoreCase));

    public static ReportLoadOutcome Load(string path, string? forcedProviderId = null)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes)
                return new(ReportLoadStatus.Unreadable, UnreadableMessage, null, null);
            var result = ReportProviderRegistry.AnalyzeFile(path, forcedProviderId);
            if (IsPartial(result.Analysis))
                return new(ReportLoadStatus.Partial, PartialMessage(result.Analysis.Meta.ReportType), result, IdForType(result.Analysis.Meta.ReportType));
            return new(ReportLoadStatus.Ok, "", result, IdForType(result.Analysis.Meta.ReportType));
        }
        catch (ReportFormatException) { return new(ReportLoadStatus.Unsupported, UnsupportedMessage, null, null); }
        catch (IOException) { return new(ReportLoadStatus.Unreadable, UnreadableMessage, null, null); }
        catch (UnauthorizedAccessException) { return new(ReportLoadStatus.Unreadable, UnreadableMessage, null, null); }
        catch (OutOfMemoryException) { return new(ReportLoadStatus.Unreadable, UnreadableMessage, null, null); }
        catch (ArgumentException) { return new(ReportLoadStatus.Unsupported, UnsupportedMessage, null, null); }
        catch (Exception) { return new(ReportLoadStatus.Unsupported, UnsupportedMessage, null, null); }
    }

    /// <summary>Maps the report family shown in the UI back to the provider id used to force it.</summary>
    public static string IdForType(string reportType) => reportType switch
    {
        "ASH" => "oracle-ash",
        "Statspack" => "oracle-statspack",
        _ => "oracle-awr"
    };
}
