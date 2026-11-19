// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using QuietLoad.Core.Model;

namespace QuietLoad.Core.Analysis.Rules;

/// <summary>A group of related checks. Rules add findings to the context and never throw for missing sections.</summary>
public interface IRule
{
    string Name { get; }
    /// <summary>Report families this rule applies to (default: AWR and Statspack, which share most sections).</summary>
    ReportKind[] Kinds => [ReportKind.Awr, ReportKind.Statspack];
    void Evaluate(AnalysisContext ctx);
}

internal static class SeverityMath
{
    public static Severity Raise(Severity s) => s == Severity.Critical ? s : (Severity)((int)s - 1);

    /// <summary>One step less severe (never below Low, never touches Info/Ok).</summary>
    public static Severity Lower(Severity s) => s is Severity.Critical or Severity.High or Severity.Medium ? (Severity)((int)s + 1) : s;

    /// <summary>Makes sure a severity is no worse than <paramref name="max"/> (Critical is the worst).</summary>
    public static Severity AtMost(Severity s, Severity max) => s < max ? max : s;

    /// <summary>Severity from share of DB time (Oracle's Performance Method focuses on the largest DB time consumers).</summary>
    public static Severity FromDbTimePct(double pct) => pct switch
    {
        >= 40 => Severity.Critical,
        >= 25 => Severity.High,
        >= 10 => Severity.Medium,
        _ => Severity.Low
    };

    /// <summary>
    /// A statement that uses 50% of DB time is not urgent when the whole database was nearly idle.
    /// Caps the severity when the absolute load (Average Active Sessions, DB time) is small.
    /// </summary>
    public static Severity ForLoad(AnalysisContext ctx, Severity s)
    {
        if (s >= Severity.Low) return s;
        if (ctx.Aas is < 0.2 || ctx.DbTimeSec is < 120) return AtMost(s, Severity.Low);
        if (ctx.Aas is < 1.0) return AtMost(s, Severity.Medium);
        return s;
    }

    /// <summary>True when the whole report shows very little activity (so ratios and shares carry little weight).</summary>
    public static bool LightLoad(AnalysisContext ctx) => ctx.Aas is < 1.0 || ctx.DbTimeSec is < 120;
}

/// <summary>Small null-safe helpers shared by the rules (AwrRow is a struct, so FirstOrDefault() never returns null).</summary>
internal static class RuleHelpers
{
    public static AwrRow? FirstRow(this AwrTable? t) => t is { Rows.Count: > 0 } ? new AwrRow(t, t.Rows[0]) : null;

    public static AwrRow? Find(this AwrTable? t, Func<AwrRow, bool> predicate)
    {
        if (t == null) return null;
        foreach (var r in t.DataRows)
            if (predicate(r)) return r;
        return null;
    }

    public static double? Val(this AwrRow? r, params string[] columns) => r is { Table: not null } v ? v.Num(columns) : null;

    public static string Txt(this AwrRow? r, params string[] columns) => r is { Table: not null } v ? v.Str(columns) : "";

    public static double? Val(this AwrRow r, params string[] columns) => r.Table == null ? null : r.Num(columns);

    public static string Txt(this AwrRow r, params string[] columns) => r.Table == null ? "" : r.Str(columns);

    public static string Clean(string? s) => (s ?? "").Trim().TrimEnd(':').Trim();

    /// <summary>Release as (major, minor) from e.g. "12.2.0.1.0"; (0, 0) when unknown (treated as a current release).</summary>
    public static (int Major, int Minor) Version(AnalysisContext ctx)
    {
        var p = (ctx.Info.Release ?? "").Split('.');
        var major = p.Length > 0 && int.TryParse(p[0], out var a) ? a : 0;
        var minor = p.Length > 1 && int.TryParse(p[1], out var b) ? b : 0;
        return (major, minor);
    }

    /// <summary>True when the report's release is at least major.minor, or when the release is unknown (be optimistic, not noisy).</summary>
    public static bool AtLeast(AnalysisContext ctx, int major, int minor = 0)
    {
        var (a, b) = Version(ctx);
        if (a == 0) return true;
        return a > major || (a == major && b >= minor);
    }

    /// <summary>True for a "WORKLOAD REPOSITORY PDB report" (PDB-level snapshots): CDB-wide sections are absent and some parameters are inherited.</summary>
    public static bool IsPdbReport(AnalysisContext ctx) =>
        ctx.Report.Tables.Any(t => t.Title.Contains("PDB report", StringComparison.OrdinalIgnoreCase) || t.Title.Contains("PDB snapshots", StringComparison.OrdinalIgnoreCase));

    // schemas that Oracle E-Business Suite seeds (and owns); application modules look like "e:ONT:..." / "FRM:..."
    private static readonly HashSet<string> EbsOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "APPS", "APPLSYS", "APPLSYSPUB", "APPS_NE", "AR", "AP", "GL", "ONT", "OE", "INV", "PO", "WIP", "BOM", "MRP", "CST", "FA", "PA", "PER", "HR", "HXC",
        "OKC", "OKS", "OKL", "QP", "XLA", "XLE", "ZX", "IBY", "JTF", "CSI", "CS", "CSD", "CSF", "CSP", "ENG", "EGO", "MTL", "WSH", "WSM", "ASO", "AMS", "AMW",
        "IEX", "IGI", "FEM", "FND", "FV", "JA", "JE", "JG", "JL", "PJM", "POM", "PN", "PSA", "RLM", "OZF", "BEN", "ICX", "EAM", "CE", "AK", "ALR", "ASG", "MSC", "MSD"
    };

    /// <summary>True for objects owned by an Oracle E-Business Suite seeded schema (or the application family named by a "e:XXX" module).</summary>
    public static bool IsEbsSeededOwner(AnalysisContext ctx, string? owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) return false;
        if (EbsOwners.Contains(owner.Trim())) return true;
        // a module "e:ONT:..." names the application whose schema is ONT
        foreach (var t in ctx.Report.Tables)
        {
            var c = t.Col("SQL Module", "Module");
            if (c < 0) continue;
            foreach (var row in t.Rows)
                if (c < row.Length && row[c].StartsWith("e:", StringComparison.OrdinalIgnoreCase) && row[c].Split(':').ElementAtOrDefault(1) is { Length: > 0 } fam
                    && fam.Equals(owner.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>"SYS_IOT_TOP_12345": the primary-key index of an index-organized table (the table's data lives in it).</summary>
    public static bool IsIotIndexName(string? name) =>
        !string.IsNullOrEmpty(name) && System.Text.RegularExpressions.Regex.IsMatch(name.Trim(), @"^SYS_IOT_TOP_\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>"3 executions" / "1 execution".</summary>
    public static string Plural(double? n, string singular, string? plural = null) =>
        $"{AnalysisContext.N(n)} {(n == 1 ? singular : plural ?? singular + "s")}";

    /// <summary>Seconds as a readable duration, but sub-second values in milliseconds ("4.2 ms") instead of "0.00 seconds".</summary>
    public static string DurFine(double? seconds) =>
        seconds is { } s && s > 0 && s < 1 ? AnalysisContext.Ms(s * 1000) : AnalysisContext.Dur(seconds);

    /// <summary>Average wait in ms: the reported value, or computed from total time and waits when the report rounded it to 0.</summary>
    public static double? AvgMs(double? reported, double? seconds, double? waits) =>
        reported is > 0 ? reported : seconds is > 0 && waits is > 0 ? seconds / waits * 1000 : reported;

    /// <summary>Host CPU %Idle from the Host CPU table (null when the report has none).</summary>
    public static double? HostIdle(AnalysisContext ctx) => ctx.Report.TableStartingWith("Host CPU").FirstRow().Val("%Idle");

    /// <summary>
    /// init.ora parameter value. In a CDB the report lists a parameter once for the CDB (blank PDB column) and again per PDB;
    /// the CDB-level row wins so that a blank PDB override cannot hide the real value.
    /// </summary>
    public static string? Param(AnalysisContext ctx, string name)
    {
        var t = ctx.Report.Table("init.ora Parameters");
        if (t == null) return ctx.Param(name);
        var nameCol = t.Col("Parameter Name");
        var valCol = t.Col("Begin value", "Begin Value");
        var pdbCol = t.Col("PDB Name");
        if (nameCol < 0 || valCol < 0) return ctx.Param(name);
        string? any = null;
        foreach (var row in t.Rows)
        {
            if (row.Length <= Math.Max(nameCol, valCol) || !row[nameCol].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var v = row[valCol].Trim().Trim('\'', '"');
            var isPdb = pdbCol >= 0 && pdbCol < row.Length && row[pdbCol].Trim().Length > 0;
            if (!isPdb) return v;
            any ??= v;
        }
        return any;
    }

    /// <summary>Cumulative or per-second statistic from "Instance Activity Stats" (total).</summary>
    public static double? Stat(AnalysisContext ctx, string name) => ctx.InstanceStat(name);

    public static double? StatPerSec(AnalysisContext ctx, string name) => ctx.InstanceStatPerSec(name);

    /// <summary>Wait-event row of the foreground event table: % DB time / avg ms / seconds.</summary>
    public static AwrRow? ForegroundEvent(AnalysisContext ctx, string eventName)
    {
        var t = ctx.Report.Table("Foreground Wait Events");
        return t?.RowByLabel(eventName);
    }

    public static double? ForegroundEventPct(AnalysisContext ctx, string eventName) =>
        ForegroundEvent(ctx, eventName).Val("% DB time", "%DB time", "% DB Time");

    /// <summary>Share of DB time of a wait class (Foreground Wait Class / Wait Classes by Total Wait Time).</summary>
    public static double? WaitClassPct(AnalysisContext ctx, string waitClass) =>
        ctx.Report.Table("Foreground Wait Class", "Wait Classes by Total Wait Time")?.RowByLabel(waitClass).Val("% DB time", "%DB time");

    /// <summary>Column holding the measured value of a "Segments by ..." table (the column just before %Total / % of Capture).</summary>
    public static int SegmentValueColumn(AwrTable seg)
    {
        var share = seg.Col("%Total", "% of Capture");
        return share > 0 ? share - 1 : -1;
    }

    public static string SegmentName(AwrRow r)
    {
        var sub = r.Str("Subobject Name");
        return $"{r.Str("Owner")}.{r.Str("Object Name")}" + (sub.Length > 0 ? $" ({sub})" : "");
    }

    /// <summary>The 'PDB Name' / 'Container Name' of a row (CDB reports), empty otherwise.</summary>
    public static string Container(AwrRow r) => r.Str("PDB Name", "Container Name");

    /// <summary>True for PL/SQL blocks and CALL statements (their elapsed time includes the SQL they run).</summary>
    public static bool IsPlsqlText(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("DECLARE", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("CALL", StringComparison.OrdinalIgnoreCase);
    }
}
