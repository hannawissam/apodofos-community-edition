// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text;
using Apodofos.Core.Model;

namespace Apodofos.Tests;

/// <summary>
/// Renders parsed report tables in the layout of Oracle's awrrpt.sql text output (title, "~~~" underline for the
/// summary sections, "-&gt;" notes, wrapped column headings, "----" rulers, left-aligned text and right-aligned
/// numbers). Used to exercise the text parser with large, realistic reports derived from the real HTML samples.
/// </summary>
internal static class TextReportRenderer
{
    private const int MaxText = 60;

    public static string Render(AwrReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WORKLOAD REPOSITORY report for");
        sb.AppendLine();
        var n = 0;
        foreach (var t in report.Tables)
        {
            n++;
            var summary = n <= 14 && t.Section is null;
            var title = t.Title;
            if (summary && title.StartsWith("Database Instance")) title = "";
            else if (summary && (title.StartsWith("Host Information") || title.StartsWith("Snapshot Information"))) title = "";

            if (title.Length > 0)
            {
                sb.AppendLine(title.Replace("Percentages (Target 100%)", "Percentages (Target 100%)"));
                if (summary) sb.AppendLine(new string('~', Math.Max(title.Length, 6)));
                else sb.AppendLine("-> Only rows with significant activity are shown");
                if (!summary) sb.AppendLine("-> ordered by the first column, descending");
            }
            RenderTable(sb, t, summary);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void RenderTable(StringBuilder sb, AwrTable t, bool keyValueStyle)
    {
        var colCount = t.ColumnCount;
        if (colCount == 0) return;
        var rows = t.Rows.Select(r => Pad(r, colCount)).ToList();
        var headers = Pad(t.Headers.ToArray(), colCount);

        // Instance efficiency / cache sizes style grids have no header: Oracle prints "Label:  value" pairs.
        if (t.Headers.Count == 0 || t.Headers.All(string.IsNullOrWhiteSpace))
        {
            foreach (var r in rows)
            {
                var sbRow = new StringBuilder();
                for (var c = 0; c + 1 < colCount; c += 2)
                    sbRow.Append(' ', 2).Append(Clip(r[c]).PadLeft(28)).Append(' ').Append(Clip(r[c + 1]).PadLeft(12)).Append("  ");
                if (colCount % 2 == 1) sbRow.Append(Clip(r[^1]));
                sb.AppendLine(sbRow.ToString().TrimEnd());
            }
            return;
        }

        var numeric = new bool[colCount];
        for (var c = 0; c < colCount; c++)
        {
            var vals = rows.Select(r => r[c]).Where(v => v.Length > 0).ToList();
            numeric[c] = c > 0 && vals.Count > 0 && vals.Count(v => Apodofos.Core.Parsing.NumberParser.Parse(v) is not null) * 10 >= vals.Count * 8;
        }

        // Break long headings into up to 3 lines, like awrrpt does ("Total Wait Time (s)" -> "Total Wait" / "Time (s)").
        var headLines = new List<string[]>();
        var split = headers.Select(h => SplitHeader(h)).ToArray();
        var lines = Math.Max(1, split.Max(s => s.Length));
        for (var l = 0; l < lines; l++)
            headLines.Add(split.Select(s => l - (lines - s.Length) >= 0 ? s[l - (lines - s.Length)] : "").ToArray());

        var widths = new int[colCount];
        for (var c = 0; c < colCount; c++)
        {
            widths[c] = Math.Max(1, headLines.Max(h => h[c].Length));
            foreach (var r in rows) widths[c] = Math.Max(widths[c], Clip(r[c]).Length);
        }

        foreach (var h in headLines)
            sb.AppendLine(Line(h, widths, numeric).TrimEnd());
        sb.AppendLine(string.Join(' ', widths.Select(w => new string('-', w))));
        foreach (var r in rows)
            sb.AppendLine(Line(r.Select(Clip).ToArray(), widths, numeric).TrimEnd());
    }

    private static string Line(string[] cells, int[] widths, bool[] numeric)
    {
        var sb = new StringBuilder();
        for (var c = 0; c < cells.Length; c++)
        {
            if (c > 0) sb.Append(' ');
            sb.Append(numeric[c] ? cells[c].PadLeft(widths[c]) : cells[c].PadRight(widths[c]));
        }
        return sb.ToString();
    }

    private static string[] SplitHeader(string h)
    {
        h = h.Trim();
        if (h.Length <= 12) return [h];
        var words = h.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var cur = "";
        foreach (var w in words)
        {
            if (cur.Length > 0 && cur.Length + 1 + w.Length > 12) { lines.Add(cur); cur = w; }
            else cur = cur.Length == 0 ? w : cur + " " + w;
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines.Count > 3 ? [lines[0], lines[1], string.Join(' ', lines.Skip(2))] : [.. lines];
    }

    private static string[] Pad(string[] r, int n)
    {
        if (r.Length >= n) return r;
        var x = new string[n];
        Array.Fill(x, "");
        Array.Copy(r, x, r.Length);
        return x;
    }

    private static string Clip(string s)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
        return s.Length > MaxText ? s[..MaxText] : s;
    }
}
