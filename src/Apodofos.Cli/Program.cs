// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text;
using Apodofos.Core.Analysis;
using Apodofos.Core.Export;
using Apodofos.Core.Model;
using Apodofos.Core.Parsing;
using Apodofos.Core.Providers;

namespace Apodofos.Cli;

internal static class Program
{
    private const int ExitOk = 0, ExitUsage = 1, ExitUnreadable = 2, ExitInvalid = 3, ExitError = 4;

    private const string UsageTemplate = """
        {NAME} (offline) - analyzes Oracle AWR, ASH and Statspack reports (HTML or text).

        Usage:
          {CLI} <report> [--html out.html] [--json out.json] [--script out.sql] [--text]

        Options:
          --html <file>     Write the self-contained interactive dashboard.
          --json <file>     Write the full analysis result as JSON.
          --script <file>   Write all finding SQL as one commented script.
          --text            Print a readable summary to the console.
          --license         Show copyright, licence (AGPL-3.0-only), warranty and trademark notices.
          --version         Show the version.
          --help, -h        Show this help.

        With no output option, --text is assumed.
        Exit codes: 0 ok, 1 usage error, 2 file unreadable, 3 not a valid AWR report, 4 other error.
        """;

    private static string Usage => UsageTemplate
        .Replace("{NAME}", Apodofos.Host.ProductInfo.Current.Name, StringComparison.Ordinal)
        .Replace("{CLI}", Apodofos.Host.ProductInfo.Current.CliName, StringComparison.Ordinal);

    private static int Main(string[] args)
    {
        string? report = null, html = null, json = null, script = null;
        bool text = false, dump = false;
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a.ToLowerInvariant())
                {
                    case "--help" or "-h" or "-?" or "/?": Console.WriteLine(Usage); return ExitOk;
                    case "--version": Console.WriteLine(VersionLine()); return ExitOk;
                    case "--license": Console.WriteLine(LicenseText()); return ExitOk;
                    case "--text": text = true; break;
                    case "--dump-tables": dump = true; break;
                    case "--html": html = Next(args, ref i, a); break;
                    case "--json": json = Next(args, ref i, a); break;
                    case "--script": script = Next(args, ref i, a); break;
                    default:
                        if (a.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option '{a}'.");
                        if (report != null) throw new ArgumentException("Only one report file can be given.");
                        report = a;
                        break;
                }
            }
            if (report == null) throw new ArgumentException("No report file given.");
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return ExitUsage;
        }

        ProviderResult? provided = null;
        AwrReport? loaded = null;
        try
        {
            if (dump) loaded = AwrReportLoader.LoadFile(report);
            else provided = ReportProviderRegistry.AnalyzeFile(report);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Error: cannot read '" + report + "': " + ex.Message);
            return ExitUnreadable;
        }
        catch (ReportFormatException ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return ExitInvalid;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: the report could not be parsed: " + ex.Message);
            return ExitInvalid;
        }

        if (dump) { DumpTables(loaded!); return ExitOk; }

        try
        {
            var result = provided!.Analysis;
            if (html != null) WriteFile(html, DashboardBuilder.Build(result, provided.ReportHtml, DashboardMode.Export));
            if (json != null) WriteFile(json, DashboardBuilder.ToJson(result, null, DashboardMode.Export));
            if (script != null) WriteFile(script, BuildScript(result));
            if (text || (html == null && json == null && script == null)) PrintSummary(result);
            return ExitOk;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return ExitError;
        }
    }

    internal static string VersionString()
    {
        var v = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
            ?? typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        var plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }

    internal static string VersionLine() => Apodofos.Host.ProductInfo.Current.VersionLine(VersionString());

    internal static string LicenseText() => string.Join(Environment.NewLine,
        VersionLine(),
        "Copyright (c) 2026 Wissam HANNA",
        "License: GNU AGPL v3.0 only (AGPL-3.0-only). This is free software: you are free to change and redistribute it.",
        "There is NO WARRANTY, to the extent permitted by law.",
        "Source code and license: https://github.com/hannawissam/apodofos-community-edition",
        "Third-party notices: see THIRD-PARTY-NOTICES.md",
        "Apodofos is a trademark of Wissam HANNA (French trade mark application no. 5322937, not yet registered). The name and logo are not licensed under the AGPL: see TRADEMARK.md.",
        "Apodofos is not an Oracle product and is not affiliated with, sponsored by or endorsed by Oracle. Oracle and all Oracle product names are trademarks or registered trademarks of Oracle Corporation and/or its affiliates.");

    private static string Next(string[] args, ref int i, string opt)
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Option {opt} needs a file name.");
        return args[++i];
    }

    private static void WriteFile(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        Console.Error.WriteLine("Wrote " + Path.GetFullPath(path));
    }

    internal static string BuildScript(AnalysisResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- Apodofos Report Analyzer - SQL for findings");
        sb.AppendLine($"-- Database: {r.Meta.DbName}  Instance: {r.Meta.Instance}  Snapshots: {r.Meta.BeginSnap}-{r.Meta.EndSnap}");
        sb.AppendLine("-- Review every statement before running it. Statements marked CAUTION change the system.");
        sb.AppendLine();
        foreach (var f in r.Findings.Where(f => f.Sql.Count > 0))
        {
            sb.AppendLine("-- " + new string('=', 70));
            sb.AppendLine($"-- {f.Id} [{f.Severity}] {f.Title}");
            sb.AppendLine("-- " + new string('=', 70));
            foreach (var s in f.Sql)
            {
                sb.AppendLine("-- " + s.Title);
                if (!string.IsNullOrWhiteSpace(s.Caution))
                    foreach (var line in s.Caution.Split('\n')) sb.AppendLine("-- CAUTION: " + line.TrimEnd('\r'));
                sb.AppendLine(s.Code.TrimEnd());
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private static void PrintSummary(AnalysisResult r)
    {
        var m = r.Meta;
        Console.WriteLine();
        Colored($"{m.ReportType} analysis: {m.DbName}{(string.IsNullOrWhiteSpace(m.Instance) || m.Instance == m.DbName ? "" : $" ({m.Instance})")} {m.Release}", ConsoleColor.Cyan);
        Console.WriteLine((m.ReportType == "ASH" ? "Sample window" : $"Snapshots {m.BeginSnap}-{m.EndSnap}") + $"   {m.BeginTime} -> {m.EndTime}");
        Console.WriteLine($"Elapsed {Fmt(m.ElapsedMinutes)} min   {(m.ReportType == "ASH" ? "Active session time" : "DB time")} {Fmt(m.DbTimeMinutes)} min   Avg active sessions {Fmt(m.AverageActiveSessions)}");
        Console.WriteLine();
        if (!string.IsNullOrWhiteSpace(m.NumberFormat)) Console.WriteLine(m.NumberFormat);
        foreach (var alert in r.DataQuality.Alerts) Colored("WARNING: " + alert, ConsoleColor.Red);
        if (r.DataQuality.Alerts.Count > 0) Console.WriteLine();
        if (!r.Health.Reliable) Colored($"Health: not reliable ({r.Health.ReliabilityNote})", ConsoleColor.Yellow);
        else Colored($"Health: {r.Health.Score}/100  grade {r.Health.Grade}  - {r.Health.Headline}", ScoreColor(r.Health.Score));
        foreach (var p in r.Health.Paragraphs) Console.WriteLine("  " + p);
        Console.WriteLine();

        if (r.ActionPlan.Count > 0)
        {
            Colored("Fix first (ranked by expected payoff):", ConsoleColor.White);
            foreach (var a in r.ActionPlan)
            {
                Console.WriteLine($"  {a.Rank}. {a.Action}");
                Console.WriteLine($"      Impact: {a.ExpectedImpact}   Effort: {a.Effort}   Risk: {a.Risk}   Confidence: {a.Confidence}   Findings: {string.Join(", ", a.FindingIds)}");
            }
            Console.WriteLine();
        }
        if (r.DataQuality.Notes.Count > 0)
        {
            Colored($"Data quality: {r.DataQuality.Level}", r.DataQuality.Level == "Good" ? ConsoleColor.Green : r.DataQuality.Level == "Fair" ? ConsoleColor.Yellow : ConsoleColor.Red);
            foreach (var n in r.DataQuality.Notes) Console.WriteLine("  - " + n);
            Console.WriteLine();
        }
        if (r.RootCauses.Count > 0)
        {
            Colored("Root causes (related findings):", ConsoleColor.White);
            foreach (var g in r.RootCauses) Console.WriteLine($"  {g.Id} {g.Title}: {string.Join(", ", g.FindingIds)}");
            Console.WriteLine();
        }

        var real = r.Findings.Where(f => f.Severity != Severity.Ok).ToList();
        Colored($"Findings ({real.Count}):", ConsoleColor.White);
        foreach (var f in real.Take(15))
        {
            Console.Write("  ");
            Colored($"[{f.Severity,-8}]", SevColor(f.Severity), false);
            Console.WriteLine($" {f.Id} {f.Title}" + (f.Confidence != Confidence.High ? $"  (confidence: {f.Confidence})" : ""));
            if (!string.IsNullOrWhiteSpace(f.DataQuality)) Console.WriteLine("      Data quality: " + f.DataQuality);
            if (f.RelatedIds.Count > 0) Console.WriteLine($"      Related ({f.RootCauseGroup}): {string.Join(", ", f.RelatedIds)}");
            if (!string.IsNullOrWhiteSpace(f.Plain)) Console.WriteLine("      " + f.Plain);
            foreach (var rec in f.Recommendations.Take(3)) Console.WriteLine("      -> " + rec);
        }
        if (real.Count > 15) Console.WriteLine($"  ... and {real.Count - 15} more (use --html or --json for everything).");
        var ok = r.Findings.Count - real.Count;
        if (ok > 0) Console.WriteLine($"  {ok} check(s) passed.");
        if (r.Warnings.Count > 0)
        {
            Console.WriteLine();
            Colored("Notes:", ConsoleColor.DarkYellow);
            foreach (var w in r.Warnings) Console.WriteLine("  - " + w);
        }
        Console.WriteLine();
    }

    private static string Fmt(double? v) => v is { } d ? d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "n/a";

    private static ConsoleColor ScoreColor(int s) => s >= 80 ? ConsoleColor.Green : s >= 60 ? ConsoleColor.Yellow : ConsoleColor.Red;

    private static ConsoleColor SevColor(Severity s) => s switch
    {
        Severity.Critical => ConsoleColor.Red,
        Severity.High => ConsoleColor.DarkRed,
        Severity.Medium => ConsoleColor.Yellow,
        Severity.Low => ConsoleColor.DarkYellow,
        Severity.Info => ConsoleColor.Cyan,
        _ => ConsoleColor.Green
    };

    private static void Colored(string text, ConsoleColor c, bool newline = true)
    {
        var useColor = !Console.IsOutputRedirected;
        if (useColor) Console.ForegroundColor = c;
        if (newline) Console.WriteLine(text); else Console.Write(text);
        if (useColor) Console.ResetColor();
    }

    private static void DumpTables(AwrReport report)
    {
        foreach (var t in report.Tables)
        {
            Console.WriteLine($"[{t.Id}] {t.Title} | sec={t.Section} | rows={t.Rows.Count}");
            Console.WriteLine("    H: " + string.Join(" | ", t.Headers));
            if (t.Rows.Count > 0) Console.WriteLine("    R: " + string.Join(" | ", t.Rows[0]));
        }
    }
}
