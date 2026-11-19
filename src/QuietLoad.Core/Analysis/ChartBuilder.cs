// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using QuietLoad.Core.Model;
using QuietLoad.Core.Parsing;

namespace QuietLoad.Core.Analysis;

/// <summary>
/// Curated charts for the main AWR sections. Every other table is charted generically by the UI
/// ("All metrics" explorer), so together every numeric metric in the report can be visualised.
/// Chart ids are stable: findings reference them through <see cref="Finding.ChartRefs"/>.
/// </summary>
public static class ChartBuilder
{
    public static List<ChartSpec> Build(AnalysisContext ctx)
    {
        var r = ctx.Report;
        if (r.Kind == ReportKind.Ash) return AshChartBuilder.Build(ctx);
        var charts = new List<ChartSpec>();
        void Add(ChartSpec? c) { if (c != null && c.Labels.Count > 0 && c.Series.Any(s => s.Values.Any(v => v is not null))) charts.Add(c); }

        // ---- Overview ----
        var wc = r.Table("Foreground Wait Class") ?? r.Table("Wait Classes by Total Wait Time");
        Add(FromTable(wc, "db-time-breakdown", "Overview", "Where database time went", "donut", "Wait Class", ["% DB time", "%DB time"], "% of DB time",
            "Each slice is a share of total DB time. The biggest slice is where tuning pays off most.", top: 12, skipZero: true));

        var top = r.TableStartingWith("Top 10 Foreground Events", "Top 5 Timed");
        Add(FromTable(top, "top-events", "Overview", "Top foreground events (% of DB time)", "hbar", "Event", ["% DB time", "%DB time"], "% of DB time",
            "The events where sessions spent the most time. DB CPU is useful work; everything else is waiting."));
        Add(FromTable(top, "top-events-latency", "Overview", "Average wait per event", "hbar", "Event", ["Wait Avg(ms)", "Avg wait (ms)", "Avg Wait"], "ms",
            "How long each individual wait took. Disk reads should normally be under 10 ms, commits under 5 ms.", skipZero: true));

        var tm = r.Table("Time Model Statistics");
        if (tm != null)
        {
            var c = FromTable(tm, "time-model", "Overview", "Time model (% of DB time)", "hbar", "Statistic Name", ["% of DB Time"], "% of DB time",
                "Breaks DB time into activities (SQL execution, parsing, PL/SQL ...). Categories overlap, so they do not add up to 100%.", top: 20, skipZero: true);
            if (c != null)
            {
                var keep = c.Labels.Select((l, i) => (l, i)).Where(x => !x.l.Equals("DB time", StringComparison.OrdinalIgnoreCase)).Select(x => x.i).ToList();
                c.Labels = keep.Select(i => c.Labels[i]).ToList();
                foreach (var s in c.Series) s.Values = keep.Select(i => s.Values[i]).ToList();
                Add(c);
            }
        }

        var host = r.TableStartingWith("Host CPU");
        if (host?.DataRows.FirstOrDefault() is { Cells: not null } h)
        {
            Add(new ChartSpec
            {
                Id = "host-cpu", Group = "Overview", Title = "Host CPU utilisation", Type = "stacked", Unit = "%",
                Labels = ["Host CPU"], TableRef = host.Id,
                Series = [Series("User", h.Num("%User")), Series("System", h.Num("%System")), Series("I/O wait", h.Num("%WIO")), Series("Idle", h.Num("%Idle"))],
                Explain = "How busy the server's processors were. Little idle time means the server is CPU-bound."
            });
        }

        var eff = r.TableStartingWith("Instance Efficiency");
        if (eff != null && ctx.Efficiency.Count > 0)
        {
            var labels = new List<string>();
            var vals = new List<double?>();
            foreach (var row in eff.Rows)
                for (var i = 0; i + 1 < row.Length; i += 2)
                    if (row[i].Length > 0 && NumberParser.Parse(row[i + 1]) is { } v) { labels.Add(row[i].TrimEnd(':').Trim()); vals.Add(Math.Min(v, 100)); }
            Add(new ChartSpec
            {
                Id = "efficiency", Group = "Overview", Title = "Instance efficiency (target 100%)", Type = "hbar", Unit = "%",
                Labels = labels, Series = [new ChartSeries { Name = "%", Values = vals }], TableRef = eff.Id, Reference = 100, ReferenceLabel = "target",
                Explain = "Internal efficiency ratios. Values well below 100% support the findings; Oracle advises not to tune ratios in isolation."
            });
        }

        var lp = r.Table("Load Profile");
        Add(FromTable(lp, "load-profile", "Overview", "Load profile (per second)", "hbar", "", ["Per Second"], "per second",
            "Workload volume per second. Uses a logarithmic scale because values differ by orders of magnitude.", skipZero: true, log: true, cleanLabels: true));

        // ---- Wait events ----
        Add(FromTable(r.Table("Foreground Wait Events"), "fg-events", "Wait events", "Foreground wait events (total wait time)", "hbar", "Event", ["Total Wait Time (s)", "Total Wait Time (sec)"], "seconds",
            "Wait time of user sessions by event (idle events excluded by Oracle).", top: 15, skipZero: true));
        Add(FromTable(r.Table("Background Wait Events"), "bg-events", "Wait events", "Background wait events (total wait time)", "hbar", "Event", ["Total Wait Time (s)", "Total Wait Time (sec)"], "seconds",
            "Wait time of Oracle background processes (log writer, DB writer ...).", top: 15, skipZero: true));
        Add(Histogram(r.Table("Wait Event Histogram"), top));

        // ---- SQL ----
        Add(Sql(r.Table("SQL ordered by Elapsed Time"), "top-sql-elapsed", "Top SQL by elapsed time", ["Elapsed Time (s)"], "seconds", "Total time per statement in the period."));
        Add(Sql(r.Table("SQL ordered by CPU Time"), "top-sql-cpu", "Top SQL by CPU time", ["CPU Time (s)"], "seconds", "CPU consumed per statement."));
        Add(Sql(r.Table("SQL ordered by User I/O Wait Time"), "top-sql-io", "Top SQL by I/O wait", ["User I/O Time (s)"], "seconds", "Time each statement waited for disk."));
        Add(Sql(r.Table("SQL ordered by Gets"), "top-sql-gets", "Top SQL by logical reads (buffer gets)", ["Buffer Gets"], "blocks", "Memory reads per statement: the main driver of CPU usage."));
        Add(Sql(r.Table("SQL ordered by Reads"), "top-sql-reads", "Top SQL by physical reads", ["Physical Reads"], "blocks", "Disk reads per statement."));
        Add(Sql(r.Table("SQL ordered by Executions"), "top-sql-execs", "Top SQL by executions", ["Executions"], "executions", "How many times each statement ran."));
        Add(Sql(r.Table("SQL ordered by Parse Calls"), "top-sql-parses", "Top SQL by parse calls", ["Parse Calls"], "parses", "Statements parsed most often."));

        // ---- I/O ----
        var ts = r.Table("Tablespace IO Stats");
        // reads (count) and latency (ms) have different units, so they are separate charts instead of two series on one axis
        Add(FromTable(ts, "tablespace-io", "I/O", "Tablespace average read time (ms)", "hbar", "Tablespace", ["Av 1-bk Rd(ms)", "Av Rd(ms)"], "ms",
            "Average time per read for the busiest tablespaces (single-block reads where the report separates them). Disk reads should normally be under 10 ms.", top: 12, sortBy: "Reads"));
        Add(FromTable(ts, "tablespace-reads", "I/O", "Tablespace reads", "hbar", "Tablespace", ["Reads"], "reads",
            "Number of reads per tablespace in the period.", top: 12, sortBy: "Reads"));
        Add(FromTable(r.Table("IOStat by Function summary"), "iostat-function", "I/O", "I/O requests per second by function", "hbar", "Function Name", ["Reqs per sec"], "requests/s",
            "Which database components issue the I/O (buffer cache reads, LGWR, DBWR, direct reads ...).", skipZero: true, allMatches: true));
        Add(FromTable(r.Table("IOStat by Filetype summary"), "iostat-filetype", "I/O", "I/O requests per second by file type", "hbar", "Filetype Name", ["Reqs per sec"], "requests/s",
            "I/O by file type: data files, redo logs, temp files, control files.", skipZero: true, allMatches: true));

        // ---- Memory & advisories ----
        Add(Advisory(r.Table("Buffer Pool Advisory"), "advisory-buffer", "Buffer cache advisory", ["Size for Est (M)"], ["Est Phys Read Factor", "Estd Phys Read Factor"], "Cache size (MB)", "Physical read factor",
            "Predicted disk reads (1.0 = today) for different buffer cache sizes. A steep drop to the right means more memory would help.", r => r.Str("P") is "D" or ""));
        Add(Advisory(r.Table("PGA Memory Advisory"), "advisory-pga", "PGA memory advisory", ["PGA Target Est (MB)"], ["Estd PGA Cache Hit %"], "PGA target (MB)", "Estimated cache hit %",
            "Predicted share of sort/hash work done in memory at different PGA sizes."));
        Add(Advisory(r.Table("Shared Pool Advisory"), "advisory-shared-pool", "Shared pool advisory", ["Shared Pool Size(M)"], ["Est LC Time Saved Factr"], "Shared pool (MB)", "Parse time saved factor",
            "Predicted parse time saved at different shared pool sizes (1.0 = today)."));
        Add(Advisory(r.Table("SGA Target Advisory"), "advisory-sga", "SGA target advisory", ["SGA Target Size (M)"], ["Est DB Time (s)"], "SGA target (MB)", "Estimated DB time (s)",
            "Predicted total DB time at different SGA sizes. Lower is better."));
        Add(FromTable(r.Table("Memory Dynamic Components"), "memory-components", "Memory", "SGA/PGA component sizes", "hbar", "Component", ["Current Size (Mb)", "Current Size (MB)"], "MB",
            "Current size of each automatically managed memory component.", skipZero: true));

        // ---- Segments ----
        foreach (var t in r.Tables.Where(t => t.TitleStartsWith("Segments by")))
        {
            // the measured value is the column just before %Total / % of Capture (index 7 in 12c+, 5 in 11g layouts)
            var shareIdx = t.Col("%Total", "% of Capture");
            var valueCol = shareIdx > 0 ? t.Headers[shareIdx - 1] : null;
            if (valueCol == null) continue;
            var c = new ChartSpec
            {
                Id = "seg-" + Slug(t.Title["Segments by".Length..]), Group = "Segments", Title = t.Title, Type = "hbar", Unit = valueCol, TableRef = t.Id,
                Explain = "The database objects (tables, indexes) with the highest activity of this type."
            };
            var s = new ChartSeries { Name = valueCol };
            foreach (var row in t.DataRows)
            {
                var name = row.Str("Object Name");
                var sub = row.Str("Subobject Name");
                c.Labels.Add($"{row.Str("Owner")}.{name}{(sub.Length > 0 ? "." + sub : "")}");
                s.Values.Add(row.Num(valueCol));
            }
            c.Series.Add(s);
            Add(c);
        }

        // ---- Contention ----
        Add(FromTable(r.Table("Latch Sleep Breakdown"), "latch-sleeps", "Contention", "Latch sleeps", "hbar", "Latch Name", ["Sleeps"], "sleeps",
            "Internal latches where sessions had to sleep (wait) to get access.", top: 12, skipZero: true, sortBy: "Sleeps"));
        Add(FromTable(r.Table("Enqueue Activity"), "enqueues", "Contention", "Enqueue (lock) wait time", "hbar", "Enqueue Type (Request Reason)", ["Wt Time (s)"], "seconds",
            "Time spent waiting for each type of lock.", top: 12, skipZero: true));
        Add(FromTable(r.Table("Buffer Wait Statistics"), "buffer-waits", "Contention", "Buffer waits by block class", "hbar", "Class", ["Total Wait Time (s)"], "seconds",
            "Which kind of block sessions were fighting over (data block, undo header, segment header ...).", skipZero: true));
        Add(FromTable(r.Table("Mutex Sleep Summary"), "mutex-sleeps", "Contention", "Mutex sleeps", "hbar", "Mutex Type", ["Wait Time (ms)"], "ms",
            "Library cache / cursor mutex contention.", top: 12, skipZero: true));

        // ---- Coverage gap fillers (sections that were only visible in the table explorer before) ----
        Add(FromTable(r.Table("File IO Stats"), "file-io-latency", "I/O", "Data file average read time (ms)", "hbar", "Filename", ["Av Rd(ms)"], "ms",
            "The slowest data files by average read time. Single-digit milliseconds is healthy for spinning disks, 1-2 ms for flash.", top: 12, skipZero: true, sortBy: "Av Rd(ms)"));
        Add(FromTable(r.Table("Latch Activity"), "latch-wait-time", "Contention", "Latch wait time (s)", "hbar", "Latch Name", ["Wait Time (s)"], "seconds",
            "Latches where sessions lost the most time waiting, from the full latch activity list.", top: 12, skipZero: true, sortBy: "Wait Time (s)"));
        Add(FromTable(r.Table("Dictionary Cache Stats"), "dictionary-cache-miss", "Memory", "Dictionary cache miss rate (%)", "hbar", "Cache", ["Pct Miss"], "% of requests",
            "How often the data dictionary cache (row cache) had to go to disk. A high rate with many requests means the shared pool is too small.", top: 12, skipZero: true, sortBy: "Pct Miss"));
        Add(Sql(r.Table("SQL ordered by Sharable Memory"), "top-sql-sharable", "Top SQL by sharable memory", ["Sharable Mem (b)"], "bytes", "Shared pool memory held by each statement's cursors."));
        Add(Sql(r.Table("SQL ordered by Physical Reads (UnOptimized)"), "top-sql-unoptimized", "Top SQL by unoptimized physical reads", ["UnOptimized Read Reqs"], "requests", "Reads that Exadata/flash caching could not optimise."));
        Add(FromTable(r.Table("Service Wait Class Stats"), "service-userio-wait", "Overview", "User I/O wait time by service", "hbar", "Service Name", ["User I/O Wt Time"], "centiseconds",
            "Which database services (applications) lost the most time to disk reads.", skipZero: true));

        // ---- ASH / activity ----
        Add(AshOverTime(r.Table("Activity Over Time")));
        Add(FromTable(r.Table("Top Events"), "ash-top-events", "Activity (ASH)", "ASH top events (% of activity)", "hbar", "Event", ["% Activity"], "% of activity",
            "Sampled session activity by event."));
        Add(FromTable(r.TableStartingWith("Top ADDM Findings"), "addm", "Overview", "ADDM findings (% of active sessions)", "hbar", "Finding Name", ["Percent active sessions of finding"], "%",
            "Oracle's ADDM findings ranked by their impact."));

        // ---- Undo / misc ----
        Add(TimeLine(r.Table("Undo Segment Stats"), "undo-blocks", "Undo", "Undo blocks per interval", "End Time", "Num Undo Blocks", "blocks", "Undo generated over time."));
        Add(FromTable(r.Table("Service Statistics"), "service-dbtime", "Overview", "DB time by service", "donut", "Service Name", ["DB Time (s)"], "seconds",
            "Which database services (applications) consumed DB time.", skipZero: true));
        var rl = r.Table("Resource Limit Stats");
        if (rl != null)
        {
            var c = new ChartSpec { Id = "resource-limits", Group = "Configuration", Title = "Resource limit utilisation", Type = "hbar", Unit = "% of limit", TableRef = rl.Id, Reference = 100, ReferenceLabel = "limit",
                Explain = "Peak usage of capped resources as a percentage of their limit." };
            var s = new ChartSeries { Name = "% of limit" };
            foreach (var row in rl.DataRows)
                if (row.Num("Maximum Utilization") is { } m && row.Num("Limit") is > 0 and var l) { c.Labels.Add(row.Str("Resource Name")); s.Values.Add(Math.Round(m / l * 100, 1)); }
            c.Series.Add(s);
            Add(c);
        }

        return charts;
    }

    private static ChartSeries Series(string name, params double?[] values) => new() { Name = name, Values = [.. values] };

    private static string Slug(string s) => new string(s.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-').Replace("--", "-");

    /// <summary>Builds a chart from a label column and one or more numeric columns.</summary>
    private static ChartSpec? FromTable(AwrTable? t, string id, string group, string title, string type, string labelCol, string[] valueCols, string? unit,
        string explain, int top = 15, bool skipZero = false, bool log = false, bool cleanLabels = false, string? sortBy = null, bool allMatches = false)
    {
        if (t == null || t.Rows.Count == 0) return null;
        var li = labelCol.Length == 0 ? 0 : t.Col(labelCol);
        if (li < 0) li = 0;
        // Each requested column becomes a series; for allMatches the first header occurrence is used.
        var cols = new List<int>();
        foreach (var vc in valueCols)
        {
            var ci = t.Col(vc);
            if (ci >= 0 && !cols.Contains(ci)) { cols.Add(ci); if (type is "donut" or "line") break; }
            if (!allMatches && type != "hbar" && cols.Count > 0) break;
        }
        // For multi-name candidates of the same metric keep only the first match.
        if (valueCols.Length > 1 && cols.Count > 1 && valueCols.All(v => v.Contains("DB time", StringComparison.OrdinalIgnoreCase) || v.Contains("Avg", StringComparison.OrdinalIgnoreCase) || v.Contains("Total Wait", StringComparison.OrdinalIgnoreCase) || v.Contains("Size", StringComparison.OrdinalIgnoreCase)))
            cols = [cols[0]];
        if (cols.Count == 0) return null;

        var rows = t.Rows.Where(row => row.Length > li && row[li].Trim().Length > 0).ToList();
        if (skipZero) rows = rows.Where(row => cols.Any(c => c < row.Length && (NumberParser.Parse(row[c]) ?? 0) != 0)).ToList();
        var sortIdx = sortBy != null ? t.Col(sortBy) : -1;
        if (sortIdx >= 0) rows = rows.OrderByDescending(row => sortIdx < row.Length ? NumberParser.Parse(row[sortIdx]) ?? 0 : 0).ToList();
        rows = rows.Take(top).ToList();
        if (rows.Count == 0) return null;

        return new ChartSpec
        {
            Id = id, Group = group, Title = title, Type = type, Unit = unit, Explain = explain, TableRef = t.Id, LogScale = log,
            Labels = rows.Select(row => cleanLabels ? row[li].TrimEnd(':').Trim() : row[li]).ToList(),
            Series = cols.Select(c => new ChartSeries { Name = t.Headers.ElementAtOrDefault(c) ?? "", Values = rows.Select(row => c < row.Length ? NumberParser.Parse(row[c]) : null).ToList() }).ToList()
        };
    }

    private static ChartSpec? Sql(AwrTable? t, string id, string title, string[] valueCol, string unit, string explain)
    {
        var c = FromTable(t, id, "SQL", title, "hbar", "SQL Id", valueCol, unit, explain, top: 15);
        if (c == null || t == null) return c;
        // Add module as a subtitle hint for the first entry
        c.Subtitle = "Labels are SQL_IDs; open the SQL tab for full text.";
        return c;
    }

    private static ChartSpec? Advisory(AwrTable? t, string id, string title, string[] xCols, string[] yCols, string xLabel, string yLabel, string explain, Func<AwrRow, bool>? filter = null)
    {
        if (t == null || t.Rows.Count == 0) return null;
        var xi = t.Col(xCols);
        var yi = t.Col(yCols);
        if (xi < 0 || yi < 0) return null;
        var rows = t.DataRows.Where(r => filter?.Invoke(r) ?? true).Where(r => r.NumAt(xi) is not null).OrderBy(r => r.NumAt(xi)).ToList();
        if (rows.Count < 2) return null;
        return new ChartSpec
        {
            Id = id, Group = "Memory", Title = title, Type = "line", TableRef = t.Id, XLabel = xLabel, YLabel = yLabel, Explain = explain,
            Labels = rows.Select(r => r.StrAt(xi)).ToList(),
            Series = [new ChartSeries { Name = yLabel, Values = rows.Select(r => r.NumAt(yi)).ToList() }]
        };
    }

    private static ChartSpec? TimeLine(AwrTable? t, string id, string group, string title, string xCol, string yCol, string unit, string explain)
    {
        if (t == null || t.Rows.Count < 2) return null;
        var xi = t.Col(xCol);
        var yi = t.Col(yCol);
        if (xi < 0 || yi < 0) return null;
        var rows = t.DataRows.Reverse().ToList(); // AWR lists newest first
        return new ChartSpec
        {
            Id = id, Group = group, Title = title, Type = "line", Unit = unit, TableRef = t.Id, Explain = explain, XLabel = xCol, YLabel = yCol,
            Labels = rows.Select(r => r.StrAt(xi)).ToList(),
            Series = [new ChartSeries { Name = yCol, Values = rows.Select(r => r.NumAt(yi)).ToList() }]
        };
    }

    private static ChartSpec? AshOverTime(AwrTable? t)
    {
        if (t == null || t.Rows.Count == 0) return null;
        var slotCol = t.Col("Slot Time (Duration)", "Slot Time");
        var countCol = t.Col("Slot Count");
        if (slotCol < 0 || countCol < 0) return null;
        var labels = new List<string>();
        var vals = new List<double?>();
        string last = "";
        foreach (var r in t.DataRows)
        {
            var slot = r.StrAt(slotCol);
            if (slot.Length == 0 || slot == last) continue;
            last = slot;
            labels.Add(slot.Split('(')[0].Trim());
            vals.Add(r.NumAt(countCol));
        }
        if (labels.Count < 2) return null;
        return new ChartSpec
        {
            Id = "ash-activity", Group = "Activity (ASH)", Title = "Active session samples over time", Type = "line", Unit = "samples", TableRef = t.Id,
            XLabel = "Time slot", YLabel = "ASH samples", Labels = labels, Series = [new ChartSeries { Name = "Samples", Values = vals }],
            Explain = "Number of active-session samples per time slot. Peaks show when the database was busiest."
        };
    }

    /// <summary>Latency distribution (stacked % per bucket) for the top non-CPU events.</summary>
    private static ChartSpec? Histogram(AwrTable? hist, AwrTable? top)
    {
        if (hist == null || hist.Rows.Count == 0 || hist.Headers.Count < 4) return null;
        var events = top?.DataRows.Select(r => r.Str("Event")).Where(e => e.Length > 0 && e != "DB CPU").Take(6).ToList() ?? [];
        if (events.Count == 0) events = hist.DataRows.OrderByDescending(r => r.NumAt(1) ?? 0).Take(6).Select(r => r.Label).ToList();
        var rows = events.Select(e => hist.RowByLabel(e)).Where(r => r is not null).Select(r => r!.Value).ToList();
        if (rows.Count == 0) return null;
        var bucketCols = Enumerable.Range(2, hist.Headers.Count - 2).ToList();
        return new ChartSpec
        {
            Id = "wait-histogram", Group = "Wait events", Title = "Latency distribution of top events", Type = "stacked", Unit = "% of waits", TableRef = hist.Id,
            Labels = rows.Select(r => r.Label).ToList(),
            Series = bucketCols.Select(c => new ChartSeries
            {
                Name = hist.Headers[c].Replace("% of Waits", "").Replace("% of Total Waits", "").Trim(),
                Values = rows.Select(r => (double?)(r.NumAt(c) ?? 0)).ToList()
            }).ToList(),
            Explain = "For each event, the share of waits that completed within each time bucket. More weight on the left (fast buckets) is better."
        };
    }
}
