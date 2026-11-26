// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Globalization;
using Apodofos.Core.Model;
using Apodofos.Core.Parsing;

namespace Apodofos.Core.Analysis;

/// <summary>Shared state and helpers for the rules.</summary>
public sealed class AnalysisContext
{
    private int _seq;

    public AnalysisContext(AwrReport report)
    {
        Report = report;
        SanityChecks = SanityScan.Run(report); // must run first: it invalidates impossible cells for every later read
        Params = report.InitParameters();
        SqlTexts = report.SqlTexts();
        Efficiency = report.EfficiencyPercentages();
        Cpus = report.Info.Cpus
               ?? (int?)report.TableStartingWith("Host CPU")?.DataRows.FirstOrDefault().Num("CPUs")
               ?? (int?)NumberParser.FirstNumberIn(report.TableStartingWith("Host CPU")?.Title);
    }

    public AwrReport Report { get; }
    public AwrReportInfo Info => Report.Info;
    public Dictionary<string, string> Params { get; }
    public Dictionary<string, string> SqlTexts { get; }
    public Dictionary<string, double> Efficiency { get; }
    public int? Cpus { get; }
    /// <summary>Impossible values found by <see cref="SanityScan"/> (they were blanked, so rules never see them).</summary>
    internal List<DataQualityAnalyzer.Check> SanityChecks { get; }
    public List<Finding> Findings { get; } = [];

    public double? ElapsedSec => Info.ElapsedMinutes * 60;
    public double? DbTimeSec => Info.DbTimeMinutes * 60;
    public double? Aas => Info.AverageActiveSessions;

    public string BeginSnap => string.IsNullOrEmpty(Info.BeginSnapId) ? "&begin_snap" : Info.BeginSnapId;
    public string EndSnap => string.IsNullOrEmpty(Info.EndSnapId) ? "&end_snap" : Info.EndSnapId;
    public string InstNum => Info.InstanceNumber > 0 ? Info.InstanceNumber.ToString(CultureInfo.InvariantCulture) : "&inst_num";

    /// <summary>Fills the common substitution variables used by the SQL snippets.</summary>
    public string Sub(string sql) => sql
        .Replace("&begin_snap", SqlSafe.Digits(BeginSnap, "begin_snap"))
        .Replace("&end_snap", SqlSafe.Digits(EndSnap, "end_snap"))
        .Replace("&inst_num", InstNum);

    public Finding Add(Finding f)
    {
        if (string.IsNullOrEmpty(f.Id)) f.Id = $"F{++_seq:000}";
        f.Sql = f.Sql.Select(s => s with { Code = Sub(s.Code).Trim() }).ToList();
        Findings.Add(f);
        return f;
    }

    public bool HasFinding(string category, string titleFragment) =>
        Findings.Any(f => f.Category == category && f.Title.Contains(titleFragment, StringComparison.OrdinalIgnoreCase));

    public string? Param(string name) => Params.TryGetValue(name, out var v) ? v.Trim('\'', '"', ' ') : null;

    /// <summary>Load Profile "per second" value by label.</summary>
    public double? LoadPerSec(params string[] labels) =>
        Report.Table("Load Profile")?.RowByLabel(labels)?.Num("Per Second");

    public double? LoadPerTxn(params string[] labels) =>
        Report.Table("Load Profile")?.RowByLabel(labels)?.Num("Per Transaction");

    public double? Eff(string key) => Efficiency.TryGetValue(AwrTable.Normalize(key), out var v) ? v : null;

    /// <summary>Time Model "% of DB Time" for a statistic.</summary>
    public double? TimeModelPct(string statistic) =>
        Report.Table("Time Model Statistics")?.RowByLabel(statistic)?.Num("% of DB Time");

    public double? TimeModelSec(string statistic) =>
        Report.Table("Time Model Statistics")?.RowByLabel(statistic)?.Num("Time (s)");

    public double? InstanceStat(string name)
    {
        var t = Report.Table("Instance Activity Stats") ?? Report.Table("Key Instance Activity Stats");
        return t?.RowByLabel(name)?.Num("Total");
    }

    public double? InstanceStatPerSec(string name)
    {
        var t = Report.Table("Instance Activity Stats") ?? Report.Table("Key Instance Activity Stats");
        return t?.RowByLabel(name)?.Num("per Second");
    }


    /// <summary>
    /// CPU actually used, as a share of the server's CPU capacity (0-100+): the host's busy time when the report has it,
    /// otherwise DB CPU time over CPUs x elapsed (AWR) or the sampled "on CPU" sessions over the CPU count (ASH, an upper bound).
    /// Null when it cannot be told. Average active sessions are NOT CPU use: sessions that wait for locks or disk count as active.
    /// </summary>
    public double? CpuUsedPct
    {
        get
        {
            var idle = Report.TableStartingWith("Host CPU")?.DataRows.FirstOrDefault().Num("%Idle");
            if (idle is not null) return Math.Clamp(100 - idle.Value, 0, 100);
            if (Cpus is not > 0) return null;
            if (Report.Kind == ReportKind.Ash)
            {
                var onCpu = Report.TableStartingWith("Top User Events")?.DataRows
                    .Where(r => r.Str("Event").StartsWith("CPU", StringComparison.OrdinalIgnoreCase)).Sum(r => r.Num("Avg Active Sessions") ?? 0);
                return onCpu is > 0 ? onCpu / Cpus * 100 : null;
            }
            var cpuSec = TimeModelSec("DB CPU");
            return cpuSec is > 0 && ElapsedSec is > 0 ? cpuSec / (Cpus * ElapsedSec) * 100 : null;
        }
    }

    /// <summary>
    /// "On average about 4.7 sessions were active ... 5 CPUs": states that active sessions include waiting sessions and gives the
    /// CPU actually used when known, instead of calling active sessions a share of CPU capacity.
    /// </summary>
    public string ActiveSessionsSentence(string noun = "user sessions")
    {
        if (Aas is not { } aas) return "";
        var s = $"On average about {N(aas, 1)} {noun} were active at the same time";
        if (Cpus is not > 0) return s + ".";
        s += $", on a server with {N(Cpus)} CPUs (that is {N(aas / Cpus * 100, 0)}% of the CPU count, but sessions waiting for locks or disk are active too, so it is not CPU use";
        s += CpuUsedPct is { } used ? $"; CPU actually used {(Report.Kind == ReportKind.Ash ? "by sampled sessions " : "")}was about {N(used, 0)}%)." : ").";
        return s;
    }

    // ---------- formatting ----------
    public static string N(double? v, int decimals = 0) =>
        v is null ? "n/a" : v.Value.ToString("N" + decimals, CultureInfo.InvariantCulture);

    public static string P(double? v, int decimals = 1) => v is null ? "n/a" : N(v, decimals) + "%";

    public static string Ms(double? v) => v is null ? "n/a" : v < 1 ? N(v * 1000, 0) + " µs" : N(v, v < 10 ? 2 : 1) + " ms";

    /// <summary>Human readable duration from seconds ("2.3 hours", "45 minutes").</summary>
    public static string Dur(double? seconds)
    {
        if (seconds is null) return "n/a";
        var s = seconds.Value;
        if (s >= 86400) return N(s / 86400, 1) + " days";
        if (s >= 3600) return N(s / 3600, 1) + " hours";
        if (s >= 120) return N(s / 60, 0) + " minutes";
        return N(s, s < 10 ? 2 : 0) + " seconds";
    }

    /// <summary>Compact large numbers: 1.2K, 3.4M, 5.6B.</summary>
    public static string Big(double? v)
    {
        if (v is null) return "n/a";
        var a = Math.Abs(v.Value);
        return a switch
        {
            >= 1e12 => N(v / 1e12, 1) + "T",
            >= 1e9 => N(v / 1e9, 1) + "B",
            >= 1e6 => N(v / 1e6, 1) + "M",
            >= 1e4 => N(v / 1e3, 1) + "K",
            _ => N(v, a < 10 ? 2 : 0)
        };
    }

    public static string Bytes(double? v)
    {
        if (v is null) return "n/a";
        var a = v.Value;
        return a switch
        {
            >= 1L << 40 => N(a / (1L << 40), 1) + " TB",
            >= 1L << 30 => N(a / (1L << 30), 1) + " GB",
            >= 1L << 20 => N(a / (1L << 20), 1) + " MB",
            >= 1L << 10 => N(a / (1L << 10), 1) + " KB",
            _ => N(a, 0) + " B"
        };
    }
}
