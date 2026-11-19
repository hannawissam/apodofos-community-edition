// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuietLoad.Core.Model;

namespace QuietLoad.Core.Knowledge;

/// <summary>
/// One reference into the official Oracle documentation. Only facts and original wording are stored
/// (title, URL, anchor, keywords, a short own-words summary); no Oracle text is embedded.
/// </summary>
public sealed record DocEntry
{
    public string Id { get; init; } = "";
    /// <summary>Official page or section title (a factual reference).</summary>
    public string Title { get; init; } = "";
    /// <summary>Canonical page URL without fragment (https://docs.oracle.com/..., or file:/// for the offline local reference).</summary>
    public string Url { get; init; } = "";
    /// <summary>Fragment id of the section inside the page (verified to exist), or null.</summary>
    public string? Anchor { get; init; }
    /// <summary>Human-readable hint of where to look on the page.</summary>
    public string? Section { get; init; }
    public string Book { get; init; } = "";
    public string BookCode { get; init; } = "";
    public List<string> Releases { get; init; } = [];
    /// <summary>awr | ash | statspack</summary>
    public List<string> Kinds { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    /// <summary>Statistic / wait event / parameter / view names and report section titles the entry is about.</summary>
    public List<string> Keywords { get; init; } = [];
    /// <summary>Report section (table) titles this entry explains; drives analysis.sectionDocs.</summary>
    public List<string> Sections { get; init; } = [];
    /// <summary>One or two sentences in our own words (max 240 characters).</summary>
    public string Summary { get; init; } = "";
    public string? VerifiedOn { get; init; }
    /// <summary>For an offline-only reference (e.g. spdoc.txt): where the file lives on the database host.</summary>
    public string? Local { get; init; }

    [JsonIgnore]
    public string FullUrl => Anchor is { Length: > 0 } ? Url + "#" + Anchor : Url;

    public bool IsLocal => Local != null;
}

/// <summary>Lightweight form of a catalog entry sent to the UI (analysis.docs[]).</summary>
public sealed record DocDto(string Id, string Title, string Url, string Book, List<string> Tags, string Summary, List<string> Kinds, List<string> Releases, string? Section, string? Local)
{
    public static DocDto From(DocEntry e) => new(e.Id, e.Title, e.FullUrl, e.Book, e.Tags, e.Summary, e.Kinds, e.Releases, e.Section, e.Local);
}

internal sealed class DocCatalogFile
{
    public int Schema { get; set; }
    public string? Generated { get; set; }
    public List<DocEntry> Entries { get; set; } = [];
}

/// <summary>
/// Embedded, offline catalog of Oracle documentation references for AWR, ASH and Statspack analysis.
/// Loaded once from the <c>QuietLoad.Docs.doc-catalog.json</c> resource. Never touches the network.
/// </summary>
public static class DocCatalog
{
    internal const string ResourceName = "QuietLoad.Docs.doc-catalog.json";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private static readonly Lazy<IReadOnlyList<DocEntry>> All = new(Load);
    private static readonly Lazy<Dictionary<string, DocEntry>> ByIdMap =
        new(() => All.Value.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyList<DocEntry> Entries => All.Value;

    public static string KindName(ReportKind k) => k switch { ReportKind.Ash => "ash", ReportKind.Statspack => "statspack", _ => "awr" };

    /// <summary>Lookup by id (case-insensitive); null when unknown.</summary>
    public static DocEntry? Get(string id) => ByIdMap.Value.GetValueOrDefault(id);

    /// <summary>All entries that apply to a report kind.</summary>
    public static IEnumerable<DocEntry> ForKind(ReportKind kind)
    {
        var k = KindName(kind);
        return All.Value.Where(e => e.Kinds.Contains(k));
    }

    /// <summary>
    /// Free-text search over id, title, keywords, tags, section titles and summary. Every whitespace-separated term must match.
    /// Exact keyword hits rank first.
    /// </summary>
    public static IReadOnlyList<DocEntry> Search(string query, ReportKind? kind = null, string? tag = null, int max = 50)
    {
        var terms = (query ?? "").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToLowerInvariant()).ToArray();
        IEnumerable<DocEntry> src = kind is { } k ? ForKind(k) : All.Value;
        if (!string.IsNullOrWhiteSpace(tag)) src = src.Where(e => e.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        var scored = new List<(DocEntry E, int Score)>();
        foreach (var e in src)
        {
            var hay = (e.Id + " " + e.Title + " " + string.Join(' ', e.Keywords) + " " + string.Join(' ', e.Tags) + " " + string.Join(' ', e.Sections) + " " + e.Summary).ToLowerInvariant();
            if (terms.Length > 0 && !terms.All(hay.Contains)) continue;
            var score = 0;
            var phrase = string.Join(' ', terms);
            if (phrase.Length > 0)
            {
                if (e.Keywords.Any(x => x.Equals(phrase, StringComparison.OrdinalIgnoreCase))) score += 100;
                else if (e.Keywords.Any(x => x.Contains(phrase, StringComparison.OrdinalIgnoreCase))) score += 40;
                if (e.Title.Contains(phrase, StringComparison.OrdinalIgnoreCase)) score += 30;
                if (e.Sections.Any(x => x.Contains(phrase, StringComparison.OrdinalIgnoreCase))) score += 30;
            }
            scored.Add((e, score));
        }
        return scored.OrderByDescending(x => x.Score).Select(x => x.E).Take(max).ToList();
    }

    /// <summary>Entries tagged with a topic (e.g. "wait-event", "licensing").</summary>
    public static IEnumerable<DocEntry> ByTag(string tag) =>
        All.Value.Where(e => e.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));

    /// <summary>Entries that explain a report section (table title), longest matching section first.</summary>
    public static IReadOnlyList<DocEntry> ForSection(string tableTitle, ReportKind kind)
    {
        if (string.IsNullOrWhiteSpace(tableTitle)) return [];
        var res = new List<(DocEntry E, int Len, int Order)>();
        var i = 0;
        foreach (var e in ForKind(kind))
        {
            i++;
            var best = -1;
            foreach (var s in e.Sections)
                if (s.Length > best && tableTitle.Contains(s, StringComparison.OrdinalIgnoreCase)) best = s.Length;
            if (best >= 0) res.Add((e, best, e.BookCode is "tgdba" or "tgsql" ? 0 : 1));
        }
        return res.OrderByDescending(x => x.Len).ThenBy(x => x.Order).Select(x => x.E).ToList();
    }

    /// <summary>Report section index: table id to doc entry ids (at most <paramref name="perTable"/> each).</summary>
    public static Dictionary<string, List<string>> BuildSectionIndex(IEnumerable<(string Id, string Title)> tables, ReportKind kind, int perTable = 4)
    {
        var map = new Dictionary<string, List<string>>();
        foreach (var (id, title) in tables)
        {
            var hits = ForSection(title, kind).Take(perTable).Select(e => e.Id).ToList();
            if (hits.Count > 0) map[id] = hits;
        }
        return map;
    }

    private static IReadOnlyList<DocEntry> Load()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (s == null) return [];
        var file = JsonSerializer.Deserialize<DocCatalogFile>(s, JsonOptions);
        return file?.Entries ?? [];
    }

    /// <summary>Serialises entries in the catalog file format (used by tests for round-tripping).</summary>
    internal static string ToJson(IEnumerable<DocEntry> entries) =>
        JsonSerializer.Serialize(new DocCatalogFile { Schema = 1, Entries = entries.ToList() }, JsonOptions);

    internal static IReadOnlyList<DocEntry> FromJson(string json) =>
        JsonSerializer.Deserialize<DocCatalogFile>(json, JsonOptions)?.Entries ?? [];
}
