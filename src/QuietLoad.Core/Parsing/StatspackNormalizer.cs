// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Globalization;
using System.Text.RegularExpressions;
using QuietLoad.Core.Model;

namespace QuietLoad.Core.Parsing;

/// <summary>
/// Maps Statspack titles, column headers and values onto the names the shared AWR rules and charts look up, adds what Statspack
/// does not print (wait classes, % of DB time, %CPU of a statement, a SQL text list) and separates idle events.
/// </summary>
internal static partial class StatspackNormalizer
{
    [GeneratedRegex(@"\s+(?:DB/Inst:|for DB:|End Snap:|Snaps?:).*$", RegexOptions.IgnoreCase)]
    private static partial Regex TitleTail();

    private static readonly Dictionary<string, string> TitleMap = new(StringComparer.Ordinal)
    {
        ["time model system stats"] = "Time Model Statistics",
        ["wait events"] = "Foreground Wait Events",
        ["sql ordered by cpu"] = "SQL ordered by CPU Time",
        ["sql ordered by elapsed"] = "SQL ordered by Elapsed Time",
        ["sql ordered by disk reads"] = "SQL ordered by Reads",
        ["os statistics"] = "Operating System Statistics",
        ["top 5 wait events"] = "Top 5 Timed Events",
        ["cache sizes end"] = "Cache Sizes",
        ["enqueue activity"] = "Enqueue Activity",
        ["buffer wait statistics"] = "Buffer Wait Statistics",
        ["latch sleep breakdown"] = "Latch Sleep Breakdown",
    };

    public static string CanonicalTitle(string raw)
    {
        var t = TitleTail().Replace(raw.Trim(), "");
        t = Regex.Replace(t, @"\s{2,}", " ").Trim().TrimEnd(':').Trim();
        t = Regex.Replace(t, @"\s*\((end|begin)\)$", "", RegexOptions.IgnoreCase);
        return TitleMap.TryGetValue(AwrTable.Normalize(t), out var m) ? m : t;
    }

    // ------------------------------------------------------------------------------------------ event knowledge

    private static readonly HashSet<string> IdleEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "SQL*Net message from client", "SQL*Net message to client", "SQL*Net more data from client", "SQL*Net message from dblink",
        "SQL*Net more data from dblink", "dispatcher timer", "dispatcher listen timer", "pipe get", "PL/SQL lock timer", "rdbms ipc message",
        "smon timer", "pmon timer", "wait for unread message on broadcast channel", "jobq slave wait", "virtual circuit status",
        "lock manager wait for remote message", "gcs remote message", "ges remote message", "DIAG idle wait", "PX Idle Wait",
        "Space Manager: slave idle wait", "class slave wait", "EMON slave idle wait", "LNS ASYNC dest activity", "LogMiner: client waiting for transaction",
        "ASM background timer", "GCR sleep", "heartbeat monitor sleep", "i/o slave wait", "Queue Monitor Wait", "watchdog main loop", "MRP redo arrival",
        "Wait for shrink lock", "SGA: MMAN sleep for component shrink", "Streams AQ: qmn slave idle wait", "Streams AQ: qmn coordinator idle wait",
        "Streams AQ: waiting for messages in the queue", "Streams AQ: waiting for time management or cleanup tasks", "parallel recovery slave idle wait",
        "fbar timer", "VKTM Logical Idle Wait", "VKRM Idle", "PING", "SQL*Net break/reset to client-IGNORE"
    };

    private static readonly string[] IdlePrefixes = ["Streams AQ:", "PX Deq:", "PX Idle", "LNS ", "LGWR worker group idle", "wait for unread message"];

    public static bool IsIdleEvent(string name)
    {
        var n = name.Trim();
        if (n.Length == 0) return false;
        if (IdleEvents.Contains(n)) return true;
        if (IdlePrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return !n.StartsWith("PX Deq Credit", StringComparison.OrdinalIgnoreCase);
        // Statspack truncates event names to 32 characters in the wait tables.
        return n.Length >= 20 && IdleEvents.Any(i => i.Length > n.Length && i.StartsWith(n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Oracle wait class of an event (Statspack before 11g does not print it).</summary>
    public static string WaitClassOf(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        if (n is "db cpu" or "cpu time") return "";
        if (IsIdleEvent(name)) return "Idle";
        if (n.StartsWith("gc ") || n.StartsWith("gcs ") || n.StartsWith("ges ")) return "Cluster";
        if (n is "log file sync") return "Commit";
        if (n.StartsWith("log file switch") || n.StartsWith("log buffer space") || n.StartsWith("free buffer waits") || n.StartsWith("write complete waits")
            || n.StartsWith("undo segment extension") || n.StartsWith("enq: hw") || n.StartsWith("enq: st") || n.StartsWith("enq: sq") || n.StartsWith("checkpoint completed")) return "Configuration";
        if (n.StartsWith("enq: tx - row lock") || n.StartsWith("enq: tm") || n.StartsWith("enq: ul") || n.StartsWith("lock:")) return "Application";
        if (n.StartsWith("enq: tx - index") || n.StartsWith("enq: tx - allocate") || n.StartsWith("enq: tx")) return n.Contains("row lock") ? "Application" : "Concurrency";
        if (n.StartsWith("enq:") || n == "enqueue") return "Other";
        if (n.StartsWith("latch free") || n.StartsWith("wait list latch")) return "Other";
        if (n.StartsWith("latch:") || n.StartsWith("library cache") || n.StartsWith("cursor:") || n.StartsWith("row cache") || n.StartsWith("buffer busy")
            || n.StartsWith("buffer exterminate") || n.StartsWith("mutex") || n.StartsWith("resmgr:become active")) return n.StartsWith("resmgr") ? "Scheduler" : "Concurrency";
        if (n.StartsWith("resmgr:")) return "Scheduler";
        if (n.StartsWith("sql*net")) return "Network";
        if (n.StartsWith("control file") || n.StartsWith("db file parallel write") || n.StartsWith("log file parallel write") || n.StartsWith("log file sequential read")
            || n.StartsWith("log archive") || n.StartsWith("db file single write") || n.StartsWith("ksfd") || n.StartsWith("rman") || n.StartsWith("os thread startup")
) return n.StartsWith("os thread") ? "Concurrency" : "System I/O";
        if (n.StartsWith("db file ") || n.StartsWith("direct path") || n.StartsWith("read by other session") || n.StartsWith("local write wait")) return "User I/O";
        if (n.StartsWith("lgwr wait for redo copy") || n.StartsWith("sga:") || n.StartsWith("events in waitclass")) return "Other";
        return "Other";
    }

    // --------------------------------------------------------------------------------------------- helpers

    private static string Key(string? s) => AwrTable.Normalize(s);
    private static int ColIdx(AwrTable t, string normalized) => t.Headers.FindIndex(h => Key(h) == normalized);

    private static void Rename(AwrTable t, string fromNormalized, string to)
    {
        var i = ColIdx(t, fromNormalized);
        if (i >= 0) t.Headers[i] = to;
    }

    private static string F(double v, string fmt = "0.##") => NumberParser.Format(v, fmt);

    private static void AddColumn(AwrTable t, string name, Func<string[], string> value)
    {
        var width = Math.Max(t.Headers.Count, t.Rows.Count == 0 ? 0 : t.Rows.Max(r => r.Length));
        while (t.Headers.Count < width) t.Headers.Add("");
        t.Headers.Add(name);
        for (var i = 0; i < t.Rows.Count; i++)
        {
            var r = t.Rows[i];
            if (r.Length < width) r = [.. r, .. Enumerable.Repeat("", width - r.Length)];
            t.Rows[i] = [.. r, value(r)];
        }
    }

    private static AwrTable NewTable(string title, List<string> headers, List<string[]> rows) =>
        new() { Id = "", Index = 0, Title = title, Headers = headers, Rows = rows };

    // ------------------------------------------------------------------------------------------------ main

    public static void Normalize(AwrReport report, string[] lines)
    {
        var info = report.Info;
        foreach (var t in report.Tables.ToList())
        {
            try { FixTable(report, t); }
            catch (Exception ex) { report.ParseWarnings.Add($"Table '{t.Title}' could not be normalised: {ex.Message}"); }
        }

        // Host CPU provides the CPU count when the header line has none.
        var host = report.TableStartingWith("Host CPU");
        if (info.Cpus is null && host?.DataRows.FirstOrDefault() is { Cells.Length: > 0 } h && h.Num("CPUs") is > 0 and var c) info.Cpus = (int)c;
        if (info.Cores is null && host?.DataRows.FirstOrDefault() is { Cells.Length: > 0 } h2 && h2.Num("Cores") is > 0 and var cr) info.Cores = (int)cr;

        // DB time: the header line (11g+), else the time model, else estimated from the Top 5 events (CPU seconds / its share).
        var top = report.TableStartingWith("Top 5 Timed");
        if (info.DbTimeMinutes is null)
        {
            var tm = report.Table("Time Model Statistics")?.RowByLabel("DB time")?.NumAt(1);
            if (tm is > 0) info.DbTimeMinutes = tm / 60;
            else if (top != null)
            {
                foreach (var r in top.DataRows)
                {
                    var sec = r.Num("Time (s)");
                    var pct = r.Num("% DB time");
                    if (sec is > 0 && pct is > 0) { info.DbTimeMinutes = sec / (pct / 100) / 60; report.ParseWarnings.Add("DB time is not printed in this Statspack report; it was estimated from the Top 5 events (time / share of total call time)."); break; }
                }
            }
        }
        var dbSec = info.DbTimeMinutes * 60;

        foreach (var t in report.Tables.ToList())
            if (t.Title is "Foreground Wait Events" or "Background Wait Events") PrepareEvents(report, t, dbSec);

        MoveIdleFromTop(report, top);
        AddLoadProfileTimes(report, top, dbSec);
        AddWaitClasses(report, top, dbSec);
        ExpandHistogramLabels(report);
        AddSqlTextList(report);
        AddWarnings(report, lines);

        if (info.MajorVersion is { } v && v < 19)
            report.ParseWarnings.Add($"Report is from Oracle {info.Release} (Statspack). Rules are calibrated for 19c+; most still apply, and sections this release does not print are skipped.");
    }

    // ---------------------------------------------------------------------------------------------- tables

    private static void FixTable(AwrReport report, AwrTable t)
    {
        var key = Key(t.Title);
        if (key == "time model statistics") Rename(t, "statistic", "Statistic Name");
        else if (key == "load profile") FixLoadProfile(t);
        else if (key == "memory statistics") FixMemory(t);
        else if (key == "operating system statistics") Rename(t, "total", "Value");
        else if (key.StartsWith("segments by", StringComparison.Ordinal)) Rename(t, "pct total", "%Total");
        else if (key == "buffer pool statistics") { Rename(t, "buffers", "Number of Buffers"); Rename(t, "free buffer waits", "Free Buff Wait"); }
        else if (key == "buffer pool advisory") { Rename(t, "phys read factr", "Est Phys Read Factor"); Rename(t, "size factr", "Size Factr"); }
        else if (key == "undo segment summary") Rename(t, "max tx concy", "Max Tx Concurrency");
        else if (key == "shared pool advisory") Rename(t, "time saved factr", "Est LC Time Saved Factr");
        else if (key == "pga aggr target stats" && t.HasColumn("PGA Cache Hit %") && t.Headers.Count <= 4) t.Title = "PGA Aggr Summary";
        else if (key.StartsWith("sql ordered by", StringComparison.Ordinal)) FixSql(t, key);
        else if (key.StartsWith("top 5", StringComparison.Ordinal)) { }
    }

    private static void FixLoadProfile(AwrTable t)
    {
        var map = new Dictionary<string, string>
        {
            ["redo size"] = "Redo size (bytes):", ["logical reads"] = "Logical read (blocks):", ["physical reads"] = "Physical read (blocks):",
            ["physical writes"] = "Physical write (blocks):", ["parses"] = "Parses (SQL):", ["hard parses"] = "Hard parses (SQL):", ["executes"] = "Executes (SQL):",
            ["db time s"] = "DB Time(s):", ["db cpu s"] = "DB CPU(s):"
        };
        foreach (var r in t.Rows)
            if (r.Length > 0 && map.TryGetValue(Key(r[0]), out var n)) r[0] = n;
        // Some layouts shift the column headings (no dash ruler under "Per Second"); the value columns are always second and third.
        if (t.Col("Per Second") < 0 && t.Headers.Count <= 3) { t.Headers.Clear(); t.Headers.AddRange(["", "Per Second", "Per Transaction"]); }
        if (t.Headers.Count > 0 && t.Headers[0].Length == 0 && t.Headers.Count > 1) { /* label column keeps an empty heading like the AWR text parser */ }
    }

    private static void FixMemory(AwrTable t)
    {
        // Some releases print a host memory of 0, which makes "% Host Mem used" meaningless (e.g. 931312%): drop the row.
        var host = t.RowByLabel("Host Mem (MB)");
        if (host is { } h && (h.NumAt(1) ?? 0) <= 0)
            t.Rows.RemoveAll(r => r.Length > 0 && Key(r[0]).StartsWith("% host mem", StringComparison.Ordinal));
    }

    private static void FixSql(AwrTable t, string key)
    {
        Rename(t, "elapsd time s", "Elapsed Time (s)");
        Rename(t, "elapsd time", "Elapsed Time (s)");
        Rename(t, "elapsed time s", "Elapsed Time (s)");
        if (key == "sql ordered by elapsed time") { Rename(t, "elap per exec s", "Elapsed Time per Exec (s)"); Rename(t, "elapsed per exec s", "Elapsed Time per Exec (s)"); }
        Rename(t, "total parses", "% Total Parses");
        var idCol = t.Col("SQL Id");
        if (idCol < 0)
        {
            var hv = t.Headers.FindIndex(h => Key(h).EndsWith("hash value", StringComparison.Ordinal));
            if (hv >= 0) AddColumn(t, "SQL Id", r => hv < r.Length ? r[hv].Trim() : "");
        }
        if (key == "sql ordered by elapsed time" && !t.HasColumn("%CPU"))
        {
            var el = t.Col("Elapsed Time (s)");
            var cpu = t.Col("CPU Time (s)");
            if (el >= 0 && cpu >= 0)
                AddColumn(t, "%CPU", r => NumberParser.Parse(r[el]) is > 0 and var e && NumberParser.Parse(r[cpu]) is { } c ? F(Math.Min(100, c / e * 100), "0.0") : "");
        }
    }

    // ------------------------------------------------------------------------------------------------ events

    private static void PrepareEvents(AwrReport report, AwrTable t, double? dbSec)
    {
        var timeIdx = t.Col("Total Wait Time (s)", "Total Wait Time (cs)", "Wait Time (s)", "Wait Time (cs)", "Time (s)", "Time (cs)");
        if (timeIdx >= 0)
        {
            if (Key(t.Headers[timeIdx]).EndsWith("cs", StringComparison.Ordinal))
            {
                foreach (var r in t.Rows)
                    if (timeIdx < r.Length && NumberParser.Parse(r[timeIdx]) is { } v) r[timeIdx] = F(v / 100);
                var avgIdx = t.Col("Avg wait (ms)", "Wait (ms)");
                var waitsIdx = t.Col("Waits");
                if (avgIdx >= 0 && waitsIdx >= 0)
                    foreach (var r in t.Rows)
                        if (NumberParser.Parse(r[waitsIdx]) is > 0 and var w && NumberParser.Parse(r[timeIdx]) is { } sec) r[avgIdx] = F(sec / w * 1000);
            }
            t.Headers[timeIdx] = "Total Wait Time (s)";
        }
        var avg = t.Col("Wait (ms)", "Avg wait (ms)");
        if (avg >= 0) t.Headers[avg] = "Avg wait (ms)";
        var to = t.Col("%Time -outs", "Timeouts");
        if (to >= 0 && Key(t.Headers[to]) == "timeouts") t.Headers[to] = "Timeouts";

        var ev = Math.Max(0, t.Col("Event"));
        var idle = t.Rows.Where(r => r.Length > ev && IsIdleEvent(r[ev])).ToList();
        if (idle.Count > 0)
        {
            t.Rows.RemoveAll(idle.Contains);
            var idleTitle = t.Title.StartsWith("Background", StringComparison.Ordinal) ? "Background Idle Wait Events" : "Idle Wait Events";
            report.Tables.Add(NewTable(idleTitle, [.. t.Headers], idle));
        }
        if (t.Title == "Foreground Wait Events" && timeIdx >= 0 && dbSec is > 0)
            AddColumn(t, "% DB time", r => NumberParser.Parse(r[timeIdx]) is { } s ? F(s / dbSec.Value * 100, "0.0") : "");
        AddColumn(t, "Wait Class", r => WaitClassOf(r[ev]));
    }

    /// <summary>Releases before 11g print no "DB Time(s)" / "DB CPU(s)" load rows; derive them (seconds per second) from the time model or Top 5.</summary>
    private static void AddLoadProfileTimes(AwrReport report, AwrTable? top, double? dbSec)
    {
        var lp = report.Table("Load Profile");
        var elapsed = report.Info.ElapsedMinutes * 60;
        if (lp == null || elapsed is not > 0 || dbSec is not > 0) return;
        var width = Math.Max(lp.Headers.Count, 3);
        string[] Row(string label, double sec) => [label, F(sec / elapsed.Value, "0.0"), "", .. Enumerable.Repeat("", width - 3)];
        if (lp.RowByLabel("DB Time(s)") == null) lp.Rows.Insert(0, Row("DB Time(s):", dbSec.Value));
        var cpu = report.Table("Time Model Statistics")?.RowByLabel("DB CPU")?.NumAt(1) ?? top?.RowByLabel("DB CPU")?.Num("Time (s)");
        if (cpu is > 0 && lp.RowByLabel("DB CPU(s)") == null) lp.Rows.Insert(1, Row("DB CPU(s):", cpu.Value));
    }

    private static void MoveIdleFromTop(AwrReport report, AwrTable? top)
    {
        if (top == null) return;
        var ev = Math.Max(0, top.Col("Event"));
        var idle = top.Rows.Where(r => r.Length > ev && IsIdleEvent(r[ev])).ToList();
        foreach (var r in top.Rows)
            if (r.Length > 5 && r[5].Length == 0 && r.Length > ev) r[5] = WaitClassOf(r[ev]);
        if (idle.Count == 0) return;
        top.Rows.RemoveAll(idle.Contains);
        report.Tables.Add(NewTable("Top 5 Idle Events (excluded)", [.. top.Headers], idle));
    }

    private static void AddWaitClasses(AwrReport report, AwrTable? top, double? dbSec)
    {
        var fg = report.Table("Foreground Wait Events");
        if (fg == null || dbSec is not > 0) return;
        var ev = Math.Max(0, fg.Col("Event"));
        var waitsIdx = fg.Col("Waits");
        var timeIdx = fg.Col("Total Wait Time (s)");
        var clsIdx = fg.Col("Wait Class");
        if (timeIdx < 0 || clsIdx < 0) return;
        var groups = fg.Rows.GroupBy(r => r[clsIdx])
            .Select(g => (Cls: g.Key.Length == 0 ? "Other" : g.Key, Waits: g.Sum(r => waitsIdx >= 0 ? NumberParser.Parse(r[waitsIdx]) ?? 0 : 0), Sec: g.Sum(r => NumberParser.Parse(r[timeIdx]) ?? 0)))
            .Where(g => g.Sec > 0).OrderByDescending(g => g.Sec).ToList();
        var cpu = report.Table("Time Model Statistics")?.RowByLabel("DB CPU")?.NumAt(1)
                  ?? top?.RowByLabel("DB CPU")?.Num("Time (s)");
        var rows = new List<string[]>();
        if (cpu is > 0) rows.Add(["DB CPU", "", F(cpu.Value, "0"), "", F(cpu.Value / dbSec.Value * 100, "0.0")]);
        foreach (var g in groups)
            rows.Add([g.Cls, F(g.Waits, "0"), F(g.Sec, "0"), g.Waits > 0 ? F(g.Sec / g.Waits * 1000) : "", F(g.Sec / dbSec.Value * 100, "0.0")]);
        if (rows.Count > 0)
            report.Tables.Add(NewTable("Wait Classes by Total Wait Time", ["Wait Class", "Waits", "Total Wait Time (sec)", "Avg Wait (ms)", "% DB time"], rows));
    }

    private static void ExpandHistogramLabels(AwrReport report)
    {
        var hist = report.Table("Wait Event Histogram");
        if (hist == null) return;
        var names = new List<string>();
        foreach (var t in report.Tables.Where(t => t.Title is "Foreground Wait Events" or "Background Wait Events" or "Idle Wait Events" || t.TitleStartsWith("Top 5")))
        {
            var c = Math.Max(0, t.Col("Event"));
            names.AddRange(t.Rows.Where(r => r.Length > c).Select(r => r[c].Trim()));
        }
        foreach (var r in hist.Rows)
        {
            if (r.Length == 0) continue;
            var label = r[0].Trim();
            if (label.Length < 20) continue;
            var full = names.FirstOrDefault(n => n.Length > label.Length && n.StartsWith(label, StringComparison.OrdinalIgnoreCase));
            if (full != null) r[0] = full;
        }
    }

    private static void AddSqlTextList(AwrReport report)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<string[]>();
        foreach (var t in report.Tables.Where(t => t.TitleStartsWith("SQL ordered by")))
        {
            var id = t.Col("SQL Id");
            var tx = t.Col("SQL Text");
            if (id < 0 || tx < 0) continue;
            foreach (var r in t.Rows)
                if (r.Length > Math.Max(id, tx) && r[id].Length > 0 && r[tx].Length > 0 && seen.Add(r[id])) rows.Add([r[id], r[tx]]);
        }
        if (rows.Count > 0) report.Tables.Add(NewTable("Complete List of SQL Text", ["SQL Id", "SQL Text"], rows));
    }

    private static void AddWarnings(AwrReport report, string[] lines)
    {
        var rows = new List<string[]>();
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (!Regex.IsMatch(l, @"^(WARNING|Warning)\s*:")) continue;
            var msg = l;
            while (i + 1 < lines.Length && lines[i + 1].Length > 0 && char.IsWhiteSpace(lines[i + 1][0]) && !lines[i + 1].TrimStart().StartsWith('-') && msg.Length < 600)
                msg += " " + lines[++i].Trim();
            rows.Add([Regex.Replace(msg, @"\s+", " ")]);
        }
        if (rows.Count > 0) report.Tables.Add(NewTable("Statspack Warnings", ["Message"], rows));
    }
}
