// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Globalization;
using System.Text.RegularExpressions;
using QuietLoad.Core.Model;

namespace QuietLoad.Core.Parsing;

/// <summary>
/// Fills <see cref="AwrReportInfo"/> from the header tables of an ASH report (same shape in HTML and text): database,
/// CPU/memory configuration and the sample window (begin/end, elapsed, sample count, average active sessions).
/// Also gives the three header tables stable names. Never throws on missing pieces.
/// </summary>
internal static partial class AshInfoExtractor
{
    [GeneratedRegex(@"(?<v>\d[\d.,' \u00a0\u202f]*\d|\d)\s*\(?(?<u>mins?\.?|minutes?|minutos?|minuti|secs?\.?|sek\.?|seg\.?|secondes?|segundos?|hours?|hrs?|std\.?|stunden?|heures?|horas?|ore|s|h)(?![\p{L}])", RegexOptions.IgnoreCase)]
    private static partial Regex Duration();

    /// <summary>True when the tables look like the header of an ASH report.</summary>
    public static bool LooksLikeAsh(AwrReport report) =>
        report.Tables.Take(8).Any(t => t.HasColumn("ASH Buffer Size") || (t.Rows.Any(r => r.Length > 0 && AwrTable.Normalize(r[0]) == "analysis begin time")));

    public static void Populate(AwrReport report)
    {
        var info = report.Info;
        report.Kind = ReportKind.Ash;

        foreach (var t in report.Tables.Take(8))
        {
            if (t.HasColumn("DB Name") && t.HasColumn("Release")) t.Title = "Database Instance Information";
            else if (t.HasColumn("ASH Buffer Size") || (t.HasColumn("CPUs") && t.HasColumn("SGA Size"))) t.Title = "CPU and Memory Configuration";
            else if (t.HasColumn("Sample Time") || t.Rows.Any(r => r.Length > 0 && AwrTable.Normalize(r[0]) == "analysis begin time")) t.Title = "ASH Sample Window";
        }

        var db = report.Tables.FirstOrDefault(t => t.HasColumn("DB Name") && t.HasColumn("Release"));
        if (db?.DataRows.FirstOrDefault() is { Cells.Length: > 0 } d)
        {
            info.DbName = d.Str("DB Name");
            info.DbId = d.Str("DB Id");
            info.InstanceName = d.Str("Instance");
            info.InstanceNumber = (int)(d.Num("Inst num", "Inst Num") ?? 0);
            info.Release = d.Str("Release");
            info.IsRac = d.Str("RAC").Equals("YES", StringComparison.OrdinalIgnoreCase);
            info.HostName = d.Str("Host", "Host Name");
        }

        var cfg = report.Tables.FirstOrDefault(t => t.HasColumn("CPUs") && (t.HasColumn("SGA Size") || t.HasColumn("ASH Buffer Size")));
        if (cfg?.DataRows.FirstOrDefault() is { Cells.Length: > 0 } c)
            info.Cpus = (int?)c.Num("CPUs");

        var win = report.Tables.FirstOrDefault(t => t.RowByLabel("Analysis Begin Time") != null);
        if (win != null)
        {
            var begin = win.RowByLabel("Analysis Begin Time");
            var end = win.RowByLabel("Analysis End Time");
            info.BeginSnapTime = begin?.Str("Sample Time") is { Length: > 0 } bs ? bs : begin?.StrAt(1) ?? "";
            info.EndSnapTime = end?.Str("Sample Time") is { Length: > 0 } es ? es : end?.StrAt(1) ?? "";
            var src = begin?.Str("Data Source") is { Length: > 0 } ds ? ds : begin?.StrAt(2) ?? "";
            info.AshDataSource = src;
            info.AshFromHistory = src.Contains("DBA_HIST", StringComparison.OrdinalIgnoreCase);
            info.ElapsedMinutes = ParseMinutes(win.RowByLabel("Elapsed Time")?.StrAt(1));
            info.AshSampleCount = win.RowByLabel("Sample Count")?.NumAt(1);
            info.AshAasPerCpu = win.RowByLabel("Avg. Active Session per CPU", "Avg Active Session per CPU", "Avg. Active Sessions per CPU")?.NumAt(1);
            info.AshReportTarget = win.RowByLabel("Report Target")?.StrAt(1) ?? "";
            var aas = win.RowByLabel("Average Active Sessions")?.NumAt(1);
            // Oracle reports AAS directly; derive it from the sample count when absent (one sample per second in V$ASH, per 10 s in DBA_HIST).
            if (aas is null && info.AshSampleCount is { } sc && info.ElapsedMinutes is > 0)
                aas = sc * (info.AshFromHistory ? 10 : 1) / (info.ElapsedMinutes.Value * 60);
            if (aas is { } a && info.ElapsedMinutes is > 0) info.DbTimeMinutes = a * info.ElapsedMinutes;
        }
        if (info.Cpus is null && info.AshAasPerCpu is > 0 && info.DbTimeMinutes is > 0 && info.ElapsedMinutes is > 0)
        {
            var cpus = Math.Round(info.DbTimeMinutes.Value / info.ElapsedMinutes.Value / info.AshAasPerCpu.Value);
            if (cpus >= 1) info.Cpus = (int)cpus;
        }
    }

    /// <summary>"15.0 (mins)", "42 secs", "1.5 hours" to minutes.</summary>
    internal static double? ParseMinutes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Duration().Match(text);
        if (!m.Success) return NumberParser.ParseMinutes(text);
        var v = NumberParser.Parse(m.Groups["v"].Value);
        if (v is null) return null;
        var u = m.Groups["u"].Value.ToLowerInvariant();
        if (Regex.IsMatch(u, @"^(h|hr|hrs|hours?|std\.?|stunden?|heures?|horas?|ore)$")) return v * 60;
        if (Regex.IsMatch(u, @"^(s|secs?\.?|sek\.?|seg\.?|secondes?|segundos?)$")) return v / 60;
        return v;
    }
}
