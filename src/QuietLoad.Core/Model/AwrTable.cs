// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using QuietLoad.Core.Parsing;

namespace QuietLoad.Core.Model;

/// <summary>
/// One table extracted from an AWR report, kept in a generic (header + rows) shape so that
/// every metric in the report can be charted and the rules can address columns by name,
/// independent of small layout differences between Oracle releases.
/// </summary>
public sealed class AwrTable
{
    public required string Id { get; init; }
    public required int Index { get; init; }
    /// <summary>Nearest heading / caption text before the table (e.g. "Load Profile").</summary>
    public required string Title { get; set; }
    /// <summary>Top-level report section (h2), e.g. "Wait Events Statistics".</summary>
    public string? Section { get; init; }
    /// <summary>The HTML "summary" attribute Oracle writes on most AWR tables.</summary>
    public string? Summary { get; init; }
    public List<string> Headers { get; init; } = [];
    public List<string[]> Rows { get; init; } = [];

    /// <summary>
    /// Cells whose value is impossible for their column (a share above 100%, see <c>SanityScan</c>), keyed by row array.
    /// <see cref="AwrRow.Num"/> returns null for them so no rule can build a finding on an impossible number.
    /// </summary>
    public Dictionary<string[], HashSet<int>> InvalidCells { get; } = new(ReferenceEqualityComparer.Instance);

    public int ColumnCount => Math.Max(Headers.Count, Rows.Count == 0 ? 0 : Rows.Max(r => r.Length));

    public bool TitleIs(string title) => string.Equals(Normalize(Title), Normalize(title), StringComparison.Ordinal);
    public bool TitleStartsWith(string prefix) => Normalize(Title).StartsWith(Normalize(prefix), StringComparison.Ordinal);
    public bool TitleContains(string fragment) => Normalize(Title).Contains(Normalize(fragment), StringComparison.Ordinal);

    public bool HasColumn(params string[] candidates) => Col(candidates) >= 0;

    /// <summary>
    /// Finds a column by header. Exact (normalized) matches win over "contains" matches, and
    /// candidates are tried in order, which lets callers list the 19c header first and older
    /// variants afterwards.
    /// </summary>
    public int Col(params string[] candidates)
    {
        var normalized = Headers.Select(Normalize).ToList();
        foreach (var c in candidates)
        {
            var n = Normalize(c);
            var idx = normalized.IndexOf(n);
            if (idx >= 0) return idx;
        }
        foreach (var c in candidates)
        {
            var n = Normalize(c);
            for (var i = 0; i < normalized.Count; i++)
                if (normalized[i].Length > 0 && normalized[i].Contains(n, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    public IEnumerable<AwrRow> DataRows => Rows.Select(r => new AwrRow(this, r));

    /// <summary>Finds the first row whose first cell (label) matches.</summary>
    public AwrRow? RowByLabel(params string[] labels)
    {
        foreach (var l in labels)
        {
            var n = Normalize(l);
            foreach (var r in Rows)
                if (r.Length > 0 && Normalize(r[0]) == n) return new AwrRow(this, r);
        }
        foreach (var l in labels)
        {
            var n = Normalize(l);
            foreach (var r in Rows)
                if (r.Length > 0 && Normalize(r[0]).StartsWith(n, StringComparison.Ordinal)) return new AwrRow(this, r);
        }
        return null;
    }

    /// <summary>Lower-case, alphanumerics and % only, so "Buffer  Hit   %:" == "buffer hit %".</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        Span<char> buf = stackalloc char[Math.Min(s.Length, 512)];
        var len = 0;
        var lastSpace = true;
        foreach (var ch in s)
        {
            if (len >= buf.Length) break;
            if (char.IsLetterOrDigit(ch) || ch == '%' || ch == '/')
            {
                buf[len++] = char.ToLowerInvariant(ch);
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                buf[len++] = ' ';
                lastSpace = true;
            }
        }
        return new string(buf[..len]).Trim();
    }

    public override string ToString() => $"{Id}: {Title} [{Headers.Count} cols x {Rows.Count} rows]";
}

/// <summary>A row bound to its table so values can be fetched by column name.</summary>
public readonly record struct AwrRow(AwrTable Table, string[] Cells)
{
    public string Label => Cells is { Length: > 0 } ? Cells[0] : string.Empty;

    public string Str(params string[] columns)
    {
        var i = Table?.Col(columns) ?? -1; // default(AwrRow) has no table
        return i >= 0 && Cells != null && i < Cells.Length ? Cells[i] : string.Empty;
    }

    public double? Num(params string[] columns)
    {
        var i = Table?.Col(columns) ?? -1; // default(AwrRow) has no table
        return i >= 0 && Cells != null && i < Cells.Length ? NumAt(i) : null;
    }

    /// <summary>Numeric value of a cell, null when blank, not numeric or flagged impossible by the sanity scan.</summary>
    public double? NumAt(int index)
    {
        if (index < 0 || Cells == null || index >= Cells.Length) return null;
        if (Table != null && Table.InvalidCells.Count > 0 && Table.InvalidCells.TryGetValue(Cells, out var bad) && bad.Contains(index)) return null;
        return NumberParser.Parse(Cells[index]);
    }

    /// <summary>The raw parsed value even when the sanity scan flagged it (used only to explain the problem).</summary>
    public double? NumRaw(int index) => index >= 0 && Cells != null && index < Cells.Length ? NumberParser.Parse(Cells[index]) : null;
    public string StrAt(int index) => index >= 0 && index < Cells.Length ? Cells[index] : string.Empty;
}
