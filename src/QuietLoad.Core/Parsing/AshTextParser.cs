// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using QuietLoad.Core.Model;

namespace QuietLoad.Core.Parsing;

/// <summary>
/// Parser for ASH plain-text reports (ashrpt.sql / DBMS_WORKLOAD_REPOSITORY.ASH_REPORT_TEXT).
/// <para>
/// The text layout is one block per section: a title line ("Top User Events   DB/Inst: DB/inst (...)"), optional notes,
/// column headings, a "---- ----" ruler and the rows, closed by an indented dash line. Some sections use two stacked
/// rulers (a second heading line belongs to a second row line); each pair of lines becomes one row. Rows that continue a
/// previous entry (blank leading key cells) inherit those keys, which makes the result look like the HTML report's
/// tables. The parser never throws; unrecognised shapes are skipped with a warning.
/// </para>
/// </summary>
public static partial class AshTextParser
{
    private static readonly string[] KnownTitles =
    [
        "Top User Events", "Top Background Events", "Top Cluster Events", "Top Event P1/P2/P3 Values", "Top Service/Module",
        "Top Client IDs", "Top SQL Command Types", "Top Phases of Execution", "Top Remote Instances", "Top SQL with Top Events",
        "Top SQL with Top Row Sources", "Top SQL using literals", "Top Parsing Module/Action", "Complete List of SQL Text",
        "Top PL/SQL Procedures", "Top Java Workload", "Top Call Types", "Top Sessions", "Top Blocking Sessions",
        "Top Sessions running PQs", "Top DB Objects", "Top DB Files", "Top Latches", "Activity Over Time"
    ];

    [GeneratedRegex(@"^(?<t>\S.*?)\s{2,}DB/Inst:")]
    private static partial Regex TitleWithInst();

    [GeneratedRegex(@"^\s*(?<k>Analysis Begin Time|Analysis End Time|Elapsed Time|Begin Data Source|End Data Source|Sample Count|Average Active Sessions|Avg\. Active Sessions? per CPU|Report Target)\s*:\s*(?<v>.*?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderPair();

    [GeneratedRegex(@"^\s{3,}-{20,}\s*$")]
    private static partial Regex Footer();

    private readonly record struct Col(int Start, int End);
    private readonly record struct Tok(int Start, string Text);

    public static AwrReport Parse(string text, string sourcePath = "")
    {
        using var scope = NumberConventions.Begin(text);
        var report = ParseCore(text, sourcePath);
        scope.Apply(report, text);
        return report;
    }

    private static AwrReport ParseCore(string text, string sourcePath = "")
    {
        var report = new AwrReport { SourcePath = sourcePath, Format = AwrReportFormat.Text, Kind = ReportKind.Ash };
        var starts = new Dictionary<int, int>();
        string[] lines = [];
        try
        {
            lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.Replace('\t', ' ').TrimEnd()).ToArray();
            Build(lines, report, starts);
        }
        catch (Exception ex)
        {
            report.ParseWarnings.Add("ASH text report could not be fully parsed: " + ex.Message);
        }
        try { AshInfoExtractor.Populate(report); }
        catch (Exception ex) { report.ParseWarnings.Add("ASH header information could not be read: " + ex.Message); }
        report.SanitizedHtml = RenderHtml(lines, starts);
        return report;
    }

    // ---------------------------------------------------------------------------------------------------- structure

    private static int TitleLine(string[] lines, int i, out string title)
    {
        title = "";
        var l = lines[i];
        if (l.Length == 0 || char.IsWhiteSpace(l[0])) return -1;
        var m = TitleWithInst().Match(l);
        if (m.Success) { title = m.Groups["t"].Value.Trim(); return i; }
        var t = l.Trim();
        foreach (var k in KnownTitles)
            if (t.Equals(k, StringComparison.OrdinalIgnoreCase)) { title = k; return i; }
        return -1;
    }

    private static void Build(string[] lines, AwrReport report, Dictionary<int, int> starts)
    {
        // section starts
        var sections = new List<(int Line, string Title)>();
        for (var i = 0; i < lines.Length; i++)
            if (TitleLine(lines, i, out var t) >= 0) sections.Add((i, t));

        var headerEnd = sections.Count > 0 ? sections[0].Line : lines.Length;
        var drafts = new List<(int First, string Title, List<string> Headers, List<string[]> Rows)>();

        // --- header zone: DB / CPU tables and the key: value block
        var headerLines = lines.Take(headerEnd).ToArray();
        for (var r = 0; r < headerLines.Length; r++)
        {
            if (!IsRuler(headerLines[r]) || r == 0 || headerLines[r - 1].Length == 0) continue;
            if (r + 1 >= headerLines.Length || headerLines[r + 1].Length == 0) continue;
            var cols = Columns(headerLines[r]);
            var heads = HeadingLines(headerLines, r, 2);
            var headers = BuildHeaders(heads.Select(h => Tokens(headerLines[h])).ToList(), cols);
            var rows = new List<string[]>();
            for (var k = r + 1; k < headerLines.Length && headerLines[k].Length > 0 && !IsRuler(headerLines[k]); k++)
                rows.Add(Slice(headerLines[k], cols));
            drafts.Add((heads.Count > 0 ? heads[0] : r, "", headers, rows));
        }
        var pairs = new List<string[]>();
        var firstPair = -1;
        string beginSrc = "", endSrc = "";
        for (var i = 0; i < headerEnd; i++)
        {
            var m = HeaderPair().Match(lines[i]);
            if (!m.Success) continue;
            if (firstPair < 0) firstPair = i;
            var k = m.Groups["k"].Value.Trim();
            var v = m.Groups["v"].Value.Trim();
            if (k.StartsWith("Begin Data", StringComparison.OrdinalIgnoreCase)) { beginSrc = v; continue; }
            if (k.StartsWith("End Data", StringComparison.OrdinalIgnoreCase)) { endSrc = v; continue; }
            pairs.Add([k + ":", v, ""]);
        }
        if (pairs.Count > 0)
        {
            foreach (var p in pairs)
            {
                if (p[0].StartsWith("Analysis Begin", StringComparison.OrdinalIgnoreCase)) p[2] = beginSrc;
                else if (p[0].StartsWith("Analysis End", StringComparison.OrdinalIgnoreCase)) p[2] = endSrc;
            }
            drafts.Add((firstPair, "", ["", "Sample Time", "Data Source"], pairs));
        }

        // --- sections
        for (var s = 0; s < sections.Count; s++)
        {
            var from = sections[s].Line + 1;
            var to = s + 1 < sections.Count ? sections[s + 1].Line : lines.Length;
            for (var k = from; k < to; k++)
                if (lines[k].StartsWith("End of Report", StringComparison.OrdinalIgnoreCase)) { to = k; break; }
            try
            {
                var d = ReadSection(lines, from, to, sections[s].Title, sections[s].Line);
                if (d != null) drafts.Add(d.Value);
            }
            catch (Exception ex)
            {
                report.ParseWarnings.Add($"Section '{sections[s].Title}' could not be read: {ex.Message}");
            }
        }

        drafts.Sort((a, b) => a.First.CompareTo(b.First));
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in drafts)
        {
            if (d.Rows.Count == 0 && d.Title.Length == 0) continue;
            var t = new AwrTable
            {
                Id = $"awr-t-{report.Tables.Count}",
                Index = report.Tables.Count,
                Title = d.Title.Length > 0 ? d.Title : $"Table {report.Tables.Count + 1}",
                Headers = d.Headers,
                Rows = d.Rows
            };
            counts[t.Title] = counts.TryGetValue(t.Title, out var n) ? n + 1 : 1;
            if (counts[t.Title] > 1) t.Title = $"{t.Title} ({counts[t.Title]})";
            starts.TryAdd(d.First, t.Index);
            report.Tables.Add(t);
        }
    }

    private static (int First, string Title, List<string> Headers, List<string[]> Rows)? ReadSection(string[] lines, int from, int to, string title, int titleLine)
    {
        // end at the closing dash line
        for (var k = from; k < to; k++)
            if (Footer().IsMatch(lines[k]) && !IsRulerLike(lines, k)) { to = k; break; }

        var rulers = new List<int>();
        for (var k = from; k < to; k++)
            if (IsRuler(lines[k])) rulers.Add(k);
        if (rulers.Count == 0) return null;

        var a = rulers[0];
        var colsA = Columns(lines[a]);
        var headsA = HeadingLines(lines, a, 3, from);
        var headersA = BuildHeaders(headsA.Select(h => Tokens(lines[h])).ToList(), colsA);

        // stacked ruler: B follows within 3 lines, no blank line between
        var b = -1;
        for (var k = a + 1; k <= Math.Min(a + 4, to - 1); k++)
        {
            if (lines[k].Length == 0) break;
            if (IsRuler(lines[k]) && k > a + 1) { b = k; break; }
            if (Tokens(lines[k]).Any(t => NumberParserLike(t.Text))) break;
        }
        List<Col>? colsB = null;
        var headers = headersA;
        if (b > 0)
        {
            colsB = Columns(lines[b]);
            var between = new List<List<Tok>>();
            for (var k = a + 1; k < b; k++) between.Add(Tokens(lines[k]));
            headers = [.. headersA, .. BuildHeaders(between, colsB)];
        }

        var rows = new List<string[]>();
        var start = (b > 0 ? b : a) + 1;
        string[]? prev = null;
        for (var k = start; k < to; k++)
        {
            var line = lines[k];
            if (line.Length == 0) { if (rows.Count > 0) break; continue; }
            if (line.TrimStart().StartsWith("->", StringComparison.Ordinal) || IsRuler(line)) continue;
            var cells = Slice(line, colsA).ToList();
            if (colsB != null)
            {
                var bLine = k + 1 < to && lines[k + 1].Length > 0 ? lines[k + 1] : "";
                cells.AddRange(Slice(bLine, colsB));
                if (bLine.Length > 0) k++;
            }
            if (cells.All(c => c.Length == 0)) continue;
            // continuation of the previous entry: inherit blank leading key cells
            if (prev != null && cells[0].Length == 0)
            {
                var firstFilled = cells.FindIndex(c => c.Length > 0);
                var limit = firstFilled < 0 ? 0 : Math.Min(firstFilled, colsA.Count);
                for (var c = 0; c < limit && c < prev.Length; c++) cells[c] = prev[c];
            }
            var arr = cells.ToArray();
            rows.Add(arr);
            prev = arr;
        }
        var width = Math.Max(headers.Count, rows.Count == 0 ? 0 : rows.Max(r => r.Length));
        while (headers.Count < width) headers.Add("");
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Length < width) rows[i] = [.. rows[i], .. Enumerable.Repeat("", width - rows[i].Length)];
        return (titleLine, title, headers, rows);
    }

    // ------------------------------------------------------------------------------------------------------ helpers

    private static bool NumberParserLike(string t) => t.Length > 0 && t.Any(char.IsDigit) && t.All(ch => char.IsDigit(ch) || ch is '.' or ',' or '%' or '-' or '\'' or '\u2019') && (t.Length > 3 || t.Any(ch => ch is '.' or ',' or '%' or '\'' or '\u2019'));

    private static bool IsRulerLike(string[] lines, int k) => IsRuler(lines[k]);

    /// <summary>A heading underline: groups of dashes separated by single spaces; the section-closing line is one long group, indented.</summary>
    private static bool IsRuler(string line)
    {
        var t = line.Trim();
        if (t.Length < 3 || !t.All(ch => ch is '-' or ' ')) return false;
        if (!t.Contains(' ') && (line.StartsWith("  ") || t.Length >= 50)) return false; // closing line
        return t.Contains('-');
    }

    private static List<Col> Columns(string ruler)
    {
        var cols = new List<Col>();
        for (var i = 0; i < ruler.Length;)
        {
            if (ruler[i] != '-') { i++; continue; }
            var s = i;
            while (i < ruler.Length && ruler[i] == '-') i++;
            cols.Add(new Col(s, i - 1));
        }
        return cols;
    }

    private static List<int> HeadingLines(string[] lines, int ruler, int max, int floor = 0)
    {
        var list = new List<int>();
        for (var k = ruler - 1; k >= floor && list.Count < max; k--)
        {
            var l = lines[k];
            if (l.Length == 0 || IsRuler(l) || l.TrimStart().StartsWith("->", StringComparison.Ordinal) || Footer().IsMatch(l)) break;
            if (TitleLine(lines, k, out _) >= 0) break;
            list.Insert(0, k);
        }
        return list;
    }

    private static List<Tok> Tokens(string line)
    {
        var res = new List<Tok>();
        for (var i = 0; i < line.Length;)
        {
            if (line[i] == ' ') { i++; continue; }
            var s = i;
            while (i < line.Length && line[i] != ' ') i++;
            res.Add(new Tok(s, line[s..i]));
        }
        return res;
    }

    private static int ColumnFor(int pos, List<Col> cols)
    {
        for (var c = cols.Count - 1; c > 0; c--)
            if (pos >= cols[c - 1].End + 1) return c;
        return 0;
    }

    private static List<string> BuildHeaders(List<List<Tok>> headingLines, List<Col> cols)
    {
        var parts = new List<string>[cols.Count];
        for (var c = 0; c < parts.Length; c++) parts[c] = [];
        foreach (var line in headingLines)
            foreach (var t in line) parts[ColumnFor(t.Start, cols)].Add(t.Text);
        return parts.Select(p => string.Join(' ', p).Trim()).ToList();
    }

    private static string[] Slice(string line, List<Col> cols)
    {
        var cells = new string[cols.Count];
        for (var c = 0; c < cols.Count; c++)
        {
            var start = c == 0 ? 0 : cols[c - 1].End + 1;
            var end = c == cols.Count - 1 ? line.Length - 1 : cols[c].End;
            cells[c] = start >= line.Length || end < start ? "" : line.Substring(start, Math.Min(end, line.Length - 1) - start + 1).Trim();
        }
        return cells;
    }

    private static string RenderHtml(string[] lines, Dictionary<int, int> starts)
    {
        var sb = new StringBuilder(lines.Sum(l => l.Length + 1) + 1024);
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\">")
          .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline';\">")
          .Append("<style>body{margin:12px;background:#fff;color:#111}pre{font:12px/1.35 Consolas,'Cascadia Mono',monospace;margin:0}")
          .Append("pre.awr-highlight,table.awr-highlight{outline:3px solid #f59e0b;outline-offset:3px}</style></head><body><pre>");
        for (var i = 0; i < lines.Length; i++)
        {
            if (starts.TryGetValue(i, out var idx)) sb.Append("</pre><pre id=\"awr-t-").Append(idx).Append("\">");
            sb.Append(WebUtility.HtmlEncode(lines[i])).Append('\n');
        }
        sb.Append("</pre></body></html>");
        return sb.ToString();
    }
}
