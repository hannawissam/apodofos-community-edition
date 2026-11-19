// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text.RegularExpressions;
using QuietLoad.Core.Model;
using static QuietLoad.Core.Analysis.AnalysisContext;

namespace QuietLoad.Core.Analysis;

/// <summary>
/// Ranked "Fix first" plan: the five actions with the best payoff. Root-cause groups count once (their members overlap,
/// so their impacts are not added). Expected impact is the share of DB time the finding involves, an upper bound
/// of what the fix can win, not a promise.
/// </summary>
internal static class ActionPlanBuilder
{
    private static readonly Regex HighEffort = new(@"redesign|re-?architect|partition|rewrite|change the application|application change|add (more )?(cpu|memory|disk)|hardware|migrate|upgrade|resharding|new storage|schema", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LowEffort = new(@"alter system|parameter|gather|statistics|increase|enable|set |resize|check ", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HighRisk = new(@"restart|downtime|outage|upgrade|spfile|flush|kill|resize.*redo|drop |truncate|hardware|migrate", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MediumRisk = new(@"alter (system|table|index)|index|hint|baseline|profile|plan|cursor_sharing|parameter|create |rebuild|partition|rewrite", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<ActionItem> Build(List<Finding> findings, List<RootCauseGroup> groups, ReportKind kind = ReportKind.Awr)
    {
        var plan = new List<ActionItem>();
        try
        {
            var actionable = findings.Where(f => f.Severity <= Severity.Low && f.Category is not ("Report scope" or "Report quality" or "Statspack")).ToList();
            var byId = actionable.ToDictionary(f => f.Id);
            var used = new HashSet<string>();
            var candidates = new List<(ActionItem Item, double Score)>();

            foreach (var g in groups)
            {
                var ms = g.FindingIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
                if (ms.Count == 0) continue;
                foreach (var m in ms) used.Add(m.Id);
                // the group's own cause (symptom) supplies title, recommendation and impact; members only add context
                var lead = byId.TryGetValue(g.LeadFindingId ?? "", out var l) ? l : Lead(ms);
                candidates.Add(Make(lead, ms, g.Title, g.Summary, kind, g.ImpactPct > 0 ? g.ImpactPct : null));
            }
            foreach (var f in actionable.Where(f => !used.Contains(f.Id)))
                candidates.Add(Make(f, [f], null, null, kind, null));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (item, _) in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Item.FindingIds.First()))
            {
                if (!seen.Add(item.Action)) continue;
                item.Rank = plan.Count + 1;
                plan.Add(item);
                if (plan.Count == 5) break;
            }
        }
        catch { /* the plan is a convenience: never fail the analysis */ }
        return plan;
    }

    private static string Shorten(string title)
    {
        var t = title.TrimEnd('.', ':', ' ');
        return t.Length <= 110 ? t : t[..107].TrimEnd() + "...";
    }

    private static Finding Lead(List<Finding> ms) =>
        ms.Where(f => f.Recommendations.Count > 0).OrderBy(f => f.Severity).ThenByDescending(f => f.Impact).FirstOrDefault()
        ?? ms.OrderBy(f => f.Severity).ThenByDescending(f => f.Impact).First();

    private static (ActionItem, double) Make(Finding lead, List<Finding> members, string? groupTitle, string? groupSummary, ReportKind kind, double? causalImpact)
    {
        // members of a group overlap (the same DB time seen from several angles), so use the largest share, not the sum
        var timeWord = kind == ReportKind.Ash ? "active session time" : "DB time";
        // a group is worth what its cause is worth (the wait event / resource), not what its member statements add up to
        var impact = Math.Clamp(causalImpact ?? members.Max(m => m.Impact), 0, 100);
        var rec = lead.Recommendations.FirstOrDefault(r => !r.StartsWith("This is a PL/SQL call", StringComparison.OrdinalIgnoreCase)) ?? lead.Recommendations.FirstOrDefault() ?? lead.Title;
        // label = what the recommendation is about: the group's cause, or the finding's own title for a finding that stands alone
        var action = groupTitle != null ? $"{groupTitle}: {rec}" : $"{Shorten(lead.Title)}: {rec}";
        var probe = string.Join(" ", lead.Recommendations.Take(2)) + " " + lead.Title;
        var effort = HighEffort.IsMatch(probe) ? "High" : LowEffort.IsMatch(rec) ? "Low" : "Medium";
        var risk = HighRisk.IsMatch(probe) ? "High" : MediumRisk.IsMatch(probe) ? "Medium" : "Low";
        var conf = members.Select(m => m.Confidence).Min(); // the best-supported member decides
        var why = groupTitle != null ? $"{groupTitle}: {lead.Title}. {groupSummary}" : lead.Title + ".";
        var item = new ActionItem
        {
            Action = action,
            Why = why,
            ExpectedImpactPct = Math.Round(impact, 1),
            ExpectedImpact = impact >= 99 ? "affects the whole workload"
                : impact >= 1 ? $"involves about {N(impact, 0)}% of {timeWord} (upper bound)"
                : $"stability / hygiene: not measured in {timeWord}",
            Effort = effort,
            Risk = risk,
            Confidence = conf,
            FindingIds = members.OrderBy(m => m.Severity).ThenByDescending(m => m.Impact).Select(m => m.Id).ToList()
        };
        var sev = members.Min(m => m.Severity) switch { Severity.Critical => 40, Severity.High => 25, Severity.Medium => 12, _ => 3 };
        var confFactor = conf switch { Confidence.High => 1.0, Confidence.Medium => 0.7, _ => 0.4 };
        var effortPenalty = effort switch { "High" => 8, "Medium" => 3, _ => 0 };
        var score = Math.Min(impact, 60) * confFactor + sev * confFactor - effortPenalty;
        return (item, score);
    }
}
