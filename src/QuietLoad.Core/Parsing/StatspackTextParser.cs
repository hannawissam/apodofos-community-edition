// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using QuietLoad.Core.Model;
using static QuietLoad.Core.Parsing.AwrTextParser;

namespace QuietLoad.Core.Parsing;

/// <summary>
/// Parser for Statspack plain-text reports (spreport.sql / sprepins.sql, Oracle 9i to 19c layouts).
/// <para>
/// The report is cut into sections (a title line carrying "DB/Inst:" or "for DB:", or a title underlined with "~~~~", ended
/// by the dashed closing line or the next title). Most sections are read with the AWR text parser, one section at a time, so
/// that a table can never run into the next one. Sections whose layout differs (Top 5 events, Host CPU, the SQL sections that
/// list hash values and wrapped SQL text, the wait histogram) have their own readers. Titles and column headers are then
/// normalised to the names the shared AWR rules and charts look up (see <see cref="StatspackNormalizer"/>).
/// The parser never throws; unknown or damaged sections become warnings.
/// </para>
/// </summary>
public static partial class StatspackTextParser
{
    private sealed record Section(int Start, int End, string Raw);

    [GeneratedRegex(@"^\s*(?<t>\S.*?)\s+(?:DB/Inst:|for DB:)\s.*$")]
    private static partial Regex DbInstTitle();

    [GeneratedRegex(@"^\s*-{40,}\s*$")]
    private static partial Regex ClosingLine();

    [GeneratedRegex(@"DB/Inst:\s*(?<db>[^/\s]+)/(?<inst>\S+)")]
    private static partial Regex DbInstNames();

    [GeneratedRegex(@"for DB:\s*(?<db>\S+)\s+Instance:\s*(?<inst>\S+)")]
    private static partial Regex ForDbNames();

    [GeneratedRegex(@"^\s*(?:(?<name>\S+)\s+)?(?<id>\d{6,})\s+(?<inst>\S+)\s+(?<num>\d+)\s+(?:(?<start>\d\d-\S+-\d\d\s+\d\d:\d\d(?::\d\d)?)\s+)?(?<rel>\d+\.\d+(?:\.\d+)+)\s+(?<rac>YES|NO)\b(?:\s+(?<host>\S+))?")]
    private static partial Regex DatabaseRow();

    [GeneratedRegex(@"^\s*Host\s+Name:\s*(?<host>\S+)(?:\s+Num CPUs:\s*(?<cpu>[\d.,'’]+))?(?:\s+Phys Memory \(MB\):\s*(?<mem>[\d.,'’]+))?", RegexOptions.IgnoreCase)]
    private static partial Regex HostLine();

    [GeneratedRegex(@"^\s*(?<host>\S+)\s+(?<plat>[A-Za-z][A-Za-z0-9 \-_/\.\(\)]*?)\s+(?<cpus>\d+)\s+(?<cores>\d+)\s+(?<soc>\d+)\s+(?<mem>[\d.,'’]*\d)\s*$")]
    private static partial Regex HostPlatformRow();

    [GeneratedRegex(@"^\s*(?<k>Begin|End) Snap:\s+(?<id>\d+)\s+(?<time>\d\d-\S+-\d\d\s+\d\d:\d\d:\d\d)(?:\s+(?<sess>\d+))?(?:\s+(?<curs>[\d.,]+))?(?:\s+(?<comment>\S.*))?$")]
    private static partial Regex SnapLine();

    [GeneratedRegex(@"^\s*(?<k>Elapsed|DB Time):\s*(?<v>[\d.,'’]+)\s*\((?:mins?|min\.|Min\.?|minutes?)\)", RegexOptions.IgnoreCase)]
    private static partial Regex MinsLine();

    [GeneratedRegex(@"^(?<label>\S.*?)\s{2,}(?<nums>[-\d,.#'’]+(?:\s+[-\d,.#'’]+)*)(?:\s+(?<cls>[A-Za-z][A-Za-z0-9 /]*?))?\s*$")]
    private static partial Regex EventRow();

    [GeneratedRegex(@"([A-Za-z%][A-Za-z0-9 %/()&\-\.>]{1,45}?)\s*:\s+([-+]?[\d.,'’]*\d[KMGT]?)")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"CPUs:\s*(?<cpus>\d+)(?:\s+Cores:\s*(?<cores>\d+))?(?:\s+Sockets:\s*(?<sock>\d+))?")]
    private static partial Regex CpuTitle();

    public static AwrReport Parse(string text, string sourcePath = "")
    {
        using var scope = NumberConventions.Begin(text);
        var report = ParseCore(text, sourcePath);
        scope.Apply(report, text);
        return report;
    }

    private static AwrReport ParseCore(string text, string sourcePath = "")
    {
        var report = new AwrReport { SourcePath = sourcePath, Format = AwrReportFormat.Text, Kind = ReportKind.Statspack };
        string[] lines = [];
        var anchors = new Dictionary<AwrTable, int>();
        try
        {
            lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Select(l => l.Replace('\t', ' ').TrimEnd()).ToArray();
            BuildReport(lines, report, anchors);
        }
        catch (Exception ex)
        {
            report.ParseWarnings.Add("Statspack report could not be fully parsed: " + ex.Message);
        }

        // Ids and indexes are init-only: rebuild the tables with their final position.
        var final = new List<AwrTable>();
        var remapped = new Dictionary<AwrTable, int>();
        for (var i = 0; i < report.Tables.Count; i++)
        {
            var o = report.Tables[i];
            var n = new AwrTable { Id = $"awr-t-{i}", Index = i, Title = o.Title, Headers = o.Headers, Rows = o.Rows };
            final.Add(n);
            if (anchors.TryGetValue(o, out var line)) remapped[n] = line;
        }
        report.Tables.Clear();
        report.Tables.AddRange(final);
        anchors = remapped;
        if (report.Tables.Count == 0) report.ParseWarnings.Add("No tables were recognised in the Statspack report.");
        var starts = new Dictionary<int, int>();
        foreach (var (t, line) in anchors) if (report.Tables.Contains(t)) starts.TryAdd(line, t.Index);
        report.SanitizedHtml = RenderHtml(lines, starts);
        return report;
    }

    // ------------------------------------------------------------------------------------------------ sections

    private static void BuildReport(string[] lines, AwrReport report, Dictionary<AwrTable, int> anchors)
    {
        var sections = FindSections(lines, out var headerEnd);
        ParseHeader(lines, headerEnd, report, anchors);

        var tables = new List<(AwrTable Table, int Line)>();
        var sqlCapture = new List<string[]>();
        foreach (var s in sections)
        {
            try
            {
                foreach (var t in ParseSection(lines, s, sqlCapture)) tables.Add((t, s.Start));
            }
            catch (Exception ex)
            {
                report.ParseWarnings.Add($"Section '{s.Raw.Trim()}' (line {s.Start + 1}) could not be read: {ex.Message}");
            }
        }

        // Merge continuation pages (Instance Activity Stats, Latch Activity ...) that repeat the same title and headers.
        var merged = new List<(AwrTable Table, int Line)>();
        var byKey = new Dictionary<string, AwrTable>();
        foreach (var (t, line) in tables)
        {
            var key = AwrTable.Normalize(t.Title) + "|" + string.Join(",", t.Headers.Select(AwrTable.Normalize));
            if (byKey.TryGetValue(key, out var first)) { first.Rows.AddRange(t.Rows); continue; }
            byKey[key] = t;
            merged.Add((t, line));
        }
        // Different layouts under one title get distinguishing suffixes.
        var titleSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (t, _) in merged)
        {
            var n = titleSeen.TryGetValue(t.Title, out var c) ? c + 1 : 1;
            titleSeen[t.Title] = n;
            if (n > 1)
                t.Title += t.HasColumn("Begin Value") ? " - Absolute Values" : t.HasColumn("per Hour") ? " - Derived" : $" ({n})";
        }

        var ordered = merged.OrderBy(x => x.Line).ToList();
        foreach (var (t, line) in ordered)
        {
            report.Tables.Add(t);
            anchors[t] = line;
        }
        if (sqlCapture.Count > 0)
            report.Tables.Add(NewTable("SQL Capture Summary", ["Section", "Captured SQL %", "Threshold"], sqlCapture));
        StatspackNormalizer.Normalize(report, lines);
    }

    /// <summary>A "~~~~" underline, possibly followed by column rulers or headings on the same line.</summary>
    private static bool IsTildeish(string l) => l.TrimStart().StartsWith("~~~", StringComparison.Ordinal);

    private static List<Section> FindSections(string[] lines, out int headerEnd)
    {
        var elapsedIdx = -1;
        for (var i = 0; i < lines.Length && i < 60; i++)
            if (Regex.IsMatch(lines[i], @"^\s*Elapsed:", RegexOptions.IgnoreCase)) { elapsedIdx = i; break; }
        headerEnd = elapsedIdx >= 0 ? elapsedIdx + 1 : 0;
        if (elapsedIdx >= 0 && elapsedIdx + 1 < lines.Length && Regex.IsMatch(lines[elapsedIdx + 1], @"^\s*DB Time:", RegexOptions.IgnoreCase)) headerEnd++;

        var starts = new List<(int Line, string Raw)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Length == 0 || ClosingLine().IsMatch(l) || IsTildeish(l)) continue;
            var t = l.TrimStart();
            if (t.StartsWith('-') || t.StartsWith('>')) continue;
            var m = DbInstTitle().Match(l);
            if (m.Success) { starts.Add((i, m.Groups["t"].Value)); continue; }
            if (Regex.IsMatch(l, @"^\s*Shared Pool Statistics\s+Begin")) { starts.Add((i, "Shared Pool Statistics")); continue; }
            if (i + 1 < lines.Length && IsTildeish(lines[i + 1]))
            {
                var title = Regex.Split(t, @"\s{3,}")[0].Trim();
                if (title.Length > 0) starts.Add((i, title));
            }
        }
        var hdr = headerEnd;
        starts = starts.Where(s => s.Line >= hdr).ToList();
        if (elapsedIdx < 0)
            starts = starts.Where(s => !Regex.IsMatch(s.Raw, @"^(Database|Snapshot|Host|DB Name|Snap Id)", RegexOptions.IgnoreCase)).ToList();

        var result = new List<Section>();
        for (var k = 0; k < starts.Count; k++)
        {
            var (line, raw) = starts[k];
            var end = k + 1 < starts.Count ? starts[k + 1].Line : lines.Length;
            for (var j = line + 1; j < end; j++)
                if (ClosingLine().IsMatch(lines[j]) || lines[j].StartsWith("End of Report", StringComparison.OrdinalIgnoreCase)) { end = j; break; }
            result.Add(new Section(line, end, raw));
            if (headerEnd == 0 && k == 0) headerEnd = line;
        }
        if (result.Count > 0 && headerEnd == 0) headerEnd = result[0].Start;
        if (headerEnd == 0) headerEnd = Math.Min(lines.Length, 25);
        return result;
    }

    // --------------------------------------------------------------------------------------------------- header

    private static void ParseHeader(string[] lines, int headerEnd, AwrReport report, Dictionary<AwrTable, int> anchors)
    {
        var info = report.Info;
        var end = Math.Min(headerEnd, lines.Length);
        for (var i = 0; i < end; i++)
        {
            var l = lines[i];
            var m = DatabaseRow().Match(l);
            if (m.Success && info.DbId.Length == 0)
            {
                info.DbName = m.Groups["name"].Success ? m.Groups["name"].Value : "";
                info.DbId = m.Groups["id"].Value;
                info.InstanceName = m.Groups["inst"].Value;
                info.InstanceNumber = int.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture);
                info.StartupTime = m.Groups["start"].Value;
                info.Release = m.Groups["rel"].Value;
                info.IsRac = m.Groups["rac"].Value == "YES";
                if (m.Groups["host"].Success) info.HostName = m.Groups["host"].Value;
                continue;
            }
            m = HostLine().Match(l);
            if (m.Success)
            {
                info.HostName = m.Groups["host"].Value;
                if (NumberParser.Parse(m.Groups["cpu"].Value) is { } cpu && cpu > 0) info.Cpus = (int)cpu;
                if (NumberParser.Parse(m.Groups["mem"].Value) is { } mb && mb > 0) info.MemoryGb = Math.Round(mb / 1024, 2);
                continue;
            }
            if (info.HostName.Length == 0 && i > 0 && lines[i - 1].Contains("Platform", StringComparison.OrdinalIgnoreCase) | (i > 1 && lines[i - 2].Contains("Platform", StringComparison.OrdinalIgnoreCase)))
            {
                var hp = HostPlatformRow().Match(l);
                if (hp.Success)
                {
                    info.HostName = hp.Groups["host"].Value; info.Platform = hp.Groups["plat"].Value.Trim();
                    info.Cpus = int.Parse(hp.Groups["cpus"].Value, CultureInfo.InvariantCulture); info.Cores = int.Parse(hp.Groups["cores"].Value, CultureInfo.InvariantCulture);
                    info.Sockets = int.Parse(hp.Groups["soc"].Value, CultureInfo.InvariantCulture); info.MemoryGb = NumberParser.Parse(hp.Groups["mem"].Value);
                    continue;
                }
            }
            m = SnapLine().Match(l);
            if (m.Success)
            {
                var begin = m.Groups["k"].Value == "Begin";
                var sess = NumberParser.Parse(m.Groups["sess"].Value);
                if (begin) { info.BeginSnapId = m.Groups["id"].Value; info.BeginSnapTime = m.Groups["time"].Value.Trim(); info.BeginSessions = sess; }
                else { info.EndSnapId = m.Groups["id"].Value; info.EndSnapTime = m.Groups["time"].Value.Trim(); info.EndSessions = sess; }
                continue;
            }
            m = MinsLine().Match(l);
            if (m.Success)
            {
                var v = NumberParser.Parse(m.Groups["v"].Value);
                if (m.Groups["k"].Value.Equals("Elapsed", StringComparison.OrdinalIgnoreCase)) info.ElapsedMinutes = v;
                else info.DbTimeMinutes = v;
            }
        }

        // Instance and database names from the section titles when the header row has no name (older layouts).
        var all = string.Join("\n", lines.Take(Math.Min(lines.Length, 400)));
        var di = DbInstNames().Match(all);
        if (di.Success)
        {
            if (info.DbName.Length == 0) info.DbName = di.Groups["db"].Value;
            if (info.InstanceName.Length == 0) info.InstanceName = di.Groups["inst"].Value;
        }
        else if (ForDbNames().Match(all) is { Success: true } fd)
        {
            if (info.DbName.Length == 0) info.DbName = fd.Groups["db"].Value;
            if (info.InstanceName.Length == 0) info.InstanceName = fd.Groups["inst"].Value;
        }

        var dbTable = new AwrTable
        {
            Id = "", Index = 0, Title = "Database Instance Information",
            Headers = ["DB Name", "DB Id", "Instance", "Inst Num", "Startup Time", "Release", "RAC"],
            Rows = [[info.DbName, info.DbId, info.InstanceName, info.InstanceNumber.ToString(CultureInfo.InvariantCulture), info.StartupTime, info.Release, info.IsRac ? "YES" : "NO"]]
        };
        var hostTable = new AwrTable
        {
            Id = "", Index = 0, Title = "Host Information",
            Headers = ["Host Name", "Platform", "CPUs", "Cores", "Sockets", "Memory (GB)"],
            Rows = [[info.HostName, info.Platform, info.Cpus?.ToString(CultureInfo.InvariantCulture) ?? "", info.Cores?.ToString(CultureInfo.InvariantCulture) ?? "", info.Sockets?.ToString(CultureInfo.InvariantCulture) ?? "", info.MemoryGb?.ToString("0.##", CultureInfo.InvariantCulture) ?? ""]]
        };
        var snapTable = new AwrTable
        {
            Id = "", Index = 0, Title = "Snapshot Information",
            Headers = ["", "Snap Id", "Snap Time", "Sessions", "Curs/Sess"],
            Rows =
            [
                ["Begin Snap", info.BeginSnapId, info.BeginSnapTime, info.BeginSessions?.ToString(CultureInfo.InvariantCulture) ?? "", ""],
                ["End Snap", info.EndSnapId, info.EndSnapTime, info.EndSessions?.ToString(CultureInfo.InvariantCulture) ?? "", ""],
                ["Elapsed", "", info.ElapsedMinutes is { } em ? NumberParser.Format(em, "0.####") : "", "", ""]
            ]
        };
        if (info.DbTimeMinutes is { } dt) snapTable.Rows.Add(["DB Time", "", NumberParser.Format(dt, "0.####"), "", ""]);
        foreach (var t in new[] { dbTable, hostTable, snapTable }) { report.Tables.Add(t); anchors[t] = 0; }
        if (info.DbId.Length == 0) report.ParseWarnings.Add("The database header row was not found; database name is unknown.");
        if (info.ElapsedMinutes is null) report.ParseWarnings.Add("Snapshot elapsed time not found; load calculations may be incomplete.");
    }

    // -------------------------------------------------------------------------------------------------- sections

    private static readonly string[] SkippedSections = ["top process memory", "process memory summary"];

    private static List<AwrTable> ParseSection(string[] lines, Section s, List<string[]> sqlCapture)
    {
        var title = StatspackNormalizer.CanonicalTitle(s.Raw);
        var key = AwrTable.Normalize(title);
        if (SkippedSections.Any(k => key.StartsWith(k, StringComparison.Ordinal))) return [];

        var body = lines[s.Start..s.End];
        if (key.StartsWith("top 5", StringComparison.Ordinal)) return OneOrNone(ReadTopEvents(body, title));
        if (key.StartsWith("host cpu", StringComparison.Ordinal)) return OneOrNone(ReadHostCpu(body, s.Raw));
        if (key == "wait event histogram") return OneOrNone(ReadHistogram(body, title));
        if (key.StartsWith("sql ordered by", StringComparison.Ordinal)) return OneOrNone(ReadSqlSection(body, title, sqlCapture));
        if (key.StartsWith("instance cpu", StringComparison.Ordinal)) return OneOrNone(ReadInstanceCpu(body));

        var parsed = AwrTextParser.Parse(string.Join("\n", body), "");
        var list = new List<AwrTable>();
        var k = 0;
        foreach (var t in parsed.Tables)
        {
            if (t.Rows.Count == 0) continue;
            if (k > 0 && key == "sga memory summary") break;
            t.Title = k == 0 ? title : $"{title} ({k + 1})";
            k++;
            list.Add(t);
        }
        if (key == "load profile") AddLoadRatios(body, list);
        return list;
    }

    private static List<AwrTable> OneOrNone(AwrTable? t) => t == null ? [] : [t];

    private static AwrTable NewTable(string title, List<string> headers, List<string[]> rows) =>
        new() { Id = "", Index = 0, Title = title, Headers = headers, Rows = rows };

    /// <summary>"% Blocks changed per Read: 0.05   Recursive Call %: 57.52" lines under the Load Profile.</summary>
    private static void AddLoadRatios(string[] body, List<AwrTable> list)
    {
        var rows = new List<string[]>();
        foreach (var l in body)
        {
            if (!l.TrimStart().StartsWith('%') && !l.TrimStart().StartsWith("Rollback per", StringComparison.Ordinal)) continue;
            foreach (Match m in KeyValue().Matches(l)) rows.Add([m.Groups[1].Value.Trim() + ":", m.Groups[2].Value]);
        }
        if (rows.Count > 0) list.Add(NewTable("Load Profile Ratios", ["", "Value"], rows));
    }

    // ----------------------------------------------------------------------------------------- Top 5 events

    private static AwrTable? ReadTopEvents(string[] body, string title)
    {
        var ruler = Array.FindIndex(body, IsDashRuler);
        if (ruler < 0) return null;
        var headText = string.Join(" ", body[Math.Max(0, ruler - 3)..ruler]);
        var cs = headText.Contains("(cs)", StringComparison.OrdinalIgnoreCase);
        var rows = new List<string[]>();
        for (var i = ruler + 1; i < body.Length; i++)
        {
            var l = body[i];
            if (l.Length == 0) break;
            var m = EventRow().Match(l.TrimStart());
            if (!m.Success) continue;
            var nums = m.Groups["nums"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string waits = "", time, avg = "", pct;
            switch (nums.Length)
            {
                case >= 4: waits = nums[0]; time = nums[1]; avg = nums[2]; pct = nums[3]; break;
                case 3: waits = nums[0]; time = nums[1]; pct = nums[2]; break;
                case 2: time = nums[0]; pct = nums[1]; break;
                default: continue;
            }
            var name = m.Groups["label"].Value.Trim();
            if (name.Equals("CPU time", StringComparison.OrdinalIgnoreCase)) name = "DB CPU";
            var t = NumberParser.Parse(time);
            if (cs && t is not null) time = NumberParser.Format(t.Value / 100);
            var w = NumberParser.Parse(waits);
            var ts = NumberParser.Parse(time);
            if ((cs || avg.Length == 0 || avg.Contains('#')) && w is > 0 && ts is not null) avg = NumberParser.Format(ts.Value / w.Value * 1000);
            rows.Add([name, waits, time, avg, pct, m.Groups["cls"].Success ? m.Groups["cls"].Value.Trim() : ""]);
        }
        return NewTable(title, ["Event", "Waits", "Time (s)", "Avg wait (ms)", "% DB time", "Wait Class"], rows);
    }

    // ----------------------------------------------------------------------------------------------- Host CPU

    private static AwrTable? ReadHostCpu(string[] body, string rawTitle)
    {
        var ruler = Array.FindIndex(body, IsDashRuler);
        if (ruler < 0 || ruler + 1 >= body.Length) return null;
        var row = body[ruler + 1];
        var nums = Tokens(row).Select(t => t.Text).Where(x => NumberParser.Parse(x) is not null).ToList();
        if (nums.Count < 4) return null;
        var m = CpuTitle().Match(rawTitle + " " + body[0]);
        // begin, end load average (optional), then User, System, Idle, WIO, WCPU (optional)
        string lb = "", le = "", user, sys, idle, wio, wcpu = "";
        if (nums.Count >= 6) { lb = nums[0]; le = nums[1]; user = nums[2]; sys = nums[3]; idle = nums[4]; wio = nums[5]; if (nums.Count > 6) wcpu = nums[6]; }
        else { user = nums[0]; sys = nums[1]; idle = nums[2]; wio = nums[3]; }
        var cpus = m.Success ? m.Groups["cpus"].Value : "";
        return NewTable($"Host CPU (CPUs: {cpus})".Replace("(CPUs: )", "").Trim(),
            ["CPUs", "Cores", "Sockets", "Load Average Begin", "Load Average End", "%User", "%System", "%WIO", "%Idle", "%WCPU"],
            [[cpus, m.Groups["cores"].Value, m.Groups["sock"].Value, lb, le, user, sys, wio, idle, wcpu]]);
    }

    private static AwrTable? ReadInstanceCpu(string[] body)
    {
        var total = "";
        var busy = "";
        var rm = "";
        foreach (var l in body)
        {
            var m = Regex.Match(l, @"^\s*(?<k>.*?):\s*(?<v>[-\d,.'’]+)?\s*$");
            if (!m.Success) continue;
            var k = m.Groups["k"].Value.ToLowerInvariant();
            if (k.Contains("total cpu")) total = m.Groups["v"].Value;
            else if (k.Contains("busy")) busy = m.Groups["v"].Value;
            else if (k.Contains("waiting for cpu")) rm = m.Groups["v"].Value;
        }
        if (total.Length == 0 && busy.Length == 0) return null;
        return NewTable("Instance CPU", ["%Total CPU", "%Busy CPU", "%DB time waiting for CPU (Resource Manager)"], [[total, busy, rm]]);
    }

    // ------------------------------------------------------------------------------------------ wait histogram

    private static AwrTable? ReadHistogram(string[] body, string title)
    {
        var ruler = Array.FindIndex(body, IsDashRuler);
        if (ruler < 1) return null;
        var cols = Columns(body[ruler]);
        if (cols.Count < 3) return null;
        var head = Tokens(body[ruler - 1]);
        var headers = new List<string>();
        for (var c = 0; c < cols.Count; c++)
        {
            var from = c == 0 ? 0 : cols[c - 1].End + 1;
            var toks = head.Where(t => t.Start >= from && t.Start <= cols[c].End + 1 && t.Start >= (c == 0 ? 0 : cols[c - 1].End + 1)).Select(t => t.Text);
            headers.Add(string.Join(' ', toks));
        }
        headers[0] = "Event";
        if (headers.Count > 1) headers[1] = "Total Waits";
        var rows = new List<string[]>();
        for (var i = ruler + 1; i < body.Length; i++)
        {
            var l = body[i];
            if (l.Length == 0) break;
            if (!FitsGrid(l, cols)) continue;
            rows.Add([.. SliceRow(l, cols).Select(x => x.Trim())]);
        }
        return NewTable(title, headers, rows);
    }

    // ------------------------------------------------------------------------------------------- SQL sections

    private static AwrTable? ReadSqlSection(string[] body, string title, List<string[]> sqlCapture)
    {
        var ruler = Array.FindIndex(body, IsDashRuler);
        var notes = body.Take(ruler < 0 ? body.Length : ruler).Select(l => l.Trim()).Where(l => l.StartsWith("->", StringComparison.Ordinal)).ToList();
        var cap = notes.Select(n => Regex.Match(n, @"Captured SQL accounts? for\s+([\d.,]+)%")).FirstOrDefault(m => m.Success);
        var threshold = notes.FirstOrDefault(n => n.Contains("Threshold", StringComparison.OrdinalIgnoreCase)) ?? "";
        sqlCapture.Add([title, cap?.Groups[1].Value ?? "", threshold.TrimStart('-', '>', ' ')]);
        if (ruler < 1) return null;

        var cols = Columns(body[ruler]);
        var heads = new List<List<Token>>();
        for (var h = ruler - 1; h >= 0 && ruler - h <= 3 && body[h].Length > 0 && !body[h].TrimStart().StartsWith("->", StringComparison.Ordinal); h--)
            heads.Insert(0, Tokens(body[h]));
        var headers = BuildHeaders(heads, cols);
        var rows = new List<string[]>();
        var modules = new List<string>();
        var texts = new List<StringBuilder>();
        for (var i = ruler + 1; i < body.Length; i++)
        {
            var l = body[i];
            if (l.Length == 0) continue;
            var toks = Tokens(l);
            var isRow = FitsGrid(l, cols) && toks.Count >= Math.Max(2, cols.Count - 1) && NumberParser.Parse(toks[0].Text) is not null
                        && LooksLikeRow(toks, cols.Count);
            if (isRow)
            {
                var cells = SliceRow(l, cols);
                rows.Add([.. cells]);
                modules.Add("");
                texts.Add(new StringBuilder());
                continue;
            }
            if (rows.Count == 0) continue;
            if (l.TrimStart().StartsWith("Module:", StringComparison.OrdinalIgnoreCase)) { modules[^1] = l.TrimStart()[7..].Trim(); continue; }
            var sb = texts[^1];
            sb.Append(l);
            // Statspack wraps SQL text at 64 characters inside words; shorter lines end at an original line break.
            if (l.Length != 64) sb.Append(' ');
        }
        headers = [.. headers, .. Enumerable.Repeat("", Math.Max(0, cols.Count - headers.Count))];
        headers.Add("SQL Module");
        headers.Add("SQL Text");
        for (var i = 0; i < rows.Count; i++)
            rows[i] = [.. rows[i], modules[i], Regex.Replace(texts[i].ToString().Trim(), @"\s+$", "")];
        return NewTable(title, headers, rows);
    }

    private static bool LooksLikeRow(List<Token> toks, int cols) =>
        toks.Count(t => NumberParser.Parse(t.Text) is not null || t.Text.StartsWith('#') || Regex.IsMatch(t.Text, @"^[0-9a-z]{13}$")) >= Math.Max(2, cols - 2);

    // ------------------------------------------------------------------------------------------------ HTML view

    private static string RenderHtml(string[] lines, Dictionary<int, int> tableStarts)
    {
        var sb = new StringBuilder(lines.Sum(l => l.Length + 1) + 1024);
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\">")
          .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline';\">")
          .Append("<style>body{margin:12px;background:#fff;color:#111}pre{font:12px/1.35 Consolas,'Cascadia Mono',monospace;margin:0}")
          .Append("pre.awr-highlight,table.awr-highlight{outline:3px solid #f59e0b;outline-offset:3px}</style></head><body>");
        sb.Append("<pre>");
        for (var i = 0; i < lines.Length; i++)
        {
            if (tableStarts.TryGetValue(i, out var idx))
            {
                sb.Append("</pre>");
                sb.Append("<pre id=\"awr-t-").Append(idx).Append("\">");
            }
            sb.Append(WebUtility.HtmlEncode(lines[i])).Append('\n');
        }
        sb.Append("</pre></body></html>");
        return sb.ToString();
    }
}
