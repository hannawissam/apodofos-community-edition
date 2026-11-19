// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Globalization;
using System.Reflection;
using Apodofos.Core.Analysis.Rules;
using Apodofos.Core.Model;
using static Apodofos.Core.Analysis.AnalysisContext;

namespace Apodofos.Core.Analysis;

/// <summary>
/// Runs every <see cref="IRule"/> in this assembly (discovered by reflection, so new rule classes need no
/// registration), then builds the health summary, KPI tiles, charts and table payload for the UI.
/// </summary>
public static class AnalysisEngine
{
    public static string Version => typeof(AnalysisEngine).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";

    private static readonly Type[] RuleOrder =
    [
        typeof(LoadAndEfficiencyRules), typeof(WaitEventRules), typeof(SqlRules), typeof(IoAndMemoryRules), typeof(ConfigurationRules)
    ];

    public static IReadOnlyList<IRule> DiscoverRules()
    {
        var types = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => typeof(IRule).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false } && t.GetConstructor(Type.EmptyTypes) != null)
            .OrderBy(t => Array.IndexOf(RuleOrder, t) is var i && i >= 0 ? i : 100)
            .ThenBy(t => t.Name, StringComparer.Ordinal);
        return types.Select(t => (IRule)Activator.CreateInstance(t)!).ToList();
    }

    public static AnalysisResult Analyze(AwrReport report)
    {
        using var numberScope = Apodofos.Core.Parsing.NumberParser.Use(report.NumberConvention); // read cells the way the report prints them
        var ctx = new AnalysisContext(report);
        var warnings = new List<string>(report.ParseWarnings);
        warnings.AddRange(report.IntegrityWarnings);
        foreach (var rule in DiscoverRules().Where(r => r.Kinds.Contains(report.Kind)))
        {
            try { rule.Evaluate(ctx); }
            catch (Exception ex) { warnings.Add($"Rule '{rule.Name}' could not complete: {ex.Message}"); }
        }
        // Statspack has no DBA_HIST_* views or ASH: swap in the STATS$ equivalents and AWR wording.
        if (report.Kind == ReportKind.Statspack)
        {
            try { StatspackAdapter.Adapt(ctx); }
            catch (Exception ex) { warnings.Add($"Statspack adaptation could not complete: {ex.Message}"); }
        }

        // trust layer: cross-check key numbers (inconsistencies become a low-confidence finding, never an exception)
        var checks = new List<DataQualityAnalyzer.Check>();
        try { checks = DataQualityAnalyzer.CrossChecks(ctx); DataQualityAnalyzer.AddCrossCheckFinding(ctx, checks); }
        catch (Exception ex) { warnings.Add($"Cross-checks could not complete: {ex.Message}"); }

        // one central pass: licence caveats on AWR/ASH/tuning SQL, impossible values, very short windows
        foreach (var f in ctx.Findings) LicenceCaveats.Apply(f);
        try { DataQualityAnalyzer.GuardSeverities(ctx, ctx.Findings, checks); }
        catch (Exception ex) { warnings.Add($"Severity guard could not complete: {ex.Message}"); }

        var findings = ctx.Findings
            .OrderBy(f => f.Severity)
            .ThenByDescending(f => f.Impact)
            .ToList();
        for (var i = 0; i < findings.Count; i++) findings[i].Id = $"F{i + 1:000}";

        var charts = ChartBuilder.Build(ctx);
        var chartIds = charts.Select(c => c.Id).ToHashSet();
        foreach (var f in findings) f.ChartRefs = f.ChartRefs.Where(chartIds.Contains).Distinct().ToList();

        var topSql = report.Kind == ReportKind.Ash ? AshSummary.BuildTopSql(ctx) : SqlRules.BuildSqlSummaries(ctx);

        DataQualityReport quality;
        try { quality = DataQualityAnalyzer.Assess(ctx, findings, checks, warnings); }
        catch (Exception ex) { quality = new DataQualityReport { Level = "Fair", Notes = [$"Data quality could not be fully assessed: {ex.Message}"] }; }
        var rootCauses = RootCauseAnalyzer.Link(ctx, findings, topSql);
        var actionPlan = ActionPlanBuilder.Build(findings, rootCauses, report.Kind);
        var health = report.Kind == ReportKind.Ash ? AshSummary.BuildHealth(ctx, findings) : BuildHealth(ctx, findings);
        ApplyReliability(health, quality);

        Apodofos.Core.Knowledge.DocLinker.Link(findings, report.Kind); // offline Oracle documentation catalog (Knowledge/doc-catalog.json)

        return new AnalysisResult
        {
            Docs = Apodofos.Core.Knowledge.DocCatalog.ForKind(report.Kind).Select(Apodofos.Core.Knowledge.DocDto.From).ToList(),
            SectionDocs = Apodofos.Core.Knowledge.DocCatalog.BuildSectionIndex(report.Tables.Select(t => (t.Id, t.Title)), report.Kind),
            Meta = BuildMeta(report),
            Findings = findings,
            Health = health,
            Kpis = report.Kind == ReportKind.Ash ? AshSummary.BuildKpis(ctx) : BuildKpis(ctx),
            Charts = charts,
            Tables = report.Tables.Select(t => new TableDto { Id = t.Id, Title = t.Title, Section = t.Section, Summary = t.Summary, Headers = t.Headers, Rows = t.Rows }).ToList(),
            TopSql = topSql,
            DataQuality = quality,
            RootCauses = rootCauses,
            ActionPlan = actionPlan,
            Warnings = warnings
        };
    }

    /// <summary>A score from a 3-minute window, a truncated file or misread numbers is shown as "not reliable", never as a grade.</summary>
    private static void ApplyReliability(HealthSummary health, DataQualityReport quality)
    {
        if (string.IsNullOrEmpty(quality.HealthNote)) return;
        health.Reliable = false;
        health.ReliabilityNote = quality.HealthNote;
        health.Grade = "n/a";
        health.Headline = "Health score not reliable for this report.";
        health.Paragraphs.Insert(0, quality.HealthNote + " Read the findings as leads to verify, not as a verdict.");
    }

    private static ReportMeta BuildMeta(AwrReport r) => new()
    {
        SourceFile = Path.GetFileName(r.SourcePath),
        ReportType = r.Kind switch { ReportKind.Ash => "ASH", ReportKind.Statspack => "Statspack", _ => "AWR" },
        Format = r.Format.ToString(),
        DbName = r.Info.DbName,
        DbId = r.Info.DbId,
        Instance = r.Info.InstanceName,
        Release = r.Info.Release,
        Host = r.Info.HostName,
        Platform = r.Info.Platform,
        Cpus = r.Info.Cpus,
        Cores = r.Info.Cores,
        MemoryGb = r.Info.MemoryGb,
        Rac = r.Info.IsRac,
        Cdb = r.Info.IsCdb,
        BeginSnap = r.Info.BeginSnapId,
        EndSnap = r.Info.EndSnapId,
        BeginTime = r.Info.BeginSnapTime,
        EndTime = r.Info.EndSnapTime,
        ElapsedMinutes = r.Info.ElapsedMinutes,
        DbTimeMinutes = r.Info.DbTimeMinutes,
        AverageActiveSessions = r.Info.AverageActiveSessions,
        AnalyzedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        AnalyzerVersion = Version,
        NumberFormat = r.NumberFormatNote
    };

    private static HealthSummary BuildHealth(AnalysisContext ctx, List<Finding> findings)
    {
        var counts = Enum.GetValues<Severity>().ToDictionary(s => s.ToString(), s => findings.Count(f => f.Severity == s));
        var score = 100 - findings.Sum(f => f.Severity switch
        {
            Severity.Critical => 25,
            Severity.High => 10,
            Severity.Medium => 4,
            Severity.Low => 1,
            _ => 0
        });
        score = Math.Clamp(score, 0, 100);
        var grade = score switch { >= 90 => "A", >= 75 => "B", >= 60 => "C", >= 40 => "D", _ => "F" };
        var actionable = findings.Where(f => f.Severity <= Severity.Low).ToList();
        var top = actionable.FirstOrDefault();

        var headline = score switch
        {
            >= 90 => "The database looks healthy in this period.",
            >= 75 => "The database is in reasonable shape, with a few things worth improving.",
            >= 60 => "The database shows several performance problems that are worth fixing.",
            >= 40 => "The database has significant performance problems.",
            _ => "The database has serious performance problems that need urgent attention."
        };

        var paras = new List<string>();
        var i = ctx.Info;
        paras.Add($"This {(ctx.Report.Kind == ReportKind.Statspack ? "Statspack report" : "report")} covers {Dur(ctx.ElapsedSec)} of activity on database {i.DbName} (Oracle {i.Release}) on host {i.HostName}, from {i.BeginSnapTime} to {i.EndSnapTime}.");
        if (ctx.Aas is not null)
            paras.Add(ctx.ActiveSessionsSentence("user sessions"));
        var timeFinding = findings.FirstOrDefault(f => f.Title == "Where the database time went");
        if (timeFinding != null) paras.Add(timeFinding.Plain);
        paras.Add(actionable.Count == 0
            ? "No issues requiring action were found."
            : $"We found {actionable.Count} issue(s) to address: {counts["Critical"]} critical, {counts["High"]} high, {counts["Medium"]} medium and {counts["Low"]} low priority.");
        if (top != null) paras.Add($"The biggest opportunity: {top.Title}.");

        return new HealthSummary { Score = score, Grade = grade, Headline = headline, Paragraphs = paras, Counts = counts };
    }

    private static List<Kpi> BuildKpis(AnalysisContext ctx)
    {
        var k = new List<Kpi>();
        var ratio = ctx.Aas is { } a && ctx.Cpus is > 0 ? a / ctx.Cpus.Value : (double?)null;
        k.Add(new("aas", "Avg active sessions", N(ctx.Aas, 1), null, ratio >= 1 ? "bad" : ratio >= 0.7 ? "warn" : "good",
            ctx.Cpus is > 0 ? $"vs {ctx.Cpus} CPUs" : "DB time / elapsed time"));
        k.Add(new("dbtime", "DB time", Dur(ctx.DbTimeSec), null, "neutral", $"over {Dur(ctx.ElapsedSec)} elapsed"));
        var top = ctx.Report.TableStartingWith("Top 10 Foreground Events", "Top 5 Timed");
        var cpuPct = top?.RowByLabel("DB CPU")?.Num("% DB time");
        k.Add(new("dbcpu", "DB CPU share", P(cpuPct), null, "neutral", "of DB time spent on CPU"));
        var idle = ctx.Report.TableStartingWith("Host CPU")?.DataRows.FirstOrDefault().Num("%Idle");
        k.Add(new("hostcpu", "Host CPU busy", P(idle is null ? null : 100 - idle), null, idle < 10 ? "bad" : idle < 25 ? "warn" : "good", "all processes on the server"));
        k.Add(new("tps", "Transactions/s", N(ctx.LoadPerSec("Transactions"), 1), null, "neutral", "commits + rollbacks"));
        k.Add(new("execs", "Executions/s", Big(ctx.LoadPerSec("Executes (SQL)", "Executes")), null, "neutral", "SQL statements run per second"));
        k.Add(new("lio", "Logical reads/s", Big(ctx.LoadPerSec("Logical read (blocks)", "Logical reads")), "blocks", "neutral", "reads from memory"));
        k.Add(new("pio", "Physical reads/s", Big(ctx.LoadPerSec("Physical read (blocks)", "Physical reads")), "blocks", "neutral", "reads from disk"));
        var hard = ctx.LoadPerSec("Hard parses (SQL)", "Hard parses");
        k.Add(new("hardparse", "Hard parses/s", N(hard, 1), null, hard >= 100 ? "bad" : hard >= 10 ? "warn" : "good", "new SQL compilations"));
        k.Add(new("redo", "Redo/s", Bytes(ctx.LoadPerSec("Redo size (bytes)", "Redo size")), null, "neutral", "change log generated"));
        return k;
    }
}
