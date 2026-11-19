// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Net;
using System.Text.RegularExpressions;
using Apodofos.Core.Model;
using HtmlAgilityPack;

namespace Apodofos.Core.Parsing;

/// <summary>
/// Parses AWR reports produced by awrrpt.sql / awrrpti.sql / DBMS_WORKLOAD_REPOSITORY.AWR_REPORT_HTML.
/// Each &lt;table&gt; becomes an <see cref="AwrTable"/> titled with the nearest preceding heading or caption
/// text. In the same pass the document is sanitised for safe offline display.
/// </summary>
public static partial class AwrHtmlParser
{
    private static readonly HashSet<string> SkipTextParents =
        new(StringComparer.OrdinalIgnoreCase) { "script", "style", "title", "head", "table", "li", "option", "select", "pre", "textarea" };

    private static readonly HashSet<string> DangerousElements =
        new(StringComparer.OrdinalIgnoreCase) { "script", "noscript", "iframe", "frame", "frameset", "object", "embed", "applet", "link", "base", "form", "input", "button", "svg", "math", "audio", "video", "source", "track" };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static AwrReport Parse(string html, string sourcePath = "")
    {
        using var scope = NumberConventions.Begin(html);
        var report = ParseCore(html, sourcePath);
        scope.Apply(report, html);
        return report;
    }

    private static AwrReport ParseCore(string html, string sourcePath = "")
    {
        var doc = new HtmlDocument { OptionFixNestedTags = true, OptionAutoCloseOnEnd = true };
        doc.LoadHtml(html);

        var report = new AwrReport { SourcePath = sourcePath, Format = AwrReportFormat.Html };
        string lastTitle = "";
        string? section = null;
        var titleCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Materialise first: we mutate attributes while walking.
        var nodes = doc.DocumentNode.Descendants().ToList();
        foreach (var node in nodes)
        {
            if (node.NodeType == HtmlNodeType.Text)
            {
                if (HasAncestor(node, SkipTextParents) || IsInsideLink(node)) continue;
                var text = Clean(node.InnerText);
                if (text.Length < 3 || text.Length > 160) continue;
                if (text.StartsWith("Back to", StringComparison.OrdinalIgnoreCase)) continue;
                if (text.StartsWith("->", StringComparison.Ordinal) || text.StartsWith("-", StringComparison.Ordinal)) continue;
                var heading = HeadingLevel(node);
                if (heading == 2) section = text;
                lastTitle = text;
                continue;
            }

            if (node.NodeType != HtmlNodeType.Element || !node.Name.Equals("table", StringComparison.OrdinalIgnoreCase))
                continue;
            if (HasAncestor(node, ["table"])) continue; // nested layout tables are read with their parent

            var table = ReadTable(node, report.Tables.Count, lastTitle, section);
            if (table == null) continue;

            // Stable unique titles: "Instance Activity Stats (2)" when Oracle repeats a caption.
            var key = table.Title;
            titleCounts[key] = titleCounts.TryGetValue(key, out var n) ? n + 1 : 1;
            if (titleCounts[key] > 1) table.Title = $"{key} ({titleCounts[key]})";

            node.SetAttributeValue("id", table.Id);
            report.Tables.Add(table);
        }

        FixHeaderTitles(report);
        AwrInfoExtractor.Populate(report);
        report.SanitizedHtml = Sanitize(doc);
        return report;
    }

    private static AwrTable? ReadTable(HtmlNode tableNode, int index, string title, string? section)
    {
        var rows = tableNode.Descendants("tr").Where(tr => ReferenceEquals(ClosestTable(tr), tableNode)).ToList();
        if (rows.Count == 0) return null;

        var headerRows = new List<List<string>>();
        var dataRows = new List<string[]>();
        foreach (var tr in rows)
        {
            var cells = tr.ChildNodes.Where(c => c.Name is "td" or "th").ToList();
            if (cells.Count == 0) continue;
            // A header row has <th> cells; Oracle pads multi-level headers with empty <td> spacers.
            var allTh = cells.Any(c => c.Name == "th") && cells.Where(c => c.Name == "td").All(c => Clean(c.InnerText).Length == 0);
            var expanded = new List<string>();
            foreach (var c in cells)
            {
                var text = Clean(c.InnerText);
                var span = int.TryParse(c.GetAttributeValue("colspan", "1").Trim(), out var cs) ? Math.Clamp(cs, 1, 50) : 1;
                for (var i = 0; i < span; i++) expanded.Add(i == 0 || allTh ? text : "");
            }
            if (allTh && dataRows.Count == 0) headerRows.Add(expanded);
            else if (!allTh) dataRows.Add([.. expanded]);
        }

        var headers = MergeHeaderRows(headerRows);
        var summary = tableNode.GetAttributeValue("summary", null!);
        return new AwrTable
        {
            Id = $"awr-t-{index}",
            Index = index,
            Title = string.IsNullOrWhiteSpace(title) ? $"Table {index + 1}" : title,
            Section = section,
            Summary = summary == null ? null : Clean(WebUtility.HtmlDecode(summary)),
            Headers = headers,
            Rows = dataRows
        };
    }

    private static List<string> MergeHeaderRows(List<List<string>> headerRows)
    {
        if (headerRows.Count == 0) return [];
        if (headerRows.Count == 1) return headerRows[0];
        var width = headerRows.Max(r => r.Count);
        var merged = new List<string>(width);
        for (var i = 0; i < width; i++)
        {
            var parts = headerRows.Select(r => i < r.Count ? r[i] : "").Where(p => p.Length > 0).Distinct();
            merged.Add(string.Join(" ", parts));
        }
        return merged;
    }

    /// <summary>The very first tables sit under "WORKLOAD REPOSITORY report for"; give them meaningful names.</summary>
    private static void FixHeaderTitles(AwrReport report)
    {
        foreach (var t in report.Tables.Take(8))
        {
            if (t.HasColumn("DB Name") && t.HasColumn("Release")) t.Title = "Database Instance Information";
            else if (t.HasColumn("Host Name") && t.HasColumn("Platform")) t.Title = "Host Information";
            else if (t.HasColumn("Snap Id") && t.HasColumn("Snap Time")) t.Title = "Snapshot Information";
        }
    }

    private static HtmlNode? ClosestTable(HtmlNode n)
    {
        for (var p = n.ParentNode; p != null; p = p.ParentNode)
            if (p.Name.Equals("table", StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    private static bool HasAncestor(HtmlNode n, IEnumerable<string> names)
    {
        var set = names as HashSet<string> ?? new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        for (var p = n.ParentNode; p != null; p = p.ParentNode)
            if (set.Contains(p.Name)) return true;
        return false;
    }

    private static bool IsInsideLink(HtmlNode n)
    {
        for (var p = n.ParentNode; p != null; p = p.ParentNode)
            if (p.Name.Equals("a", StringComparison.OrdinalIgnoreCase) && p.Attributes.Contains("href")) return true;
        return false;
    }

    private static int HeadingLevel(HtmlNode n)
    {
        for (var p = n.ParentNode; p != null; p = p.ParentNode)
            if (p.Name.Length == 2 && p.Name[0] is 'h' or 'H' && char.IsDigit(p.Name[1])) return p.Name[1] - '0';
        return 0;
    }

    internal static string Clean(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var s = HtmlEntity.DeEntitize(raw).Replace(' ', ' ');
        return Whitespace().Replace(s, " ").Trim();
    }

    /// <summary>
    /// Produces a display-safe copy of the report: no scripts or active content, no network access
    /// (CSP default-src 'none'), external links neutralised, internal #anchors preserved.
    /// </summary>
    private static string Sanitize(HtmlDocument doc)
    {
        foreach (var n in doc.DocumentNode.Descendants().ToList())
        {
            if (n.NodeType == HtmlNodeType.Comment) { n.Remove(); continue; }
            if (n.NodeType != HtmlNodeType.Element) continue;
            if (DangerousElements.Contains(n.Name)) { n.Remove(); continue; }
            if (n.Name.Equals("meta", StringComparison.OrdinalIgnoreCase) &&
                n.GetAttributeValue("http-equiv", "").Equals("refresh", StringComparison.OrdinalIgnoreCase)) { n.Remove(); continue; }

            foreach (var a in n.Attributes.ToList())
            {
                var name = a.Name.ToLowerInvariant();
                var value = a.Value ?? "";
                if (name.StartsWith("on", StringComparison.Ordinal) || name is "srcdoc" or "formaction" or "xmlns:xlink") { a.Remove(); continue; }
                if (name is "href" or "src" or "action" or "background" or "xlink:href")
                {
                    var v = value.Trim();
                    if (v.StartsWith('#')) continue;
                    var hash = v.IndexOf('#');
                    if (name == "href" && hash >= 0 && hash < v.Length - 1) { a.Value = v[hash..]; continue; }
                    a.Remove();
                    if (name == "href") n.SetAttributeValue("title", "External link removed (offline viewer)");
                    continue;
                }
                if (name == "style" && (value.Contains("url(", StringComparison.OrdinalIgnoreCase) || value.Contains("expression(", StringComparison.OrdinalIgnoreCase)))
                    a.Remove();
            }
        }

        var head = doc.DocumentNode.SelectSingleNode("//head");
        if (head == null)
        {
            var htmlNode = doc.DocumentNode.SelectSingleNode("//html") ?? doc.DocumentNode;
            head = doc.CreateElement("head");
            htmlNode.PrependChild(head);
        }
        head.PrependChild(HtmlNode.CreateNode(
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:;\">"));
        head.AppendChild(HtmlNode.CreateNode(
            "<style>html{scroll-behavior:smooth}table.awr-highlight{outline:3px solid #f59e0b;outline-offset:3px;box-shadow:0 0 0 9999px rgba(15,23,42,.08);transition:outline-color .4s}" +
            "body{margin:12px}</style>"));
        return doc.DocumentNode.OuterHtml;
    }
}
