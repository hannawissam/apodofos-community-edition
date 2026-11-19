// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Globalization;
using System.Text.RegularExpressions;
using QuietLoad.Core.Model;

namespace QuietLoad.Core.Parsing;

/// <summary>
/// Detects reports that were cut off or garbled (missing "End of Report" marker) and checks the header facts for
/// internal consistency (window length vs begin/end time, release number, snapshot order, plausible dates).
/// Findings of these checks become warnings and lower the data quality; they never throw.
/// </summary>
internal static partial class ReportIntegrity
{
    [GeneratedRegex(@"End\s+of\s+Report", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EndMarker();

    private static readonly string[] TimeFormats =
    [
        "dd-MMM-yy HH:mm:ss", "dd-MMM-yyyy HH:mm:ss", "d-MMM-yy HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "dd-MMM-yy HH:mm", "dd-MMM-yyyy HH:mm",
        "dd-MMM-yy hh:mm:ss tt", "dd-MMM-yyyy hh:mm:ss tt", "d-MMM-yy h:mm:ss tt", "dd-MMM-yy hh.mm.ss tt", "dd-MMM-yy HH.mm.ss", "dd-MMM-yyyy HH.mm.ss",
        "MM/dd/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm:ss", "MM/dd/yy HH:mm:ss", "dd.MM.yyyy HH:mm:ss", "dd.MM.yy HH:mm:ss", "dd/MM/yy HH:mm:ss", "d/M/yyyy HH:mm:ss"
    ];

    // month names of the languages Oracle prints (NLS_DATE_LANGUAGE); invariant first, then the others
    private static readonly string[] CultureNames = ["", "fr-FR", "de-DE", "es-ES", "it-IT", "pt-BR", "pt-PT", "nl-NL", "en-GB", "en-US"];
    private static readonly List<CultureInfo> Cultures = CultureNames.Select(n =>
    {
        try { return CultureInfo.GetCultureInfo(n); } catch { return null!; }
    }).Where(c => c is not null).ToList();

    /// <summary>Called by the parsers with the raw report text: flags a missing end-of-report marker.</summary>
    public static void InspectRaw(AwrReport report, string? content)
    {
        if (string.IsNullOrEmpty(content) || EndMarker().IsMatch(content)) return;
        var isAsh = report.Kind == ReportKind.Ash || content.Contains("ASH Report For", StringComparison.OrdinalIgnoreCase);
        var what = isAsh ? "ASH" : report.Kind == ReportKind.Statspack ? "Statspack" : "AWR";
        var msg = $"The 'End of Report' marker is missing: this {what} report looks truncated (cut off during copy, transfer or saving), so sections and totals may be incomplete.";
        report.IntegrityWarnings.Add(msg);
        // Statspack layouts of old releases do not always print the marker: a note, not a verdict
        if (report.Kind != ReportKind.Statspack) { report.Truncated = true; report.TruncationReason = msg; }
    }

    /// <summary>
    /// Parses a report timestamp, including month names in other languages ("14-janv.-26", "14-Mär-26", "14-ene-26", "14-gen-26",
    /// "14-set-26"), 12-hour clocks with AM/PM (also "a. m."/"p. m.") and dd.MM.yyyy.
    /// </summary>
    public static bool TryParseTime(string? text, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = Regex.Replace(text.Trim().Replace('\u00a0', ' ').Replace('\u202f', ' '), @"\s+", " ");
        foreach (var culture in Cultures)
        {
            if (DateTime.TryParseExact(t, TimeFormats, culture, DateTimeStyles.AllowWhiteSpaces, out value)) return true;
            // abbreviations with a trailing dot ("janv.", "févr.") and AM/PM variants
            var d = t.Replace(".-", "-").Replace("a. m.", "AM").Replace("p. m.", "PM");
            if (!ReferenceEquals(d, t) && DateTime.TryParseExact(d, TimeFormats, culture, DateTimeStyles.AllowWhiteSpaces, out value)) return true;
        }
        return false;
    }

    // majors that never existed, and the only minors (second component) each old major shipped
    private static readonly HashSet<int> UnknownMajors = [13, 14, 15, 16, 17, 20, 22];
    private static readonly Dictionary<int, int[]> ValidMinors = new()
    {
        [8] = [0, 1], [9] = [0, 2], [10] = [1, 2], [11] = [1, 2], [12] = [1, 2]
    };

    /// <summary>Header consistency checks; every returned string is a human-readable warning.</summary>
    public static List<string> Consistency(AwrReport report)
    {
        var issues = new List<string>();
        var info = report.Info;
        try
        {
            if (!string.IsNullOrWhiteSpace(info.Release))
            {
                var p = info.Release.Split('.');
                if (!int.TryParse(p[0], out var major)) issues.Add($"The release '{info.Release}' is not a valid Oracle version number.");
                else if (UnknownMajors.Contains(major))
                    issues.Add($"The release '{info.Release}' does not exist (Oracle has no {major}.x database release): the header may be garbled.");
                else if (ValidMinors.TryGetValue(major, out var minors) && p.Length > 1 && int.TryParse(p[1], out var minor) && !minors.Contains(minor))
                    issues.Add($"The release '{info.Release}' does not exist (Oracle {major} shipped as {string.Join(" and ", minors.Select(m => $"{major}.{m}"))} only): the header may be garbled.");
            }

            if (int.TryParse(info.BeginSnapId, out var b) && int.TryParse(info.EndSnapId, out var e) && e <= b)
                issues.Add($"The end snapshot ({e}) is not after the begin snapshot ({b}): the snapshot range is reversed or garbled.");

            var haveBegin = TryParseTime(info.BeginSnapTime, out var bt);
            var haveEnd = TryParseTime(info.EndSnapTime, out var et);
            if (haveBegin && (bt.Year < 1998 || bt > DateTime.Now.AddDays(2)))
                issues.Add($"The begin time '{info.BeginSnapTime}' is not a plausible date (year {bt.Year}): the clock or the header may be wrong.");
            if (haveBegin && haveEnd)
            {
                var span = (et - bt).TotalMinutes;
                if (span <= 0) issues.Add($"The end time ({info.EndSnapTime}) is not after the begin time ({info.BeginSnapTime}).");
                else if (info.ElapsedMinutes is > 0 and var el && Math.Abs(span - el) > Math.Max(1.5, 0.10 * Math.Max(span, el)))
                    issues.Add($"Begin and end time span {span:0.#} minutes but the report states an elapsed time of {el:0.#} minutes (the instance may have restarted inside the window, or the report is damaged).");
            }
        }
        catch { /* a failing check must never break the analysis */ }
        return issues;
    }
}
