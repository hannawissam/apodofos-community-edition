// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text.RegularExpressions;
using QuietLoad.Core.Analysis;
using QuietLoad.Core.Knowledge;
using QuietLoad.Core.Model;

namespace QuietLoad.Tests;

/// <summary>Offline checks of the embedded documentation catalog. No network access: links are verified by tools/verify-doc-links.ps1.</summary>
public class DocCatalogTests
{
    private static readonly string[] Kinds = ["awr", "ash", "statspack"];

    [Fact]
    public void CatalogIsLoadedAndLarge()
    {
        Assert.True(DocCatalog.Entries.Count >= 200, $"only {DocCatalog.Entries.Count} entries");
    }

    [Fact]
    public void IdsAreUniqueAndWellFormed()
    {
        var dup = DocCatalog.Entries.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dup);
        Assert.All(DocCatalog.Entries, e => Assert.Matches(@"^[a-z0-9_.]+$", e.Id));
    }

    [Fact]
    public void UrlsAreHttpsOnOracleDocsOrDocumentedLocalReference()
    {
        foreach (var e in DocCatalog.Entries)
        {
            if (e.IsLocal)
            {
                Assert.StartsWith("file:///", e.Url);
                Assert.False(string.IsNullOrWhiteSpace(e.Local));
                continue;
            }
            Assert.True(Uri.TryCreate(e.FullUrl, UriKind.Absolute, out var u), $"{e.Id}: bad url");
            Assert.Equal("https", u!.Scheme);
            Assert.Equal("docs.oracle.com", u.Host);
            Assert.False(string.IsNullOrEmpty(e.VerifiedOn), $"{e.Id}: no verifiedOn");
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", e.VerifiedOn!);
            if (e.Anchor != null) Assert.DoesNotContain('#', e.Url);
        }
    }

    [Fact]
    public void SummariesAreShortOriginalAndNonEmpty()
    {
        foreach (var e in DocCatalog.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Summary), $"{e.Id}: empty summary");
            Assert.True(e.Summary.Length <= 240, $"{e.Id}: summary {e.Summary.Length} chars");
            Assert.False(string.IsNullOrWhiteSpace(e.Title));
            Assert.True(e.Title.Length <= 160, $"{e.Id}: title too long");
            Assert.NotEmpty(e.Kinds);
            Assert.All(e.Kinds, k => Assert.Contains(k, Kinds));
            Assert.NotEmpty(e.Tags);
            Assert.NotEmpty(e.Keywords);
            Assert.NotEmpty(e.Releases);
        }
        // No bulk text: whole catalogue text per entry stays small, so page content cannot have been pasted in.
        Assert.All(DocCatalog.Entries, e => Assert.True(e.Summary.Length + e.Keywords.Sum(k => k.Length + 2) < 1200, $"{e.Id}: too much text"));
        // No two entries share the same long summary sentence (guards against pasted boilerplate).
        var longSentences = DocCatalog.Entries.SelectMany(e => Regex.Split(e.Summary, @"(?<=[.!?])\s+").Where(s => s.Length > 90).Select(s => (e.Id, s)));
        Assert.Empty(longSentences.GroupBy(x => x.s).Where(g => g.Count() > 1).Select(g => g.Key));
    }

    [Theory]
    [InlineData(ReportKind.Awr)]
    [InlineData(ReportKind.Ash)]
    [InlineData(ReportKind.Statspack)]
    public void EveryReportKindHasEntries(ReportKind kind)
    {
        Assert.True(DocCatalog.ForKind(kind).Count() >= 20);
    }

    [Fact]
    public void StatspackHasOfflineReferenceAndAshHasItsChapter()
    {
        var sp = DocCatalog.Get("statspack.spdoc");
        Assert.NotNull(sp);
        Assert.True(sp!.IsLocal);
        Assert.Contains("spdoc.txt", sp.Local);
        Assert.NotNull(DocCatalog.Get("tgdba.ash.about"));
        Assert.NotNull(DocCatalog.Get("tgdba.awr.reports"));
    }

    [Theory]
    [InlineData("buffer busy waits", "wait.buffer_busy_waits")]
    [InlineData("log file sync", "wait.log_file_sync")]
    [InlineData("DBA_HIST_SQLSTAT", "view.dba_hist_sqlstat")]
    [InlineData("cursor_sharing", "param.cursor_sharing")]
    [InlineData("STATS$SNAPSHOT", "statspack.spdoc")]
    [InlineData("ashrpt.sql", "tgdba.ash.generate")]
    [InlineData("Activity Over Time", "tgdba.ash.activity")]
    public void KeywordSearchFindsExpectedEntry(string query, string expectedId)
    {
        var hits = DocCatalog.Search(query);
        Assert.Contains(hits, e => e.Id == expectedId);
    }

    [Fact]
    public void SearchFiltersByKindAndTag()
    {
        Assert.DoesNotContain(DocCatalog.Search("DBA_HIST_SQLSTAT", ReportKind.Statspack), e => e.Id == "view.dba_hist_sqlstat");
        Assert.All(DocCatalog.Search("lock", null, "wait-event"), e => Assert.Contains("wait-event", e.Tags));
        Assert.Equal("view.dba_hist_sqlstat", DocCatalog.Get("VIEW.DBA_HIST_SQLSTAT")?.Id);
    }

    [Fact]
    public void SectionIndexMapsKnownReportSectionTitles()
    {
        Assert.Contains(DocCatalog.ForSection("Load Profile", ReportKind.Awr), e => e.Id == "tgdba.sect.loadprofile");
        Assert.Contains(DocCatalog.ForSection("Top 10 Foreground Events by Total Wait Time", ReportKind.Awr), e => e.Id == "tgdba.sect.top5");
        Assert.Contains(DocCatalog.ForSection("Time Model Statistics", ReportKind.Awr), e => e.Id == "tgdba.sect.timemodel");
        Assert.Contains(DocCatalog.ForSection("SQL ordered by Elapsed Time", ReportKind.Awr), e => e.Id == "tgdba.sect.sql");
        Assert.Contains(DocCatalog.ForSection("Instance Efficiency Percentages (Target 100%)", ReportKind.Awr), e => e.Id == "tgdba.measure.hitratio");
        Assert.Contains(DocCatalog.ForSection("Activity Over Time", ReportKind.Ash), e => e.Id == "tgdba.ash.activity");
        Assert.Contains(DocCatalog.ForSection("Top User Events", ReportKind.Ash), e => e.Id == "tgdba.ash.topuser");
        Assert.Contains(DocCatalog.ForSection("Statspack snapshot level", ReportKind.Statspack), e => e.Id == "statspack.spdoc");
        // An AWR-only entry must not be offered for a Statspack-only section list.
        Assert.DoesNotContain(DocCatalog.ForSection("Load Profile", ReportKind.Statspack), e => e.Kinds.Contains("awr") && !e.Kinds.Contains("statspack"));
    }

    [Theory, MemberData(nameof(Samples.AllHtmlData), MemberType = typeof(Samples))]
    public void DocLinkerAddsDocsToExampleReports(string path)
    {
        var result = AnalysisEngine.Analyze(Samples.Load(path));
        Assert.NotEmpty(result.Docs);
        Assert.All(result.Docs, d => { Assert.False(string.IsNullOrEmpty(d.Id)); Assert.False(string.IsNullOrEmpty(d.Summary)); });
        Assert.NotEmpty(result.SectionDocs);
        var tableIds = result.Tables.Select(t => t.Id).ToHashSet();
        Assert.All(result.SectionDocs, kv => { Assert.Contains(kv.Key, tableIds); Assert.All(kv.Value, id => Assert.NotNull(DocCatalog.Get(id))); });

        var catalogUrls = DocCatalog.Entries.Select(e => e.FullUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actionable = result.Findings.Where(f => f.Severity != Severity.Ok).ToList();
        Assert.NotEmpty(actionable);
        Assert.All(result.Findings, f =>
        {
            Assert.Equal(f.Docs.Count, f.Docs.Select(d => d.Url).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });
        Assert.Contains(result.Findings, f => f.Docs.Any(d => catalogUrls.Contains(d.Url)));
        // Findings with evidence about a known wait event link to the matching catalog entry.
        Assert.All(result.Findings.Where(f => f.Docs.Count == 0), f => Assert.Fail($"finding {f.Id} has no docs"));
    }

    [Fact]
    public void DocLinkerMatchesKeywordsAndKeepsExistingDocs()
    {
        var f = new Finding
        {
            Title = "Commit latency is high",
            Category = "Redo",
            Evidence = [new Evidence("Top event", "log file sync")],
            SourceViews = ["DBA_HIST_SYSTEM_EVENT"],
            Docs = [OracleDocs.RedoLog]
        };
        DocLinker.Link(f, ReportKind.Awr);
        Assert.Equal(OracleDocs.RedoLog.Url, f.Docs[0].Url);
        Assert.Contains(f.Docs, d => d.Url.Contains("descriptions-of-wait-events.html#GUID-"));
        Assert.True(f.Docs.Count <= 6 + 1);
    }

    [Fact]
    public void CatalogJsonRoundTrips()
    {
        var json = DocCatalog.ToJson(DocCatalog.Entries);
        var back = DocCatalog.FromJson(json);
        Assert.Equal(DocCatalog.Entries.Count, back.Count);
        Assert.Equal(DocCatalog.Entries[10].Id, back[10].Id);
        Assert.Equal(DocCatalog.Entries[10].FullUrl, back[10].FullUrl);
        Assert.Equal(DocCatalog.Entries[10].Keywords, back[10].Keywords);
    }

    [Fact]
    public void ExistingOracleDocsConstantsAreStillPresent()
    {
        Assert.StartsWith("https://docs.oracle.com/", OracleDocs.Awr.Url);
        Assert.EndsWith("/refrn/CURSOR_SHARING.html", OracleDocs.Parameter("cursor_sharing").Url);
    }
}
