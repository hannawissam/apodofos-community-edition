// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace QuietLoad.Tests;

/// <summary>Number layouts the tests render a report in. Us is the original; every other layout must analyze identically.</summary>
public enum NumStyle { Us, German, French, FrenchNarrow, Swiss, Indian, Spanish }

/// <summary>Synthetic (self-made, no third-party content) reports and the helper that re-renders their numbers in another locale.</summary>
internal static partial class Synthetic
{
    public static readonly NumStyle[] Locales = [NumStyle.German, NumStyle.French, NumStyle.FrenchNarrow, NumStyle.Swiss, NumStyle.Indian, NumStyle.Spanish];

    [GeneratedRegex(@"(?<pad> *)(?<=^|[\s>(])(?<tok>-?(?:(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?|\.\d+))(?=[KMGT%]?(?:$|[\s<]))", RegexOptions.CultureInvariant)]
    private static partial Regex NumberToken();

    /// <summary>
    /// Re-renders every US-formatted number of a report ("1,234.5") in <paramref name="style"/>; keeps digit groups only where the
    /// original had them, and keeps fixed-width text columns aligned by trading the spaces in front of the number for the width it gained.
    /// </summary>
    public static string Localise(string content, NumStyle style)
    {
        if (style == NumStyle.Us) return content;
        return NumberToken().Replace(content, m =>
        {
            var tok = m.Groups["tok"].Value;
            var neu = ConvertToken(tok, style);
            var pad = m.Groups["pad"].Value;
            var delta = neu.Length - tok.Length;
            if (pad.Length > 0) pad = delta > 0 ? new string(' ', Math.Max(1, pad.Length - delta)) : pad + new string(' ', -delta);
            return pad + neu;
        });
    }

    public static string ConvertToken(string tok, NumStyle style)
    {
        var sign = tok.StartsWith('-') ? "-" : "";
        var body = tok.TrimStart('-');
        var dot = body.IndexOf('.');
        var intPart = (dot >= 0 ? body[..dot] : body);
        var frac = dot >= 0 ? body[(dot + 1)..] : null;
        var grouped = intPart.Contains(',');
        var digits = intPart.Replace(",", "");
        var (group, dec) = style switch
        {
            NumStyle.German => (".", ","),
            NumStyle.Spanish => (".", ","),
            NumStyle.French => (" ", ","),
            NumStyle.FrenchNarrow => (" ", ","),
            NumStyle.Swiss => ("’", "."),
            _ => (",", ".")
        };
        string shown;
        if (!grouped) shown = digits;
        else if (style == NumStyle.Indian) shown = IndianGroups(digits);
        else shown = Regroup(digits, group, 3);
        return sign + shown + (frac is null ? "" : dec + frac);
    }

    private static string Regroup(string digits, string sep, int size)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % size == 0) sb.Append(sep);
            sb.Append(digits[i]);
        }
        return sb.ToString();
    }

    public static string IndianGroups(string digits)
    {
        if (digits.Length <= 3) return digits;
        var last3 = digits[^3..];
        var head = digits[..^3];
        var sb = new StringBuilder();
        for (var i = 0; i < head.Length; i++)
        {
            if (i > 0 && (head.Length - i) % 2 == 0) sb.Append(',');
            sb.Append(head[i]);
        }
        return sb + "," + last3;
    }

    // ------------------------------------------------------------------------------------------------ AWR html

    private static string Cell(string v, bool right = true) => $"<td align=\"{(right ? "right" : "left")}\" class=\"awrc\">{v}</td>";
    private static string Row(params string[] cells) => "<tr>" + string.Concat(cells.Select((c, i) => Cell(c, i > 0))) + "</tr>\n";
    private static string Table(string title, string[] headers, IEnumerable<string[]> rows) =>
        $"</p><p>{title}</p><p>\n<table border=\"1\" width=\"500\">\n<tbody><tr>" + string.Concat(headers.Select(h => $"<th class=\"awrbg\">{h}</th>")) + "</tr>\n"
        + string.Concat(rows.Select(r => Row(r))) + "</tbody></table>\n";

    /// <summary>
    /// A made-up 19c-style AWR HTML report in US number format (60 minute window, 4 CPUs, DB time 120 minutes, a lock-wait problem
    /// plus an I/O-bound statement). Pass options to change single facts for regression tests.
    /// </summary>
    public static string AwrHtml(Action<AwrOptions>? configure = null)
    {
        var o = new AwrOptions();
        configure?.Invoke(o);
        var sb = new StringBuilder();
        sb.Append("<html><head><title>AWR Report</title></head><body>\n<h1>WORKLOAD REPOSITORY report for</h1>\n");
        sb.Append(Table("Database Instance Information", ["DB Name", "DB Id", "Instance", "Inst num", "Startup Time", "Release", "RAC", "CDB"],
            [[ "SYNTH", "1,234,567,890", o.Instance, "1", "01-Jan-26 08:00", o.Release, "NO", "NO" ]]));
        sb.Append(Table("Host Information", ["Host Name", "Platform", "CPUs", "Cores", "Sockets", "Memory (GB)"],
            [[ "host1.example", "Linux x86 64-bit", "4", "4", "1", "15.60" ]]));
        sb.Append(Table("Snapshot Information", ["", "Snap Id", "Snap Time", "Sessions", "Cursors/Session"],
            [["Begin Snap:", o.BeginSnap, o.BeginTime, "58", "4.8"], ["End Snap:", o.EndSnap, o.EndTime, "61", "5.1"],
             ["Elapsed:", "", $"{o.ElapsedText}", "", ""], ["DB Time:", "", $"{o.DbTimeText}", "", ""]]));
        sb.Append(Table("Load Profile", ["", "Per Second", "Per Transaction", "Per Exec", "Per Call"],
            [["DB Time(s):", "2.0", "0.1", "0.01", "0.06"], ["DB CPU(s):", "0.8", "0.0", "0.00", "0.02"],
             ["Redo size (bytes):", "1,520,366.3", "12,072.8", "", ""], ["Logical read (blocks):", "45,543.2", "355.3", "", ""],
             ["Physical read (blocks):", "1,942.3", "15.2", "", ""], ["Hard parses (SQL):", "1.5", "0.0", "", ""],
             ["Executes (SQL):", "1,234.7", "9.6", "", ""], ["Transactions:", "128.4", "", "", ""]]));
        sb.Append(Table("Instance Efficiency Percentages (Target 100%)", ["", "", "", ""],
            [["Buffer Nowait %:", "99.97", "Redo NoWait %:", "100.00"], ["Buffer Hit %:", "98.20", "In-memory Sort %:", "100.00"],
             ["Library Hit %:", "99.10", "Soft Parse %:", "98.80"], ["Execute to Parse %:", "92.30", "Latch Hit %:", "99.90"]]));
        sb.Append(Table("Top 10 Foreground Events by Total Wait Time", ["Event", "Waits", "Total Wait Time (sec)", "Wait Avg(ms)", "% DB time", "Wait Class"],
            [["DB CPU", "", "2,880", "", o.CpuShare, ""], ["db file sequential read", "1,234,567", "2,160", "1.75", "30.0", "User I/O"],
             ["log file sync", "345,678", "864", "2.50", "12.0", "Commit"], ["enq: TX - row lock contention", "1,234", "576", "466.76", "8.0", "Application"]]));
        sb.Append(Table("Wait Classes by Total Wait Time", ["Wait Class", "Waits", "Total Wait Time (sec)", "Avg Wait (ms)", "% DB time"],
            [["DB CPU", "", "2,880", "", "40.0"], ["User I/O", "1,300,000", "2,300", "1.77", "32.0"], ["Commit", "345,678", "864", "2.50", "12.0"],
             ["Application", "1,234", "576", "466.76", "8.0"], ["System I/O", "3,000,000", "1,500", "0.50", "20.8"]]));
        sb.Append(Table("Foreground Wait Class", ["Wait Class", "Waits", "%Time -outs", "Total Wait Time (s)", "Avg wait (ms)", "%DB time"],
            [["DB CPU", "", "", "2,880", "", "40.0"], ["User I/O", "1,300,000", "0.0", "2,300", "1.77", "32.0"], ["Commit", "345,678", "0.0", "864", "2.50", "12.0"],
             ["Application", "1,234", "0.0", "576", "466.76", "8.0"]]));
        sb.Append(Table("Time Model Statistics", ["Statistic Name", "Time (s)", "% of DB Time"],
            [["sql execute elapsed time", "6,300.0", "87.5"], ["DB CPU", "2,880.0", "40.0"], ["parse time elapsed", "40.0", "0.6"], ["DB time", "7,200.0", ""]]));
        sb.Append(Table("SQL ordered by Elapsed Time", ["Elapsed Time (s)", "Executions", "Elapsed Time per Exec (s)", "%Total", "%CPU", "%IO", "SQL Id", "SQL Module", "SQL Text"],
            [["1,820.5", "12,345", "0.15", o.SqlShare, "10.2", "80.1", "abcd1234efgh5", "JDBC Thin Client", "SELECT * FROM orders WHERE customer_id = :1"],
             ["400.2", "120", "3.34", "5.6", "90.0", "5.0", "wxyz9876stuv1", "SQL*Plus", "UPDATE accounts SET balance = balance - :1 WHERE id = :2"]]));
        sb.Append(Table("Host CPU", ["CPUs", "Cores", "Sockets", "Load Average Begin", "Load Average End", "%User", "%System", "%WIO", "%Idle"],
            [["4", "4", "1", "1.20", "1.55", "22.5", "6.1", "3.2", "70.1"]]));
        sb.Append("<p>End of Report</p>\n</body></html>\n");
        return o.Truncate ? sb.ToString()[..(sb.Length * 7 / 10)] : sb.ToString();
    }

    public sealed class AwrOptions
    {
        public string Instance = "SYNTH1";
        public string Release = "19.0.0.0.0";
        public string BeginSnap = "100";
        public string EndSnap = "101";
        public string BeginTime = "10-Mar-26 10:00:00";
        public string EndTime = "10-Mar-26 11:00:00";
        public string ElapsedText = "60.00 (mins)";
        public string DbTimeText = "120.00 (mins)";
        public string CpuShare = "40.0";
        public string SqlShare = "25.3";
        public bool Truncate;
    }
}
