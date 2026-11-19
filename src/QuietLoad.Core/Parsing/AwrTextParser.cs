// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using QuietLoad.Core.Model;

namespace QuietLoad.Core.Parsing;

/// <summary>
/// Parser for AWR plain-text reports (awrrpt.sql with report_type = 'text').
/// <para>
/// Fixed-width tables are located through their "----- ------" ruler lines, which define the column boundaries
/// (only dash groups are columns; the area left of the first group is an implicit label column). Column headings
/// are the 1-3 lines above the ruler (or the "~~~~" line itself) and each heading word belongs to the column whose
/// territory contains its first character. Key/value grids such as "Instance Efficiency Percentages" are read via
/// "Label: value" pairs. Titles lose their "DB/Inst: ... Snaps: a-b" suffix, "-&gt;" note lines are skipped, the extra
/// "Module:" / SQL text lines of the "SQL ordered by" sections are attached to their row, and the two stacked rulers
/// of Tablespace / File IO Stats are merged into one row per object. The parser never throws; unknown shapes become
/// best-effort tables. A minimal HTML rendering is produced so the report can still be shown side by side.
/// </para>
/// </summary>
public static partial class AwrTextParser
{
    [GeneratedRegex(@"^[\s\-~]+$")]
    private static partial Regex RulerChars();

    [GeneratedRegex(@"([A-Za-z%][A-Za-z0-9 %/()&\-\.>]{1,45}?)\s*:\s+([-+]?[\d.,'’]*\d[KMGT]?)")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"^[0-9a-z]{13}$")]
    private static partial Regex SqlIdRegex();

    [GeneratedRegex(@"\s+(?:DB/Inst:|DB Id/Inst:|Snaps?:).*$", RegexOptions.IgnoreCase)]
    private static partial Regex TitleSuffix();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    internal readonly record struct Column(int Start, int End);
    internal readonly record struct Token(int Start, string Text);

    private sealed class Draft
    {
        public int FirstLine;
        public string Title = "";
        public List<string> Headers = [];
        public List<string[]> Rows = [];
    }

    /// <summary>Text spellings that differ from the HTML report (normalised text, see AwrTable.Normalize) -> HTML spelling.</summary>
    private static readonly Dictionary<string, string> HeaderAliases = new(StringComparer.Ordinal)
    {
        ["wait ms"] = "Avg wait (ms)",
        ["av reads/s"] = "Av Rds/s",
        ["av writes/s"] = "Writes avg/s",
        ["curs/sess"] = "Cursors/Session",
        ["total time sec"] = "Total Wait Time (sec)",
    };

    public static AwrReport Parse(string text, string sourcePath = "")
    {
        using var scope = NumberConventions.Begin(text);
        var report = ParseCore(text, sourcePath);
        scope.Apply(report, text);
        return report;
    }

    private static AwrReport ParseCore(string text, string sourcePath = "")
    {
        var report = new AwrReport { SourcePath = sourcePath, Format = AwrReportFormat.Text };
        string[] lines = [];
        var tableStarts = new Dictionary<int, int>();
        try
        {
            lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Select(l => l.Replace('\t', ' ').TrimEnd()).ToArray();
            BuildTables(lines, report, tableStarts);
        }
        catch (Exception ex)
        {
            report.ParseWarnings.Add("Text report could not be fully parsed: " + ex.Message);
        }

        try { AwrInfoExtractor.Populate(report); }
        catch (Exception ex) { report.ParseWarnings.Add("Header information could not be read: " + ex.Message); }
        if (report.Tables.Count == 0) report.ParseWarnings.Add("No tables were recognised in the text report.");
        report.SanitizedHtml = RenderHtml(lines, tableStarts);
        return report;
    }

    // ------------------------------------------------------------------------------------------------ table discovery

    private sealed class State(string[] lines)
    {
        public readonly string[] Lines = lines;
        public readonly bool[] Consumed = new bool[lines.Length];
        public readonly bool[] Boundary = new bool[lines.Length];
        public readonly bool[] RulerOk = new bool[lines.Length];
        public readonly bool[] Used = new bool[lines.Length];
        public readonly Dictionary<int, int> StackedB = [];
        public int N => Lines.Length;
    }

    private static void BuildTables(string[] lines, AwrReport report, Dictionary<int, int> tableStarts)
    {
        var st = new State(lines);
        MarkBoundaries(st);

        for (var i = 0; i < st.N; i++) st.RulerOk[i] = IsValidRuler(lines, i);

        // Stacked rulers (Tablespace / File IO Stats): object-name ruler, wrapped stat headings, stat ruler.
        for (var r = 0; r < st.N; r++)
        {
            if (!st.RulerOk[r] || st.Used[r]) continue;
            var b = FindStackedB(st, r);
            if (b > 0) { st.StackedB[r] = b; st.Used[b] = true; }
        }

        // Header lines of every ruler end the previous table.
        for (var r = 0; r < st.N; r++)
        {
            if (!st.RulerOk[r]) continue;
            st.Boundary[r] = true;
            foreach (var h in FindHeadingLines(st, r)) st.Boundary[h] = true;
        }

        var drafts = new List<Draft>();
        ReadKeyValueBlocks(st, drafts);
        for (var r = 0; r < st.N; r++)
        {
            if (!st.RulerOk[r] || st.Used[r] && !st.StackedB.ContainsKey(r)) continue;
            Draft? d;
            try { d = ReadRulerTable(st, r); }
            catch (Exception ex) { report.ParseWarnings.Add($"Table at line {r + 1} skipped: {ex.Message}"); d = null; }
            if (d != null) drafts.Add(d);
        }

        drafts.Sort((a, b) => a.FirstLine.CompareTo(b.FirstLine));
        var titleCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in drafts)
        {
            var t = new AwrTable
            {
                Id = $"awr-t-{report.Tables.Count}",
                Index = report.Tables.Count,
                Title = d.Title.Length > 0 ? d.Title : $"Table {report.Tables.Count + 1}",
                Headers = d.Headers,
                Rows = d.Rows
            };
            Uniquify(t, titleCounts);
            tableStarts.TryAdd(d.FirstLine, t.Index);
            report.Tables.Add(t);
        }

        // Header tables come first in the text layout; give them stable names like the HTML parser does.
        foreach (var t in report.Tables.Take(10))
        {
            if (t.HasColumn("DB Name") && (t.HasColumn("Release") || t.HasColumn("DB Id"))) t.Title = "Database Instance Information";
            else if (t.HasColumn("Host Name")) t.Title = "Host Information";
            else if (t.HasColumn("Snap Id") && t.HasColumn("Snap Time")) t.Title = "Snapshot Information";
        }
        MergeInstanceTable(report);
    }

    /// <summary>Lines that always start a new block: titles (above "~~~~" or "-&gt;" notes, or carrying "DB/Inst:"), tilde lines.</summary>
    private static void MarkBoundaries(State st)
    {
        var lines = st.Lines;
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Length == 0) continue;
            if (IsTildeLine(l) || IsTildeLead(l))
            {
                st.Boundary[i] = true;
                if (i > 0 && lines[i - 1].Length > 0 && !IsTildeLine(lines[i - 1])) st.Boundary[i - 1] = true;
            }
            else if (l.Contains("DB/Inst:", StringComparison.Ordinal) || l.StartsWith("End of Report", StringComparison.OrdinalIgnoreCase))
                st.Boundary[i] = true;
            else if (l.StartsWith("->", StringComparison.Ordinal) && i > 0)
            {
                var p = lines[i - 1];
                if (p.Length > 0 && !p.StartsWith("->", StringComparison.Ordinal) && !char.IsWhiteSpace(p[0])) st.Boundary[i - 1] = true;
            }
        }
    }

    private static bool IsValidRuler(string[] lines, int i)
    {
        if (!IsDashRuler(lines[i])) return false;
        // A real ruler sits directly under its headings and directly above its rows; the dash line that closes a
        // section has a blank line below it.
        return i > 0 && lines[i - 1].Length > 0 && i + 1 < lines.Length && lines[i + 1].Length > 0;
    }

    private static int FindStackedB(State st, int a)
    {
        var lines = st.Lines;
        for (var j = a + 1; j < st.N && j <= a + 4; j++)
        {
            var l = lines[j];
            if (l.Length == 0 || l.TrimStart().StartsWith("->", StringComparison.Ordinal)) return -1;
            if (IsDashRuler(l)) return j > a + 1 && st.RulerOk[j] ? j : -1;
            if (Tokens(l).Any(t => NumberParser.Parse(t.Text) is not null)) return -1;
        }
        return -1;
    }

    /// <summary>Heading lines above a ruler (max 3): stops at blanks, notes, tilde underlines and other rulers.</summary>
    private static List<int> FindHeadingLines(State st, int r)
    {
        var list = new List<int>();
        for (var k = r - 1; k >= 0 && list.Count < 3; k--)
        {
            var l = st.Lines[k];
            if (l.Length == 0 || l.TrimStart().StartsWith("->", StringComparison.Ordinal) || IsTildeLine(l) || IsDashRuler(l)) break;
            list.Insert(0, k);
            if (IsTildeLead(l)) break;
        }
        return list;
    }

    // ------------------------------------------------------------------------------------------ key/value grids

    /// <summary>"Instance Efficiency Percentages" style blocks: a title, a "~~~~" underline and "Label: value" pairs.</summary>
    private static void ReadKeyValueBlocks(State st, List<Draft> drafts)
    {
        var lines = st.Lines;
        for (var i = 1; i < st.N; i++)
        {
            if (!IsTildeLine(lines[i])) continue;
            var title = lines[i - 1].Trim();
            if (title.Length == 0 || IsTildeLine(title)) continue;
            var k = i + 1;
            while (k < st.N && lines[k].Length == 0 && k <= i + 2) k++;
            if (k >= st.N || !KeyValue().IsMatch(lines[k]) || st.RulerOk[k] || IsDashRuler(lines[k])) continue;

            var rows = new List<string[]>();
            for (; k < st.N && rows.Count < 200; k++)
            {
                var l = lines[k];
                if (l.Length == 0 || IsDashRuler(l) || IsTildeLine(l)) break;
                var matches = KeyValue().Matches(l);
                if (matches.Count == 0) break;
                var row = new List<string>();
                foreach (Match m in matches) { row.Add(m.Groups[1].Value.Trim() + ":"); row.Add(m.Groups[2].Value); }
                rows.Add([.. row]);
                st.Consumed[k] = true;
            }
            if (rows.Count == 0) continue;
            st.Consumed[i - 1] = true;
            drafts.Add(new Draft { FirstLine = i - 1, Title = CleanTitle(title), Rows = rows });
        }
    }

    // ------------------------------------------------------------------------------------------------ ruler tables

    private static Draft? ReadRulerTable(State st, int r)
    {
        var lines = st.Lines;
        var cols = Columns(lines[r]);
        if (cols.Count == 0) return null;
        var stackedB = st.StackedB.TryGetValue(r, out var b) ? b : -1;
        var tildeRuler = lines[r].TrimStart().StartsWith('~');
        var heads = FindHeadingLines(st, r);
        var firstLine = heads.Count > 0 ? heads[0] : r;
        var above = firstLine - 1;

        // Implicit label column: the area left of the first ruler group ("Redo size (bytes):   6,004,534.4 ...").
        var hasLabel = false;
        if (stackedB < 0 && cols[0].Start >= 3)
        {
            var labelled = 0;
            for (var i = r + 1; i < st.N && i <= r + 6 && lines[i].Length > 0; i++)
                if (lines[i].Length >= cols[0].Start && lines[i][..cols[0].Start].Trim().Length > 0) labelled++;
            var headLabel = heads.Count > 0 && Tokens(StripTilde(lines[heads[0]])).Any(t => t.Start < cols[0].Start);
            hasLabel = tildeRuler || labelled > 0 || headLabel;
            if (hasLabel) cols.Insert(0, new Column(0, cols[0].Start - 1));
        }

        var headTokens = heads.Select(h => Tokens(StripTilde(lines[h]))).ToList();
        var title = "";
        var titleLine = -1;

        // "Load Profile      Per Second ..." / "Shared Pool Statistics   Begin  End": title shares the heading line.
        if (hasLabel && headTokens.Count > 0 && !IsTildeLead(lines[heads[0]]) && (tildeRuler || above < 0 || lines[above].Length == 0))
        {
            var labelToks = headTokens[0].Where(t => ColumnFor(t.Start, cols) == 0).ToList();
            if (labelToks.Count > 0)
            {
                title = CleanTitle(string.Join(' ', labelToks.Select(t => t.Text)));
                titleLine = heads[0];
                headTokens[0] = headTokens[0].Where(t => ColumnFor(t.Start, cols) != 0).ToList();
            }
        }
        if (title.Length == 0) (title, titleLine) = FindTitle(st, above);
        if (titleLine >= 0 && titleLine < firstLine) firstLine = titleLine;

        var headers = BuildHeaders(headTokens, cols);
        var cellsOf = (Func<string, List<string>>)(l => SliceRow(l, cols));
        List<Column>? colsB = null;

        if (stackedB > 0)
        {
            colsB = Columns(lines[stackedB]);
            var between = new List<List<Token>>();
            for (var i = r + 1; i < stackedB; i++) between.Add(Tokens(lines[i]));
            headers.AddRange(BuildHeaders(between, colsB));
        }

        var rows = stackedB > 0 ? ReadStackedRows(st, stackedB + 1, cols, colsB!) : ReadRows(st, r + 1, cols, headers, out headers);
        _ = cellsOf;

        if (rows.Count == 0 && headers.All(h => h.Length == 0)) return null;
        for (var i = r; i < st.N && i <= r; i++) st.Consumed[i] = true;
        var width = Math.Max(headers.Count, rows.Count == 0 ? 0 : rows.Max(x => x.Length));
        while (headers.Count < width) headers.Add("");
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Length < width) rows[i] = [.. rows[i], .. Enumerable.Repeat("", width - rows[i].Length)];
        if (hasLabel && headers.Count > 0 && title.Length > 0 && headers[0] == title) headers[0] = "";

        return new Draft { FirstLine = firstLine, Title = title, Headers = headers, Rows = rows };
    }

    private static List<string[]> ReadRows(State st, int start, List<Column> cols, List<string> headersIn, out List<string> headers)
    {
        var lines = st.Lines;
        headers = headersIn;
        var rows = new List<string[]>();
        var extraMod = new List<string>();
        var extraText = new List<string>();
        var sqlCol = headers.FindIndex(h => AwrTable.Normalize(h) == "sql id");
        var textCol = headers.FindIndex(h => AwrTable.Normalize(h) == "sql text");
        var modCol = headers.FindIndex(h => AwrTable.Normalize(h) is "sql module" or "module");
        var sqlMode = sqlCol >= 0;
        var blankRun = 0;
        var misfits = 0;

        for (var i = start; i < st.N; i++)
        {
            var line = lines[i];
            if (line.Length == 0) { if (++blankRun >= 2) break; continue; }
            if (st.Boundary[i] || IsDashRuler(line) || IsTildeLine(line)) break;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("->", StringComparison.Ordinal)) continue;

            List<string>? cells = null;
            if (FitsGrid(line, cols))
            {
                cells = SliceRow(line, cols);
                if (sqlMode && !SqlIdRegex().IsMatch(sqlCol < cells.Count ? cells[sqlCol] : "")) cells = null;
            }
            if (cells != null)
            {
                if (cells.All(x => x.Length == 0)) continue;
                rows.Add([.. cells]);
                extraMod.Add("");
                extraText.Add("");
                st.Consumed[i] = true;
                blankRun = 0; misfits = 0;
                continue;
            }
            if (rows.Count == 0) { if (++misfits > 15) break; continue; }
            if (blankRun > 0) break; // free text after a blank line: the table is over
            if (sqlMode)
            {
                // "Module: ..." and (truncated) SQL text lines that follow a "SQL ordered by" row.
                var idx = rows.Count - 1;
                st.Consumed[i] = true;
                if (trimmed.StartsWith("Module:", StringComparison.OrdinalIgnoreCase))
                    Attach(rows[idx], modCol, extraMod, idx, trimmed[7..].Trim());
                else
                    Attach(rows[idx], textCol, extraText, idx, trimmed);
                continue;
            }
            if (++misfits > 15) break;
        }

        // Extra columns for captured module / SQL text when the heading does not already have them.
        var addMod = modCol < 0 && extraMod.Any(x => x.Length > 0);
        var addText = textCol < 0 && extraText.Any(x => x.Length > 0);
        if (addMod || addText)
        {
            var baseWidth = Math.Max(headers.Count, rows.Count == 0 ? 0 : rows.Max(x => x.Length));
            headers = [.. headers, .. Enumerable.Repeat("", baseWidth - headers.Count)];
            if (addMod) headers.Add("SQL Module");
            if (addText) headers.Add("SQL Text");
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Length < baseWidth) row = [.. row, .. Enumerable.Repeat("", baseWidth - row.Length)];
                var list = row.ToList();
                if (addMod) list.Add(extraMod[i]);
                if (addText) list.Add(extraText[i]);
                rows[i] = [.. list];
            }
        }
        return rows;
    }

    private static void Attach(string[] row, int col, List<string> extras, int idx, string value)
    {
        if (value.Length == 0) return;
        if (col >= 0 && col < row.Length) row[col] = row[col].Length == 0 ? value : row[col] + " " + value;
        else extras[idx] = extras[idx].Length == 0 ? value : extras[idx] + " " + value;
    }

    /// <summary>Tablespace / File IO Stats: an object-name line (which may wrap) followed by its stats line.</summary>
    private static List<string[]> ReadStackedRows(State st, int start, List<Column> colsA, List<Column> colsB)
    {
        var lines = st.Lines;
        var rows = new List<string[]>();
        List<string>? pending = null;
        var blankRun = 0;
        var misfits = 0;
        for (var i = start; i < st.N; i++)
        {
            var line = lines[i];
            if (line.Length == 0) { if (++blankRun >= 2) break; continue; }
            if (st.Boundary[i] || IsDashRuler(line) || IsTildeLine(line)) break;
            if (line.TrimStart().StartsWith("->", StringComparison.Ordinal)) continue;

            var tokens = Tokens(line);
            var isStat = tokens.Count >= 2 && NumberParser.Parse(tokens[0].Text) is not null;
            if (isStat)
            {
                List<string> stat;
                if (FitsGrid(line, colsB)) stat = SliceRow(line, colsB);
                else if (tokens.Count == colsB.Count) stat = tokens.Select(t => t.Text).ToList(); // slightly misaligned: assign in order
                else { if (++misfits > 15) break; continue; }
                var a = pending ?? [.. Enumerable.Repeat("", colsA.Count)];
                rows.Add([.. a, .. stat]);
                st.Consumed[i] = true;
                pending = null; blankRun = 0; misfits = 0;
                continue;
            }
            if (rows.Count > 0 && blankRun > 0 && pending == null) break;
            if (pending == null)
            {
                if (!FitsGrid(line, colsA)) { if (++misfits > 15) break; continue; }
                pending = SliceRow(line, colsA);
            }
            else
            {
                // Wrapped object name: glue the fragment to the last filled cell.
                var idx = pending.FindLastIndex(x => x.Length > 0);
                if (idx < 0) idx = 0;
                var width = idx < colsA.Count ? colsA[idx].End - colsA[idx].Start + 1 : 0;
                var sep = pending[idx].Length >= width ? "" : " ";
                pending[idx] = pending[idx] + sep + line.Trim();
            }
            st.Consumed[i] = true;
            blankRun = 0;
        }
        if (pending != null && pending.Any(x => x.Length > 0)) rows.Add([.. pending]);
        return rows;
    }

    /// <summary>Finds the table title above the headings: line above a "~~~" underline, or the first non-note line of the note block.</summary>
    private static (string Title, int Line) FindTitle(State st, int k)
    {
        var lines = st.Lines;
        var j = k;
        var blanks = 0;
        while (j >= 0 && lines[j].Length == 0 && blanks < 2) { j--; blanks++; }
        if (j < 0 || lines[j].Length == 0 || st.Consumed[j]) return ("", -1);

        if (IsTildeLine(lines[j]))
            return j > 0 && lines[j - 1].Length > 0 && !st.Consumed[j - 1] ? (CleanTitle(lines[j - 1]), j - 1) : ("", -1);

        var top = j;
        while (top - 1 >= 0 && lines[top - 1].Length > 0 && !st.Consumed[top - 1] && !IsDashRuler(lines[top - 1]) && !IsTildeLine(lines[top - 1])) top--;
        for (var i = j; i >= top; i--)
        {
            var t = lines[i];
            if (t.TrimStart().StartsWith("->", StringComparison.Ordinal)) continue;
            if (char.IsWhiteSpace(t[0]) && NoteAbove(lines, top, i)) continue;
            return (CleanTitle(t), i);
        }
        return ("", -1);
    }

    private static bool NoteAbove(string[] lines, int top, int i)
    {
        for (var p = top; p < i; p++) if (lines[p].TrimStart().StartsWith("->", StringComparison.Ordinal)) return true;
        return false;
    }

    private static string CleanTitle(string raw)
    {
        var t = TitleSuffix().Replace(raw.Trim(), "");
        t = Spaces().Replace(t.Trim(), " ");
        return t.TrimEnd(':').Trim();
    }

    // ----------------------------------------------------------------------------------------------------- headings

    internal static List<string> BuildHeaders(List<List<Token>> headingLines, List<Column> cols)
    {
        var parts = new List<string>[cols.Count];
        for (var c = 0; c < parts.Length; c++) parts[c] = [];
        foreach (var line in headingLines)
            foreach (var t in line) parts[ColumnFor(t.Start, cols)].Add(t.Text);
        return parts.Select(p => NormalizeHeader(string.Join(' ', p))).ToList();
    }

    private static string NormalizeHeader(string h)
    {
        h = h.Trim();
        return h.Length > 0 && HeaderAliases.TryGetValue(AwrTable.Normalize(h), out var alias) ? alias : h;
    }

    /// <summary>A heading word belongs to the column whose territory (previous ruler group end + 1 .. own end) holds its first character.</summary>
    private static int ColumnFor(int pos, List<Column> cols)
    {
        for (var c = cols.Count - 1; c > 0; c--)
            if (pos >= cols[c - 1].End + 1) return c;
        return 0;
    }

    internal static List<Token> Tokens(string line)
    {
        var res = new List<Token>();
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] == ' ') { i++; continue; }
            var s = i;
            while (i < line.Length && line[i] != ' ') i++;
            res.Add(new Token(s, line[s..i]));
        }
        return res;
    }

    private static string StripTilde(string line)
    {
        if (!IsTildeLead(line)) return line;
        var chars = line.ToCharArray();
        for (var i = 0; i < chars.Length && (chars[i] == '~' || chars[i] == ' '); i++) chars[i] = ' ';
        return new string(chars);
    }

    // --------------------------------------------------------------------------------------------------- merging etc.

    /// <summary>19c text reports split "Instance / Inst Num / Startup Time" into a second table.</summary>
    private static void MergeInstanceTable(AwrReport report)
    {
        var db = report.Tables.FirstOrDefault(t => t.Title == "Database Instance Information");
        var inst = report.Tables.FirstOrDefault(t => t.HasColumn("Inst Num") && t.HasColumn("Startup Time") && !t.HasColumn("DB Name"));
        if (db == null || inst == null || db.Rows.Count == 0 || inst.Rows.Count == 0 || db.HasColumn("Startup Time")) return;
        db.Headers.AddRange(inst.Headers);
        db.Rows[0] = [.. db.Rows[0], .. inst.Rows[0]];
    }

    private static void Uniquify(AwrTable t, Dictionary<string, int> counts)
    {
        var key = t.Title;
        counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        if (counts[key] > 1) t.Title = $"{key} ({counts[key]})";
    }

    // ------------------------------------------------------------------------------------------------ line classifiers

    internal static bool IsDashRuler(string line)
    {
        var t = line.Trim();
        if (t.Length < 3 || !RulerChars().IsMatch(t) || !t.Contains('-')) return false;
        var dashes = t.Count(ch => ch == '-');
        return dashes >= 3 && dashes >= t.Count(ch => ch == '~') - 40;
    }

    internal static bool IsTildeLine(string line)
    {
        var t = line.Trim();
        return t.Length >= 3 && t.All(ch => ch == '~');
    }

    /// <summary>"~~~~~~~~     Per Second   Per Transaction": a tilde underline that also carries column headings.</summary>
    internal static bool IsTildeLead(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("~~~", StringComparison.Ordinal) && !IsTildeLine(line) && !IsDashRuler(line);
    }

    /// <summary>Only dash groups are columns; tildes in front of them merely underline the title.</summary>
    internal static List<Column> Columns(string ruler)
    {
        var cols = new List<Column>();
        var i = 0;
        while (i < ruler.Length)
        {
            if (ruler[i] == '-')
            {
                var s = i;
                while (i < ruler.Length && ruler[i] == '-') i++;
                cols.Add(new Column(s, i - 1));
            }
            else i++;
        }
        return cols;
    }

    /// <summary>A data line fits when the gap characters between ruler groups are blank (or the line is shorter).</summary>
    internal static bool FitsGrid(string line, List<Column> cols)
    {
        var violations = 0;
        for (var c = 0; c + 1 < cols.Count; c++)
        {
            for (var p = cols[c].End + 1; p < cols[c + 1].Start; p++)
                if (p < line.Length && line[p] != ' ') { violations++; break; }
        }
        // Left-aligned first column text may overflow into the gap; tolerate one violation there.
        return violations == 0 || (violations == 1 && cols.Count > 2 && line.Length > 0 && !char.IsWhiteSpace(line[0]) && FirstGapViolated(line, cols));
    }

    private static bool FirstGapViolated(string line, List<Column> cols)
    {
        for (var p = cols[0].End + 1; p < cols[1].Start; p++)
            if (p < line.Length && line[p] != ' ') return true;
        return false;
    }

    /// <summary>
    /// Cuts a data line along the column boundaries. Text after the last ruler group is part of the last cell, except
    /// "Label: value" pairs separated by two or more spaces (Cache Sizes: "Std Block Size:  8K"), which become extra cells.
    /// </summary>
    internal static List<string> SliceRow(string line, List<Column> cols)
    {
        var last = cols[^1].End;
        List<string>? pairs = null;
        if (line.Length > last + 2 && line[last + 1] == ' ' && line[last + 2] == ' ')
        {
            var rest = line[(last + 1)..];
            var ms = KeyValue().Matches(rest);
            if (ms.Count > 0 && rest[..ms[0].Index].Trim().Length == 0)
            {
                pairs = [];
                foreach (Match m in ms) { pairs.Add(m.Groups[1].Value.Trim() + ":"); pairs.Add(m.Groups[2].Value); }
                line = line[..(last + 1)];
            }
        }
        var cells = new List<string>(cols.Count + (pairs?.Count ?? 0));
        for (var c = 0; c < cols.Count; c++) cells.Add(SliceColumn(line, cols, c).Trim());
        if (pairs != null) cells.AddRange(pairs);
        return cells;
    }

    private static string SliceColumn(string line, List<Column> cols, int c)
    {
        var start = c == 0 ? 0 : cols[c - 1].End + 1;
        var end = c == cols.Count - 1 ? line.Length - 1 : cols[c].End;
        return Slice(line, start, end);
    }

    private static string Slice(string s, int start, int endInclusive)
    {
        if (start >= s.Length) return "";
        endInclusive = Math.Min(endInclusive, s.Length - 1);
        return endInclusive < start ? "" : s.Substring(start, endInclusive - start + 1);
    }

    // ----------------------------------------------------------------------------------------------------- HTML view

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
