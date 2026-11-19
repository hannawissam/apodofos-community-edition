// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using QuietLoad.Core.Model;
using QuietLoad.Core.Parsing;
using static QuietLoad.Core.Analysis.Rules.AshHelpers;
using static QuietLoad.Core.Analysis.Rules.RuleHelpers;

namespace QuietLoad.Core.Analysis;

/// <summary>Curated charts for ASH reports (activity timeline, events, wait classes, SQL, sessions, objects). Ids are stable.</summary>
internal static class AshChartBuilder
{
    public static List<ChartSpec> Build(AnalysisContext ctx)
    {
        var charts = new List<ChartSpec>();
        void Add(ChartSpec? c) { if (c != null && c.Labels.Count > 0 && c.Series.Any(s => s.Values.Any(v => v is not null))) charts.Add(c); }

        // ---- Activity ----
        var slots = Rules.AshLoadRules.Slots(ctx);
        if (slots.Count >= 2)
        {
            var w = ctx.Info.AshFromHistory ? 10.0 : 1.0;
            var tbl = T(ctx, "Activity Over Time");
            Add(new ChartSpec
            {
                Id = "ash-activity", Group = "Activity", Title = "Active sessions over time", Type = "line", Unit = "active sessions", TableRef = tbl?.Id,
                XLabel = "Time slot", YLabel = "Average active sessions",
                Labels = slots.Select(s => s.Label.Split('(')[0].Trim()).ToList(),
                Series = [new ChartSeries { Name = "Average active sessions", Values = slots.Select(s => s.Minutes > 0 ? (double?)Math.Round(s.Count * w / (s.Minutes * 60), 2) : null).ToList() }],
                Reference = ctx.Cpus is > 0 ? ctx.Cpus : null, ReferenceLabel = ctx.Cpus is > 0 ? "CPUs" : null,
                Explain = "How many sessions were active at once in each slot (samples per second of the slot). Above the CPU line, sessions have to queue for processor time."
            });
            Add(new ChartSpec
            {
                Id = "ash-activity-samples", Group = "Activity", Title = "ASH samples per time slot", Type = "bar", Unit = "samples", TableRef = tbl?.Id,
                Labels = slots.Select(s => s.Label.Split('(')[0].Trim()).ToList(),
                Series = [new ChartSeries { Name = "Samples", Values = slots.Select(s => (double?)s.Count).ToList() }],
                Explain = "Raw sample counts; the first and last slot are shorter than the others, so compare the chart above for load."
            });
        }

        var users = T(ctx, "Top User Events");
        Add(FromTable(users, "ash-top-events", "Events", "Top user events (% of sampled activity)", "hbar", "Event", ["% Event", "% Activity"], "% of activity",
            "Where active sessions spent their time. 'CPU + Wait for CPU' is useful work (or queueing for CPU); everything else is waiting."));
        Add(FromTable(T(ctx, "Top Background Events"), "ash-bg-events", "Events", "Top background events (% of sampled activity)", "hbar", "Event", ["% Activity", "% Event"], "% of activity",
            "What Oracle's own background processes (log writer, database writer ...) were doing."));
        if (users != null)
        {
            var byClass = users.DataRows.GroupBy(r => IsCpu(r.Str("Event")) ? "CPU" : (r.Str("Event Class", "Wait Class") is { Length: > 0 } c ? c : "Other"))
                .Select(g => (g.Key, V: g.Sum(Pct))).Where(x => x.V > 0).OrderByDescending(x => x.V).ToList();
            Add(new ChartSpec
            {
                Id = "ash-wait-classes", Group = "Events", Title = "Where sampled time went (wait class)", Type = "donut", Unit = "% of activity", TableRef = users.Id,
                Labels = byClass.Select(x => x.Key).ToList(), Series = [new ChartSeries { Name = "% of activity", Values = byClass.Select(x => (double?)Math.Round(x.V, 2)).ToList() }],
                Explain = "Each slice is a share of the sampled activity of user sessions: CPU, I/O, concurrency, application locks and so on."
            });
            var cpu = users.DataRows.Where(r => IsCpu(r.Str("Event"))).Sum(Pct);
            var total = users.DataRows.Sum(Pct);
            if (total > 0)
                Add(new ChartSpec
                {
                    Id = "ash-cpu-vs-wait", Group = "Events", Title = "On CPU versus waiting", Type = "stacked", Unit = "%", TableRef = users.Id, Labels = ["User sessions"],
                    Series = [new ChartSeries { Name = "CPU + wait for CPU", Values = [Math.Round(cpu, 1)] }, new ChartSeries { Name = "Waiting", Values = [Math.Round(Math.Max(0, total - cpu), 1)] }],
                    Explain = "CPU-heavy means reduce the work SQL does; wait-heavy means find the bottleneck (disk, locks, contention)."
                });
        }

        // ---- SQL ----
        var sql = T(ctx, "Top SQL with Top Events", "Top SQL Statements");
        if (sql != null)
        {
            var rows = Distinct(sql, "SQL ID").OrderByDescending(r => r.Val("% Activity") ?? 0).Take(10).ToList();
            Add(new ChartSpec
            {
                Id = "ash-top-sql", Group = "SQL", Title = "Top SQL (% of sampled activity)", Type = "hbar", Unit = "% of activity", TableRef = sql.Id,
                Subtitle = "Labels are SQL_IDs; open the SQL tab for full text.",
                Labels = rows.Select(r => r.Str("SQL ID")).ToList(), Series = [new ChartSeries { Name = "% Activity", Values = rows.Select(r => r.Val("% Activity")).ToList() }],
                Explain = "The statements that kept sessions busiest or waiting. Tuning the top one or two usually gives the largest gain."
            });
        }
        Add(FromTable(T(ctx, "Top SQL Command Types"), "ash-command-types", "SQL", "Activity by SQL command type", "donut", "SQL Command Type", ["% Activity"], "% of activity", "What kinds of statements the sessions were running."));
        Add(FromTable(T(ctx, "Top Phases of Execution"), "ash-phases", "SQL", "Phases of execution", "hbar", "Phase of Execution", ["% Activity"], "% of activity", "Parsing, executing, connecting: where in the life of a statement the time went."));

        // ---- Who / where ----
        var mod = T(ctx, "Top Service/Module");
        if (mod != null)
        {
            var rows = Distinct(mod, "Service", "Module").OrderByDescending(r => r.Val("% Activity") ?? 0).Take(10).ToList();
            Add(new ChartSpec
            {
                Id = "ash-top-modules", Group = "Sessions", Title = "Top service / module (% of activity)", Type = "hbar", Unit = "% of activity", TableRef = mod.Id,
                Labels = rows.Select(r => $"{r.Str("Service")} / {r.Str("Module")}").ToList(), Series = [new ChartSeries { Name = "% Activity", Values = rows.Select(r => r.Val("% Activity")).ToList() }],
                Explain = "Which application component (module) and service generated the activity."
            });
        }
        var ses = T(ctx, "Top Sessions");
        if (ses != null)
        {
            var rows = Distinct(ses, "Sid, Serial#", "Sid,Srl# (Inst)", "Sid").OrderByDescending(r => r.Val("% Activity") ?? 0).Take(10).ToList();
            Add(new ChartSpec
            {
                Id = "ash-top-sessions", Group = "Sessions", Title = "Top sessions (% of activity)", Type = "hbar", Unit = "% of activity", TableRef = ses.Id,
                Labels = rows.Select(r => $"{r.Str("Sid, Serial#", "Sid,Srl# (Inst)", "Sid")} {r.Str("Program")}".Trim()).ToList(), Series = [new ChartSeries { Name = "% Activity", Values = rows.Select(r => r.Val("% Activity")).ToList() }],
                Explain = "The individual sessions that were busiest. One dominant session points to one program or job."
            });
        }
        var obj = T(ctx, "Top DB Objects");
        if (obj != null)
        {
            var rows = Distinct(obj, "Object ID").OrderByDescending(r => r.Val("% Activity") ?? 0).Take(10).ToList();
            Add(new ChartSpec
            {
                Id = "ash-top-objects", Group = "Objects", Title = "Top database objects (% of activity)", Type = "hbar", Unit = "% of activity", TableRef = obj.Id,
                Labels = rows.Select(r => r.Str("Object Name (Type)", "Object Name")).ToList(), Series = [new ChartSeries { Name = "% Activity", Values = rows.Select(r => r.Val("% Activity")).ToList() }],
                Explain = "Tables and indexes where sessions waited for I/O, locks or busy blocks."
            });
        }
        return charts;
    }

    private static ChartSpec? FromTable(AwrTable? t, string id, string group, string title, string type, string labelCol, string[] valueCols, string unit, string explain)
    {
        if (t == null || t.Rows.Count == 0) return null;
        var li = Math.Max(0, t.Col(labelCol));
        var vi = t.Col(valueCols);
        if (vi < 0) return null;
        var rows = t.Rows.Where(r => r.Length > Math.Max(li, vi) && r[li].Trim().Length > 0 && (NumberParser.Parse(r[vi]) ?? 0) > 0)
            .GroupBy(r => r[li].Trim()).Select(g => g.First()).Take(12).ToList();
        if (rows.Count == 0) return null;
        return new ChartSpec
        {
            Id = id, Group = group, Title = title, Type = type, Unit = unit, Explain = explain, TableRef = t.Id,
            Labels = rows.Select(r => r[li].Trim()).ToList(),
            Series = [new ChartSeries { Name = t.Headers[vi], Values = rows.Select(r => NumberParser.Parse(r[vi])).ToList() }]
        };
    }
}
