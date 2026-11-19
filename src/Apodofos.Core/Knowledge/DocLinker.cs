// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Core.Analysis;
using Apodofos.Core.Model;

namespace Apodofos.Core.Knowledge;

/// <summary>
/// Adds the most relevant <see cref="DocCatalog"/> entries to each finding's <see cref="Finding.Docs"/>.
/// Matching uses keywords found in the finding's evidence, title and source views; rarer keywords weigh more
/// (inverse document frequency over the catalog), so generic words never pull in unrelated pages.
/// Existing docs are kept and always listed first.
/// </summary>
public static class DocLinker
{
    public const int MaxDocs = 6;
    private const double Threshold = 4.0;

    private static readonly Lazy<Dictionary<string, double>> Idf = new(() =>
    {
        var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in DocCatalog.Entries)
            foreach (var k in KeywordsOf(e).Distinct(StringComparer.OrdinalIgnoreCase))
                df[k] = df.GetValueOrDefault(k) + 1;
        var n = Math.Max(1, DocCatalog.Entries.Count);
        return df.ToDictionary(kv => kv.Key, kv => Math.Log((double)n / kv.Value), StringComparer.OrdinalIgnoreCase);
    });

    private static IEnumerable<string> KeywordsOf(DocEntry e) =>
        e.Keywords.Concat(e.Sections).Where(k => k.Length >= 5);

    /// <summary>Adds catalog docs to every finding.</summary>
    public static void Link(IEnumerable<Finding> findings, ReportKind kind)
    {
        foreach (var f in findings)
        {
            try { Link(f, kind); }
            catch { /* documentation links are a convenience and must never break an analysis */ }
        }
    }

    public static void Link(Finding f, ReportKind kind)
    {
        var entries = DocCatalog.ForKind(kind).ToList();
        if (entries.Count == 0) return;

        var text = BuildText(f);
        var viewNames = new HashSet<string>(f.SourceViews.Select(v => v.Trim().ToUpperInvariant()));
        var idf = Idf.Value;

        var scored = new List<(DocEntry E, double Score)>();
        foreach (var e in entries)
        {
            double score = 0;
            foreach (var k in KeywordsOf(e).Distinct(StringComparer.OrdinalIgnoreCase))
                if (ContainsToken(text, k)) score += idf.GetValueOrDefault(k, 2.0);
            if (e.Tags.Contains("view") && viewNames.Contains(e.Title.ToUpperInvariant())) score += 8;
            if (score <= 0) continue;
            score += TypeBonus(e);
            if (score >= Threshold) scored.Add((e, score));
        }

        var existing = f.Docs.ToList();
        var seen = new HashSet<string>(existing.Select(d => d.Url), StringComparer.OrdinalIgnoreCase);
        var seenPages = new HashSet<string>(existing.Select(d => StripFragment(d.Url)), StringComparer.OrdinalIgnoreCase);
        var room = Math.Max(2, MaxDocs - existing.Count);

        foreach (var (e, _) in scored.OrderByDescending(x => x.Score).ThenBy(x => x.E.Id, StringComparer.Ordinal))
        {
            if (room == 0) break;
            if (!seen.Add(e.FullUrl)) continue;
            if (e.Anchor == null && seenPages.Contains(e.Url)) continue; // page already cited
            seenPages.Add(e.Url);
            f.Docs.Add(new DocRef(e.IsLocal ? e.Title + " (local file: " + e.Local + ")" : e.Book + ": " + e.Title, e.FullUrl));
            room--;
        }

        if (f.Docs.Count == 0) AddFallback(f, entries);
    }

    /// <summary>When nothing matched by keyword, point at the general guide chapter that fits the category.</summary>
    private static void AddFallback(Finding f, List<DocEntry> entries)
    {
        var cat = (f.Category + " " + f.Title).ToLowerInvariant();
        var wanted = new List<string>();
        void Has(string needle, string tag) { if (cat.Contains(needle)) wanted.Add(tag); }
        Has("memory", "memory"); Has("sga", "memory"); Has("pga", "memory");
        Has("i/o", "io"); Has("disk", "io"); Has("storage", "io");
        Has("redo", "redo"); Has("undo", "undo"); Has("parse", "parse"); Has("lock", "locking");
        Has("cpu", "cpu"); Has("latch", "latch"); Has("network", "network"); Has("sql", "sql");
        Has("config", "configuration"); Has("wait", "wait-event"); Has("rac", "rac"); Has("cluster", "rac");
        var pick = wanted
            .SelectMany(t => entries.Where(e => e.BookCode is "tgdba" or "tgsql" && e.Tags.Contains(t) && e.Anchor == null))
            .Distinct().Take(2);
        foreach (var e in pick) f.Docs.Add(new DocRef(e.Book + ": " + e.Title, e.FullUrl));
        if (f.Docs.Count == 0)
        {
            var general = entries.FirstOrDefault(e => e.Id == "tgdba.method");
            if (general != null) f.Docs.Add(new DocRef(general.Book + ": " + general.Title, general.FullUrl));
        }
    }

    private static double TypeBonus(DocEntry e) => e.Id switch
    {
        _ when e.Id.StartsWith("wait.", StringComparison.Ordinal) => 1.5,
        _ when e.Id.StartsWith("tgdba.inst.", StringComparison.Ordinal) => 1.0,
        _ when e.Id.StartsWith("param.", StringComparison.Ordinal) => 0.5,
        _ => 0
    };

    private static string BuildText(Finding f)
    {
        var parts = new List<string> { f.Title, f.Category };
        foreach (var ev in f.Evidence) { parts.Add(ev.Label); parts.Add(ev.Value); if (ev.Note != null) parts.Add(ev.Note); }
        parts.AddRange(f.SourceViews);
        parts.AddRange(f.TableRefs);
        parts.AddRange(f.Docs.Select(d => d.Title));
        return string.Join('\n', parts).ToLowerInvariant();
    }

    private static string StripFragment(string url)
    {
        var i = url.IndexOf('#');
        return i < 0 ? url : url[..i];
    }

    /// <summary>Case-insensitive match of <paramref name="keyword"/> on identifier boundaries (letters, digits, '_', '$', '#' continue a word).</summary>
    internal static bool ContainsToken(string lowerText, string keyword)
    {
        var k = keyword.ToLowerInvariant();
        var start = 0;
        while (true)
        {
            var i = lowerText.IndexOf(k, start, StringComparison.Ordinal);
            if (i < 0) return false;
            var before = i == 0 || !IsWordChar(lowerText[i - 1]);
            var end = i + k.Length;
            var after = end >= lowerText.Length || !IsWordChar(lowerText[end]);
            if (before && after) return true;
            start = i + 1;
        }
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';
}
