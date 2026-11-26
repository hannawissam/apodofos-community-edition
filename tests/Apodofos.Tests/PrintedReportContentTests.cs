// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Apodofos.Core.Analysis;
using Apodofos.Core.Export;
using Apodofos.Core.Providers;

namespace Apodofos.Tests;

/// <summary>
/// What the print / PDF report actually says. <c>report.js</c> is run under Node.js (when installed, as in
/// <see cref="UiScriptSyntaxTests"/>) against a minimal DOM stand-in, on the analyses of the synthetic reports: the
/// consolidated documentation list with its anchors and links, and the application version.
/// </summary>
public class PrintedReportContentTests
{
    private const string Heading = "Oracle documentation referenced by this analysis";

    public static IEnumerable<object[]> Reports() =>
        new[] { "awr-19c-synthetic.html", "awr-11g-synthetic.html", "ash-19c-hot-event.html", "ash-19c-hot-event.txt", "statspack-19c-layout.txt", "statspack-10g-layout.txt" }
            .Select(f => new object[] { f });

    private static AnalysisResult Analyze(string file)
    {
        var o = ReportLoader.Load(Path.Combine(AppContext.BaseDirectory, "TestData", file));
        Assert.NotNull(o.Result);
        return o.Result!.Analysis;
    }

    /// <summary>The version in Directory.Build.props: the only number the product may print about itself.</summary>
    internal static string BuildVersion()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Apodofos.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var m = Regex.Match(File.ReadAllText(Path.Combine(dir!.FullName, "Directory.Build.props")), @"<Version>([^<]+)</Version>");
        Assert.True(m.Success, "Directory.Build.props has no <Version>");
        return m.Groups[1].Value.Trim();
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public void EveryFindingAboveHealthyCitesDocumentation(string file)
    {
        var without = Analyze(file).Findings.Where(f => f.Severity != Severity.Ok && !f.Docs.Any(d => d.Url.StartsWith("https://", StringComparison.Ordinal)))
            .Select(f => $"{f.Id} {f.Severity} {f.Title}").ToList();
        Assert.True(without.Count == 0, "Findings without an https documentation reference:\n" + string.Join("\n", without));
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public void TheReferenceListIsNumberedDeduplicatedAndLinkedBothWays(string file)
    {
        var analysis = Analyze(file);
        if (Render(analysis, "") is not { } r) return;

        var cited = analysis.Findings.SelectMany(f => f.Docs.Select(d => d.Url)).Distinct(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(cited);
        Assert.Equal(cited.Order(StringComparer.Ordinal), r.Refs.Select(x => x.Url).Order(StringComparer.Ordinal)); // one entry per URL
        Assert.Equal(Enumerable.Range(1, cited.Count), r.Refs.Select(x => x.N));
        foreach (var x in r.Refs)
        {
            Assert.False(string.IsNullOrWhiteSpace(x.Title), x.Url);
            Assert.Equal(analysis.Findings.Where(f => f.Docs.Any(d => d.Url == x.Url)).Select(f => f.Id), x.Findings);
            if (x.Url.StartsWith("https://", StringComparison.Ordinal)) Assert.Equal(x.Url, x.Href);
            else Assert.Null(x.Href); // the offline spdoc.txt reference is never a link
            var entry = analysis.Docs.FirstOrDefault(d => d.Url == x.Url);
            if (entry != null) { Assert.Equal(entry.Title, x.Title); Assert.Equal(entry.Book, x.Book); }
        }

        var ids = r.Nodes.Where(n => n.Id != null).Select(n => n.Id!).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var x in r.Refs) Assert.Contains("rpt-ref-" + x.N, ids);

        var links = r.Nodes.Where(n => n.Tag == "a").ToList();
        var inPage = links.Where(a => a.Href!.StartsWith('#')).ToList();
        Assert.NotEmpty(inPage);
        Assert.All(inPage, a => Assert.Contains(a.Href![1..], ids));
        Assert.Contains(inPage, a => a.Href!.StartsWith("#rpt-ref-", StringComparison.Ordinal)); // citation to list
        Assert.Contains(inPage, a => a.Href!.StartsWith("#rpt-f-", StringComparison.Ordinal));   // list to finding
        Assert.All(links.Except(inPage), a =>
        {
            Assert.StartsWith("https://", a.Href, StringComparison.Ordinal);
            Assert.Equal("_blank", a.Target);
            Assert.Equal("noopener noreferrer", a.Rel);
        });
        foreach (var x in r.Refs.Where(x => x.Href != null)) Assert.Contains("\n" + x.Href + "\n", r.Text); // the address is printed

        Assert.Contains(Heading, r.Text);
        Assert.Contains("not every threshold or recommendation in this analysis comes from Oracle documentation", r.Text);
        Assert.Contains("needs internet access", r.Text);
        Assert.Contains("the application itself fetches nothing", r.Text);
    }

    [Theory]
    [MemberData(nameof(Reports))]
    public void ThePrintedReportStatesTheBuildVersionAndNoOther(string file)
    {
        var version = BuildVersion();
        var analysis = Analyze(file);
        Assert.Equal(version, AnalysisEngine.Version);
        Assert.Equal(version, analysis.Meta.AnalyzerVersion);

        // a saved page: no desktop host, so no application version is passed in
        if (Render(analysis, "") is not { } r) return;
        var product = analysis.Meta.Product;
        Assert.Contains($"·  Apodofos {version}  ·", r.Text);                // cover
        Assert.Contains($"Generated by {product} {version}.", r.Text);        // closing footer
        Assert.Contains($"v{version}", r.Text);                              // facts
        Assert.Contains($"content: \"{product} {version}\"", r.Text);         // running footer of every page
        var printed = Regex.Matches(r.Text, @"(?:" + Regex.Escape(product) + @"|Apodofos|\bv|analysis engine) ?(\d+(?:\.\d+)+(?:-[\w.]*\w)?)").Select(m => m.Groups[1].Value).ToList();
        Assert.True(printed.Count >= 4, "version occurrences: " + string.Join(", ", printed));
        Assert.All(printed, v => Assert.Equal(version, v));
        Assert.DoesNotContain("2.0.0", r.Text);
    }

    [Fact]
    public void WhenTheApplicationAndTheEngineDifferBothAreShownAndNothingIsInvented()
    {
        var analysis = Analyze("awr-19c-synthetic.html");
        var engine = analysis.Meta.AnalyzerVersion;

        if (Render(analysis, engine + "+abc123") is not { } same) return; // build metadata is not part of the version
        Assert.Contains($"Generated by {analysis.Meta.Product} {engine}.", same.Text);
        Assert.DoesNotContain("analysis engine", same.Text);

        var differ = Render(analysis, "9.8.7")!;
        Assert.Contains($"Apodofos 9.8.7 (analysis engine {engine})", differ.Text);
        Assert.Contains($"Generated by {analysis.Meta.Product} 9.8.7 (analysis engine {engine}).", differ.Text);

        analysis.Meta.AnalyzerVersion = ""; // an old payload without a version
        var none = Render(analysis, "")!;
        Assert.Contains($"Generated by {analysis.Meta.Product}.", none.Text);
        Assert.Contains($"content: \"{analysis.Meta.Product}\"", none.Text);
    }

    [Fact]
    public void TheDashboardTakesTheVersionFromTheHostOrThePayload()
    {
        var js = DashboardBuilder.ReadAsset("app.js");
        Assert.Contains("window.AwrReport.versionText(APP_VERSION, A && A.meta && A.meta.analyzerVersion)", js, StringComparison.Ordinal); // About box
        Assert.Contains("version: APP_VERSION", js, StringComparison.Ordinal); // print: the report adds meta.analyzerVersion itself
        Assert.DoesNotContain("$('aboutVersionNo').textContent", js, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDashboardListsTheReferencedDocumentationAndReachesItFromTheDocumentationTab()
    {
        var js = DashboardBuilder.ReadAsset("app.js");
        var summary = js[js.IndexOf("function buildSummary()", StringComparison.Ordinal)..js.IndexOf("function buildReferences(", StringComparison.Ordinal)];
        Assert.Contains("buildReferences(p);", summary, StringComparison.Ordinal);
        Assert.Contains("window.AwrReport.references(A)", js, StringComparison.Ordinal); // same data and numbering as print
        Assert.Contains("id: 'ref-' + r.n", js, StringComparison.Ordinal);
        Assert.Contains("gotoReference(r.n)", js, StringComparison.Ordinal);            // finding card number
        Assert.Contains("id: 'toRefs'", js, StringComparison.Ordinal);                  // Documentation tab
        Assert.Contains("gotoFinding(f.id)", js, StringComparison.Ordinal);             // "Cited by"
        Assert.Contains(@"/^https:\/\//i.test(u || '')", js, StringComparison.Ordinal); // https links only
        Assert.Contains(Heading, DashboardBuilder.ReadAsset("report.js"), StringComparison.Ordinal);
    }

    // ---- Node harness ----

    private sealed record Ref(int N, string Url, string? Href, string Title, string Book, List<string> Findings);
    private sealed record Node(string Tag, string? Id, string? Href, string? Target, string? Rel);
    private sealed record Rendered(List<Ref> Refs, List<Node> Nodes, string Text);

    /// <summary>Just enough DOM for report.js (no charts): elements, attributes and text.</summary>
    private const string Harness = """
        const fs = require('fs');
        const [, , reportJs, input, output] = process.argv;
        const el = (tag) => ({ nodeType: 1, tag, attrs: {}, kids: [], className: '',
          appendChild(c) { this.kids.push(c); return c; }, setAttribute(k, v) { this.attrs[k] = String(v); },
          set textContent(v) { this.kids = [{ nodeType: 3, text: String(v) }]; } });
        global.window = {};
        global.document = { createElement: el, createElementNS: (ns, t) => el(t), createTextNode: (t) => ({ nodeType: 3, text: String(t) }) };
        eval(fs.readFileSync(reportJs, 'utf8'));
        const data = JSON.parse(fs.readFileSync(input, 'utf8')), A = data.analysis;
        const refs = window.AwrReport.references(A).list.map((r) => ({ n: r.n, url: r.url, href: r.href, title: r.title, book: r.book, findings: r.findings.map((f) => f.id) }));
        const root = window.AwrReport.build(A, { version: data.version, now: new Date() });
        const nodes = [], text = [];
        (function walk(n) {
          if (n.nodeType === 3) { text.push(n.text); return; }
          nodes.push({ tag: n.tag, id: n.attrs.id || null, href: n.attrs.href || null, target: n.attrs.target || null, rel: n.attrs.rel || null });
          n.kids.forEach(walk);
        })(root);
        fs.writeFileSync(output, JSON.stringify({ refs, nodes, text: '\n' + text.join('\n') + '\n' }));
        """;

    /// <summary>Null when Node.js is not installed.</summary>
    private static Rendered? Render(AnalysisResult analysis, string appVersion)
    {
        var node = UiScriptSyntaxTests.FindNode();
        if (node == null) return null;
        var dir = Directory.CreateTempSubdirectory("awr-rpt-");
        try
        {
            string At(string name) => Path.Combine(dir.FullName, name);
            File.WriteAllText(At("harness.js"), Harness);
            File.WriteAllText(At("report.js"), DashboardBuilder.ReadAsset("report.js"));
            var payload = JsonDocument.Parse(DashboardBuilder.ToJson(analysis, null, DashboardMode.Export)).RootElement.GetProperty("analysis").GetRawText();
            File.WriteAllText(At("in.json"), "{\"version\":" + JsonSerializer.Serialize(appVersion) + ",\"analysis\":" + payload + "}");
            var psi = new ProcessStartInfo(node) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { At("harness.js"), At("report.js"), At("in.json"), At("out.json") }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(30_000), "node timed out");
            Assert.True(p.ExitCode == 0, "report.js failed:\n" + err);
            return JsonSerializer.Deserialize<Rendered>(File.ReadAllText(At("out.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        finally { try { dir.Delete(true); } catch (IOException) { /* temp folder */ } }
    }
}
