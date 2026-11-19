// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text.RegularExpressions;
using static QuietLoad.Core.Analysis.AnalysisContext;

namespace QuietLoad.Core.Analysis;

/// <summary>
/// Links findings that describe the same underlying problem from different angles (a wait event, the SQL that causes it,
/// the hot segment it hits) so the reader fixes one cause instead of reading five symptoms.
/// Heuristic and conservative: a group needs at least one symptom and two members, otherwise findings stay alone.
/// </summary>
internal static class RootCauseAnalyzer
{
    private sealed record Theme(string Key, string Title, string Chain, Func<string, Finding, bool> IsSymptom);

    private static bool Has(string text, params string[] fragments) => fragments.Any(f => text.Contains(f, StringComparison.Ordinal));

    private static readonly Theme[] Themes =
    [
        new("rowlock", "Row lock contention", "sessions wait for locked rows -> the DML statements that hold the locks -> the hot table or index",
            (t, f) => Has(t, "row lock", "locked row", "enq: tx - row", "enq: tm", "enq: ul", "application-defined lock")),
        new("hotblock", "Hot data blocks", "many sessions touch the same blocks -> the statements that do it -> the hot segment",
            (t, f) => Has(t, "buffer busy", "cache buffers chains", "index contention", "transaction slot", "allocate itl", "read by other session", "hot data in memory", "fighting over the same data block")),
        new("commit", "Commit and redo pressure", "commits wait for the redo log -> log writer or log switch -> redo configuration",
            (t, f) => Has(t, "log file sync", "log file switch", "log file parallel write", "redo logs switch", "commit to be saved", "redo log to switch")),
        new("parsing", "Parsing and cursor sharing", "statements are compiled again and again -> library cache and shared pool pressure -> connection churn",
            (t, f) => f.Category is "Parsing" or "Connections" || Has(t, "library cache", "cursor: pin", "cursor: mutex", "re-parsed", "hard pars", "connect and disconnect", "shared pool")),
        new("io", "Disk I/O load", "sessions wait for disk -> the SQL that reads the most -> slow files or tablespaces",
            (t, f) => f.Category == "I/O" || Has(t, "db file sequential read", "db file scattered read", "direct path read", "direct path write", "reading single blocks", "reading data directly from disk", "read in full again and again", "slow even if the average")),
        new("cpu", "CPU demand", "sessions need more CPU than is available -> the statements that burn it -> the objects they read",
            (t, f) => f.Category == "CPU" || Has(t, "cpu headroom", "cpu is comfortable", "wanting cpu", "than the server has cpus", "parallel queries ran on a small server", "spent working on the cpu", "database time was mostly spent on cpu"))
    ];

    private static readonly Regex SqlIdInTitle = new(@"\bSQL ([0-9a-z]{13})\b", RegexOptions.Compiled);
    private static readonly Regex DmlStart = new(@"^\s*(UPDATE|DELETE|MERGE|INSERT)\b|\bFOR\s+UPDATE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<RootCauseGroup> Link(AnalysisContext ctx, List<Finding> findings, List<SqlSummary> topSql)
    {
        var groups = new List<RootCauseGroup>();
        try
        {
            var live = findings.Where(f => f.Severity <= Severity.Info && f.Category is not ("Report scope" or "Report quality" or "Statspack")).ToList();
            var members = Themes.ToDictionary(t => t.Key, _ => new List<(Finding F, bool Symptom)>());
            var assigned = new HashSet<Finding>();
            string Text(Finding f) => (f.Title + " " + f.Plain).ToLowerInvariant();

            // pass 1: symptoms (wait events, resource pressure)
            foreach (var f in live)
                foreach (var th in Themes)
                    if (th.IsSymptom(Text(f), f) && IsSymptomShaped(f)) { members[th.Key].Add((f, true)); assigned.Add(f); break; }

            // pass 2: contributors (SQL statements and hot segments), attached to a theme that has a symptom
            foreach (var f in live.Where(x => !assigned.Contains(x)))
            {
                var key = ContributorTheme(ctx, f, topSql, members);
                if (key != null && members[key].Any(m => m.Symptom)) { members[key].Add((f, false)); assigned.Add(f); }
            }

            var n = 0;
            foreach (var th in Themes)
            {
                var list = members[th.Key];
                if (list.Count < 2 || !list.Any(m => m.Symptom)) continue;
                // the cause is the symptom (the wait event or resource pressure), never a member statement: its impact and its
                // recommendation describe the group, so a CPU-bound statement cannot lend its share to a lock-wait group
                var symptoms = list.Where(m => m.Symptom).Select(m => m.F).ToList();
                var lead = symptoms.Where(f => f.Recommendations.Count > 0).OrderBy(f => f.Severity).ThenByDescending(f => f.Impact).FirstOrDefault()
                           ?? symptoms.OrderBy(f => f.Severity).ThenByDescending(f => f.Impact).First();
                var g = new RootCauseGroup
                {
                    Id = $"RC{++n}",
                    LeadFindingId = lead.Id,
                    ImpactPct = Math.Round(Math.Clamp(symptoms.Max(f => f.Impact), 0, 100), 1),
                    Title = th.Title,
                    FindingIds = list.OrderBy(m => m.F.Severity).ThenByDescending(m => m.F.Impact).Select(m => m.F.Id).ToList(),
                    Summary = $"{list.Count} findings point to the same cause: {th.Chain}. Fixing the cause should clear several symptoms at once."
                };
                groups.Add(g);
                foreach (var (f, _) in list)
                {
                    f.RootCauseGroup = th.Title;
                    f.RelatedIds = list.Where(m => m.F != f).Select(m => m.F.Id).ToList();
                }
            }
            // most severe group first, renumbered
            groups = groups.OrderBy(g => g.FindingIds.Min(id => findings.First(f => f.Id == id).Severity))
                           .ThenByDescending(g => g.FindingIds.Max(id => findings.First(f => f.Id == id).Impact)).ToList();
            for (var i = 0; i < groups.Count; i++) groups[i].Id = $"RC{i + 1}";
        }
        catch { /* grouping is a convenience: never fail the analysis */ }
        return groups;
    }

    /// <summary>Statement- and segment-level findings are contributors, not symptoms, even when their text mentions an event.</summary>
    private static bool IsSymptomShaped(Finding f) =>
        !SqlIdInTitle.IsMatch(f.Title) && !f.Title.Contains(" receives ", StringComparison.Ordinal) && !f.Title.StartsWith("Check that optimizer", StringComparison.Ordinal);

    private static string? ContributorTheme(AnalysisContext ctx, Finding f, List<SqlSummary> topSql, Dictionary<string, List<(Finding F, bool Symptom)>> members)
    {
        var title = f.Title;
        var text = (f.Title + " " + f.Plain).ToLowerInvariant();

        // hot segment: "OWNER.OBJECT receives 20% of all memory reads"
        if (title.Contains(" receives ", StringComparison.Ordinal))
        {
            var obj = title[..title.IndexOf(" receives ", StringComparison.Ordinal)].Trim();
            // an object named by a lock/hot-block symptom belongs there
            foreach (var key in new[] { "rowlock", "hotblock" })
                if (members[key].Any(m => m.Symptom && Mentions(m.F, obj))) return key;
            return text.Contains("physical") ? "io" : "cpu";
        }

        // statement-level
        var m = SqlIdInTitle.Match(title);
        if (m.Success)
        {
            var id = m.Groups[1].Value;
            var s = topSql.FirstOrDefault(x => x.SqlId.Equals(id, StringComparison.OrdinalIgnoreCase));
            var sqlText = ctx.SqlTexts.TryGetValue(id, out var tx) ? tx : s?.Text ?? "";
            var cpu = s?.PctCpu ?? (text.Contains("mostly on cpu") ? 100 : 0);
            var io = s?.PctIo ?? (text.Contains("mostly waiting on 'db file") || text.Contains("mostly waiting on 'direct path") ? 100 : 0);
            // a DML statement joins a lock / hot-block group only when it is not simply burning CPU or reading disk, and the
            // wait event itself is large enough to matter (a 2% lock wait does not explain a statement that uses 40% of DB time)
            var waitBound = cpu < 50 && io < 50;
            bool Significant(string key) => members[key].Any(x => x.Symptom && x.F.Impact >= 5);
            if (waitBound && DmlStart.IsMatch(sqlText) && Significant("rowlock")) return "rowlock";
            if (waitBound && DmlStart.IsMatch(sqlText) && Significant("hotblock")) return "hotblock";
            if (cpu >= 50 && cpu >= io) return "cpu";
            if (io >= 50) return "io";
            return null;
        }

        if (text.Contains("run hundreds of times per second") || text.Contains("re-parsed")) return members["parsing"].Any(x => x.Symptom) ? "parsing" : "cpu";
        if (text.Contains("rebuild older versions of blocks")) return "hotblock";
        if (text.Contains("temp") && f.Category == "Memory") return "io";
        return null;
    }

    private static bool Mentions(Finding f, string obj)
    {
        var name = obj.Split('.').Last().Split(' ')[0];
        if (name.Length < 3) return false;
        return f.Evidence.Any(e => e.Label.Contains(name, StringComparison.OrdinalIgnoreCase) || (e.Note ?? "").Contains(name, StringComparison.OrdinalIgnoreCase))
               || f.Recommendations.Any(r => r.Contains(name, StringComparison.OrdinalIgnoreCase))
               || f.Technical.Contains(name, StringComparison.OrdinalIgnoreCase);
    }
}
