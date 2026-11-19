// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Core.Analysis.Rules;
using Apodofos.Core.Model;
using Apodofos.Core.Parsing;
using static Apodofos.Core.Analysis.AnalysisContext;

namespace Apodofos.Core.Analysis;

/// <summary>
/// Trust layer on top of the rules: cross-checks key numbers against each other, assesses report-level data quality
/// (short window, idle database, few samples, missing sections) and derives a per-finding confidence.
/// Everything here is tolerant: missing data skips a check, it never throws or blocks the analysis.
/// </summary>
internal static class DataQualityAnalyzer
{
    /// <summary>One inconsistency between two numbers of the report.</summary>
    internal sealed record Check(string Key, string Label, string Detail, bool AffectsShares, bool Impossible = false);

    private const string CrossCheckTitle = "Some numbers in this report do not agree with each other";

    // categories whose findings do not depend on how long or how busy the window was
    private static readonly HashSet<string> WindowIndependent = new(StringComparer.OrdinalIgnoreCase)
        { "Configuration", "Report scope", "Report quality", "Statspack" };

    // ------------------------------------------------------------------ cross checks

    public static List<Check> CrossChecks(AnalysisContext ctx)
    {
        var checks = new List<Check>(ctx.SanityChecks); // impossible values (shares above 100%) apply to every report kind
        if (ctx.Report.Kind == ReportKind.Ash) return checks;
        void Try(Action a) { try { a(); } catch { /* a failing check must never break the analysis */ } }
        var r = ctx.Report;
        var el = ctx.ElapsedSec;
        var db = ctx.DbTimeSec;

        if (el is null or <= 0)
        {
            checks.Add(new("elapsed", "Elapsed time", "The snapshot window length is missing or zero, so rates and averages could not be validated.", true));
            return checks;
        }

        // 1. DB time cannot exceed elapsed time x sessions that could have been active
        Try(() =>
        {
            var sessions = Math.Max(ctx.Info.BeginSessions ?? 0, ctx.Info.EndSessions ?? 0);
            var logons = (ctx.LoadPerSec("Logons") ?? 0) * el.Value; // sessions that came and went inside the window
            if (db > 0 && sessions > 0 && db > el.Value * (sessions + logons) * 1.5)
                checks.Add(new("dbtime-sessions", "DB time vs elapsed x sessions",
                    $"DB time is {Dur(db)} but {N(sessions)} sessions over {Dur(el)} can account for at most {Dur(el.Value * (sessions + logons))}.", true));
        });

        // 2. time model "DB time" must match the header DB time
        Try(() =>
        {
            var tm = ctx.TimeModelSec("DB time");
            if (tm > 0 && db > 60 && Math.Abs(tm.Value - db.Value) / Math.Max(tm.Value, db.Value) > 0.10)
                checks.Add(new("timemodel-dbtime", "Load Profile / header vs time model",
                    $"DB time is {Dur(db)} in the report header but {Dur(tm)} in the Time Model.", true));
        });

        // 3. Load Profile "DB Time(s)" per second is the average active sessions
        Try(() =>
        {
            var lp = ctx.LoadPerSec("DB Time(s)", "DB time");
            if (lp is { } v && ctx.Aas is { } aas && Math.Abs(v - aas) > Math.Max(0.2 * Math.Max(v, aas), 0.15))
                checks.Add(new("loadprofile-aas", "Load Profile vs DB time / elapsed",
                    $"The Load Profile says {N(v, 1)} DB seconds per second, but DB time / elapsed time gives {N(aas, 1)} average active sessions.", true));
        });

        // 4. wait-class shares add up to about 100%
        Try(() =>
        {
            // Only the foreground table adds up to 100% of DB time: "Wait Classes by Total Wait Time" also lists background
            // waits, whose time is not DB time, so its shares legitimately exceed 100%.
            var wc = r.Table("Foreground Wait Class");
            if (wc == null) return;
            var sum = wc.DataRows.Where(x => !x.Label.Trim().Equals("DB CPU", StringComparison.OrdinalIgnoreCase)).Sum(x => x.Num("% DB time", "%DB time") ?? 0); // DB CPU overlaps wait time slightly in real reports
            if (sum > 105) checks.Add(new("waitclass-sum", "Sum of wait-class %", $"The wait classes add up to {P(sum, 1)} of DB time (should be at most about 100%).", true));
        });

        // 5. top events: no single event above 100%, and the list sums to at most about 100%
        Try(() =>
        {
            var top = r.TableStartingWith("Top 10 Foreground Events", "Top 5 Timed");
            if (top == null) return;
            var pcts = top.DataRows.Select(x => x.Num("% DB time", "%DB time", "% Total Call Time")).Where(x => x is not null).Select(x => x!.Value).ToList();
            var waitSum = top.DataRows.Where(x => !x.Label.Trim().Equals("DB CPU", StringComparison.OrdinalIgnoreCase)).Sum(x => x.Num("% DB time", "%DB time", "% Total Call Time") ?? 0);
            if (pcts.Count > 0 && (pcts.Max() > 100.5 || waitSum > 105))
                checks.Add(new("topevents-sum", "Top events share", $"The top wait events add up to {P(waitSum, 1)} of DB time (largest single event {P(pcts.Max(), 1)}).", true));
        });

        // 6. SQL share of DB time cannot be above 100%
        Try(() =>
        {
            foreach (var title in new[] { "SQL ordered by Elapsed Time", "SQL ordered by CPU Time" })
            {
                var t = r.Table(title);
                var worst = t?.DataRows.Select(x => x.Num("%Total")).Where(x => x is not null).DefaultIfEmpty(0).Max() ?? 0;
                if (worst > 100.5) { checks.Add(new("sql-share", "Top SQL share vs DB time", $"'{title}' lists a statement with {P(worst, 1)} of DB time.", true)); return; }
            }
        });

        // 7. DB CPU cannot exceed the CPU capacity of the host
        Try(() =>
        {
            var cpuSec = ctx.TimeModelSec("DB CPU");
            if (cpuSec > 0 && ctx.Cpus is > 0 && cpuSec > ctx.Cpus.Value * el.Value * 1.15)
                checks.Add(new("dbcpu-capacity", "AAS / DB CPU vs CPU count",
                    $"Oracle used {Dur(cpuSec)} of CPU in {Dur(el)}, which is more than {ctx.Cpus} CPUs can deliver. The CPU count in the report may not match the real capacity (virtualization, CPU hot-add or cgroup limits).", false));
        });

        // 8. DB CPU share: top events table vs time model
        Try(() =>
        {
            var top = r.TableStartingWith("Top 10 Foreground Events", "Top 5 Timed");
            var a = top?.RowByLabel("DB CPU", "CPU time").Val("% DB time", "%DB time", "% Total Call Time");
            var b = ctx.TimeModelPct("DB CPU");
            if (a is { } x && b is { } y && Math.Abs(x - y) > 15)
                checks.Add(new("dbcpu-share", "Top events vs time model", $"DB CPU is {P(x, 1)} of DB time in the top events but {P(y, 1)} in the Time Model.", true));
        });
        return checks;
    }

    /// <summary>Adds one low-confidence Info finding that lists all inconsistencies.</summary>
    public static void AddCrossCheckFinding(AnalysisContext ctx, List<Check> checks)
    {
        if (checks.Count == 0) return;
        ctx.Add(new Finding
        {
            Severity = Severity.Info,
            Category = "Report quality",
            Title = CrossCheckTitle,
            Plain = "Some headline numbers of this report disagree with each other. The analysis keeps going, but the findings that depend on those numbers should be treated with care.",
            WhyItMatters = "A report cut from a very short window, taken across an instance restart, or with a wrong CPU count can mislead any tool. Verifying the snapshot range avoids chasing a problem that is only a measuring artefact.",
            Technical = "Cross-checks: " + string.Join(" ", checks.Select(c => c.Detail)),
            Evidence = checks.Select(c => new Evidence(c.Label, "inconsistent", c.Detail)).ToList(),
            Recommendations =
            [
                "Check that the begin and end snapshots belong to the same instance startup (DBA_HIST_SNAPSHOT.STARTUP_TIME) and that no snapshot was missed.",
                "Regenerate the report for a window of at least 10-15 minutes inside a single instance run, then compare."
            ],
            Sql = [new("Snapshots of the window and their instance startup times", """
                SELECT snap_id, begin_interval_time, end_interval_time, startup_time
                FROM   dba_hist_snapshot
                WHERE  snap_id BETWEEN &begin_snap AND &end_snap
                AND    instance_number = &inst_num
                ORDER  BY snap_id;
                """)],
            Docs = [Apodofos.Core.Knowledge.OracleDocs.Awr],
            SourceViews = ["DBA_HIST_SNAPSHOT"],
            Confidence = Confidence.Low,
            DataQuality = "Cross-check failed: " + string.Join("; ", checks.Select(c => c.Label))
        });
    }

    // ------------------------------------------------------------------ report-level assessment + confidence

    public static DataQualityReport Assess(AnalysisContext ctx, List<Finding> findings, List<Check> checks, List<string> warnings)
    {
        var report = new DataQualityReport();
        var notes = report.Notes;
        var poor = 0;
        var fair = 0;
        var r = ctx.Report;
        var kind = r.Kind;
        var per = new List<(Confidence Cap, string Reason, Func<Finding, bool> Applies)>();
        bool Measured(Finding f) => !WindowIndependent.Contains(f.Category) && f.Title != CrossCheckTitle;

        // ---- window length (AWR / Statspack)
        var minutes = ctx.Info.ElapsedMinutes;
        var healthNotes = new List<string>();
        if (kind != ReportKind.Ash || minutes is < 5)
        {
            if (minutes is null or <= 0) { fair++; notes.Add("The length of the snapshot window is unknown, so rates and averages cannot be validated."); }
            else if (minutes < 5)
            {
                poor++;
                notes.Add($"Very short window ({N(minutes, 1)} minutes): rates, ratios and shares are statistically weak. Use a window of at least 10-15 minutes.");
                per.Add((Confidence.Low, $"Very short window ({N(minutes, 1)} min)", Measured));
                healthNotes.Add($"the window is only {N(minutes, 1)} minutes long, too short for a meaningful health score");
            }
            else if (minutes < 10)
            {
                fair++;
                notes.Add($"Short window ({N(minutes, 1)} minutes): percentages may be skewed by a single burst. 10-60 minutes is the usual range.");
                per.Add((Confidence.Medium, $"Short window ({N(minutes, 1)} min)", Measured));
            }
            else if (minutes > 24 * 60)
                notes.Add($"Long window ({Dur(minutes * 60)}): short spikes are averaged out; look at shorter reports (or ASH) for peak periods.");
        }

        // ---- idle / light database
        if (kind != ReportKind.Ash && (ctx.Aas is < 0.2 || ctx.DbTimeSec is < 120))
        {
            poor++;
            notes.Add($"The database was nearly idle (about {N(ctx.Aas, 2)} average active sessions, {Dur(ctx.DbTimeSec)} of DB time): percentages describe a tiny amount of work.");
            per.Add((Confidence.Low, "Nearly idle database: shares of DB time are based on very little activity", f => Measured(f) && f.Severity != Severity.Ok));
        }
        else if (kind != ReportKind.Ash && ctx.Aas is < 1.0)
        {
            fair++;
            notes.Add($"Light load ({N(ctx.Aas, 2)} average active sessions): shares of DB time are real but the absolute impact is small.");
            per.Add((Confidence.Medium, "Light load: absolute impact is small", f => Measured(f) && f.Severity != Severity.Ok && f.Impact > 0));
        }

        // ---- ASH sample counts
        if (kind == ReportKind.Ash)
        {
            var samples = ctx.Info.AshSampleCount;
            if (samples is < 100)
            {
                poor++;
                notes.Add($"Only {N(samples)} ASH samples: percentages of sampled activity are unreliable. Widen the time window.");
                per.Add((Confidence.Low, $"Only {N(samples)} samples", f => f.Category != "Report quality"));
            }
            else if (samples is < 1000)
            {
                fair++;
                notes.Add($"{N(samples)} ASH samples: enough for the main trends, thin for small percentages.");
                per.Add((Confidence.Medium, $"{N(samples)} samples", f => f.Category != "Report quality" && f.Impact is > 0 and < 15));
            }
            if (ctx.Info.AshFromHistory)
                notes.Add("Data comes from the ASH history (DBA_HIST_ACTIVE_SESS_HISTORY, one sample in ten): short events are under-represented.");
        }

        // ---- missing core sections
        // PDB-level reports legitimately lack the instance-wide sections
        var pdbReport = RuleHelpers.IsPdbReport(ctx);
        var pdbAbsent = new[] { "Instance Efficiency", "Host CPU", "init.ora Parameters" };
        var missing = CoverageMap.CoreSections(kind)
            .Where(c => !(pdbReport && pdbAbsent.Contains(c.Name)))
            .Where(c => !c.Prefixes.Any(p => r.Tables.Any(t => t.TitleStartsWith(p)))).Select(c => c.Name).ToList();
        if (missing.Count > 0)
        {
            if (missing.Count >= 3) poor++; else fair++;
            notes.Add($"Core section(s) missing from the report: {string.Join(", ", missing)}. Checks that need them were skipped" +
                      (kind == ReportKind.Statspack ? " (a higher Statspack snapshot level or a newer release prints more sections)." : "."));
            if (missing.Count >= 3) per.Add((Confidence.Medium, $"{missing.Count} core sections missing from the report", Measured));
        }

        // ---- partial / unparsed content
        var parseIssues = r.ParseWarnings.Where(w => !w.StartsWith("Report is from Oracle", StringComparison.OrdinalIgnoreCase) && !w.Contains("estimated", StringComparison.OrdinalIgnoreCase)).ToList();
        if (parseIssues.Count > 0)
        {
            fair++;
            notes.Add($"The report was only partly understood ({parseIssues.Count} parser note(s): {string.Join(" | ", parseIssues.Take(2))}).");
        }
        if (kind != ReportKind.Ash && r.Tables.Count is > 0 and < 8)
        {
            fair++;
            notes.Add($"Only {r.Tables.Count} tables were found: this looks like a partial or truncated report.");
        }
        if (warnings.Any(w => w.Contains("estimated", StringComparison.OrdinalIgnoreCase) && w.Contains("DB time", StringComparison.OrdinalIgnoreCase)))
        {
            fair++;
            notes.Add("DB time was estimated from the top events (this Statspack report does not print it), so every share of DB time is approximate.");
            per.Add((Confidence.Medium, "DB time was estimated", f => Measured(f) && f.Impact > 0));
        }

        // ---- cross-check failures
        if (checks.Count > 0)
        {
            if (checks.Count >= 2) poor++; else fair++;
            notes.Add($"{checks.Count} cross-check(s) failed: {string.Join("; ", checks.Select(c => c.Label))}. Findings that rely on DB time shares were lowered in confidence.");
            var cap = checks.Count >= 2 || checks.Any(c => c.Impossible) ? Confidence.Low : Confidence.Medium;
            if (checks.Any(c => c.AffectsShares))
                per.Add((cap, "Cross-check failed: " + string.Join("; ", checks.Select(c => c.Label)), f => Measured(f) && f.Impact > 0));
        }

        // ---- number format of the report
        if (r.NumberConventionAmbiguous)
        {
            poor++;
            var msg = "Number format could not be determined: " + r.NumberFormatNote + " Values may be off by a factor of 10 or 1000, so no conclusion was escalated.";
            notes.Add(msg);
            report.Alerts.Add(msg);
            healthNotes.Add("the number format of the report is ambiguous");
            per.Add((Confidence.Low, "Ambiguous number format: values may be misread", Measured));
        }
        else if (r.NumberConvention == NumberConvention.European || !r.NumberFormatNote.Contains("comma thousands", StringComparison.Ordinal))
            notes.Add(r.NumberFormatNote + " This is not the standard US/UK layout; locale-formatted reports are less tested.");

        // ---- truncated / damaged report
        if (r.Truncated)
        {
            poor++;
            report.Alerts.Add(r.TruncationReason);
            notes.Add(r.TruncationReason);
            healthNotes.Add("the report is truncated");
            per.Add((Confidence.Low, "Truncated report: totals and rankings may be incomplete", Measured));
        }
        else foreach (var w in r.IntegrityWarnings) { fair++; notes.Add(w); }

        // ---- internal consistency of the header
        var consistency = ReportIntegrity.Consistency(r);
        if (consistency.Count > 0)
        {
            if (consistency.Count >= 2) poor++; else fair++;
            foreach (var c in consistency) { notes.Add(c); if (!warnings.Contains(c)) warnings.Add(c); }
            if (consistency.Count >= 2) healthNotes.Add("the report header is inconsistent");
            per.Add((Confidence.Medium, "Report header is inconsistent: " + consistency[0], Measured));
        }

        // ---- impossible values
        var impossible = checks.Where(c => c.Impossible).ToList();
        if (impossible.Count > 0)
        {
            poor++;
            report.Alerts.Add("This report contains values that cannot be true (a share above 100%). They were ignored and nothing was concluded from them; the report may use an unexpected number format, come from a damaged file or from an unsupported Oracle release. " + string.Join(" ", impossible.Select(c => c.Detail)));
            healthNotes.Add("the report contains impossible values");
        }

        // ---- context notes (do not change the level)
        if (ctx.Info.IsRac) notes.Add("RAC: this report covers one instance only; cluster-wide problems may be incomplete.");
        if (RuleHelpers.IsPdbReport(ctx)) notes.Add("PDB-level report: CDB-wide sections are absent and some parameters are inherited from the root container.");
        if (ctx.Info.MajorVersion is { } mv && mv < 12 && kind != ReportKind.Statspack)
            notes.Add($"Oracle {ctx.Info.Release}: some sections introduced in 12c+ are absent, so a few checks could not run.");

        // ---- apply confidence to findings
        foreach (var f in findings)
        {
            var conf = f.Confidence;
            var reasons = new List<string>();
            if (!string.IsNullOrWhiteSpace(f.DataQuality)) reasons.Add(f.DataQuality);
            foreach (var (cap, reason, applies) in per)
            {
                if (!applies(f)) continue;
                if (cap > conf) conf = cap;
                reasons.Add(reason);
            }
            f.Confidence = conf;
            f.DataQuality = conf == Confidence.High ? "" : string.Join("; ", reasons.Distinct());
        }

        if (findings.Count(f => f.Confidence != Confidence.High) is var reduced && reduced > 0)
            notes.Add($"{reduced} of {findings.Count} finding(s) carry reduced confidence (see each finding's data-quality note).");

        report.Level = poor > 0 ? "Poor" : fair > 0 ? "Fair" : "Good";
        if (healthNotes.Count > 0)
        {
            report.HealthNote = "The health score is not reliable: " + string.Join("; ", healthNotes) + ".";
            report.Alerts.Insert(0, report.HealthNote);
        }
        if (notes.Count == 0) notes.Add("No data-quality concerns: the window is long enough, the database was busy enough and the core sections are present.");
        return report;
    }

    // ------------------------------------------------------------------ severity guard

    private static readonly System.Text.RegularExpressions.Regex ShareOfTime = new(
        @"(?<n>\d[\d,]*(?:\.\d+)?)\s*%(?<tail>\s+of\s+(?:the\s+)?(?:total\s+|all\s+)?(?:DB\s+time|database\s+time|sampled\s+activity|active\s+session\s+time|total\s+call\s+time))",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Replaces "1,419% of DB time" by "an invalid share of DB time": an impossible number is never printed as fact.</summary>
    internal static string ScrubImpossible(string text, out bool changed)
    {
        var hit = false;
        var result = ShareOfTime.Replace(text ?? "", m =>
        {
            var v = Parsing.NumberParser.Parse(m.Groups["n"].Value, Parsing.NumberConvention.Invariant);
            if (v is not > 100.5) return m.Value;
            hit = true;
            return "an invalid share" + m.Groups["tail"].Value;
        });
        changed = hit;
        return result;
    }

    /// <summary>
    /// Runs after the rules and before ranking. (1) A finding whose impact is above 100% of DB time rests on an impossible
    /// number: it is clamped, never rated above Info and flagged. (2) When the report contains impossible values, such
    /// percentages are removed from the text of every finding. (3) A window under 5 minutes caps measured findings at Medium.
    /// </summary>
    public static void GuardSeverities(AnalysisContext ctx, List<Finding> findings, List<Check> checks)
    {
        var impossibleReport = checks.Any(c => c.Impossible);
        var veryShort = ctx.Info.ElapsedMinutes is > 0 and < 5;
        var ambiguousFormat = ctx.Report.NumberConventionAmbiguous;
        foreach (var f in findings)
        {
            var bad = false;
            if (f.Impact > 100.5) { f.Impact = 100; bad = true; }
            if (impossibleReport)
            {
                f.Title = ScrubImpossible(f.Title, out var c1); f.Plain = ScrubImpossible(f.Plain, out var c2);
                f.Technical = ScrubImpossible(f.Technical, out var c3); f.WhyItMatters = ScrubImpossible(f.WhyItMatters, out var c4);
                bad |= c1 | c2 | c3 | c4;
                for (var i = 0; i < f.Recommendations.Count; i++) { f.Recommendations[i] = ScrubImpossible(f.Recommendations[i], out var cr); bad |= cr; }
                for (var i = 0; i < f.Evidence.Count; i++)
                {
                    var e = f.Evidence[i];
                    var v = ScrubImpossible(e.Value, out var ce1);
                    var n = e.Note is null ? null : ScrubImpossible(e.Note, out var ce2);
                    if (ce1 || (n != e.Note)) { f.Evidence[i] = e with { Value = v, Note = n }; bad = true; }
                    else if (Parsing.NumberParser.Parse(e.Value, Parsing.NumberConvention.Invariant) is > 100.5 && e.Value.TrimEnd().EndsWith('%') && e.Label.Contains("share", StringComparison.OrdinalIgnoreCase))
                    { f.Evidence[i] = e with { Value = "invalid (above 100%)" }; bad = true; }
                }
            }
            if (ambiguousFormat && f.Severity < Severity.Low && !WindowIndependent.Contains(f.Category) && f.Title != CrossCheckTitle)
            {
                f.Severity = Severity.Low; // the numbers behind it may be misread: never escalate
                if (f.Confidence < Confidence.Low) f.Confidence = Confidence.Low;
                f.DataQuality = "Number format could not be determined, so this value may be misread. " + f.DataQuality;
            }
            if (bad)
            {
                if (f.Severity < Severity.Info) f.Severity = Severity.Info;
                if (f.Confidence < Confidence.Low) f.Confidence = Confidence.Low;
                f.DataQuality = "Based on an impossible value (a share above 100%): not used for ranking. " + f.DataQuality;
                f.Impact = Math.Min(f.Impact, 0);
            }
            else if (veryShort && f.Severity < Severity.Medium && !WindowIndependent.Contains(f.Category) && f.Title != CrossCheckTitle)
                f.Severity = Severity.Medium; // a window under 5 minutes can never justify High or Critical
        }
    }
}
