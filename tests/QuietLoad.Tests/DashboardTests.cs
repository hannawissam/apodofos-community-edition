// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text.RegularExpressions;
using QuietLoad.Core.Analysis;
using QuietLoad.Core.Export;

namespace QuietLoad.Tests;

public class DashboardTests
{
    private static string Build(DashboardMode mode, bool withData)
    {
        if (!withData) return DashboardBuilder.Build(null, null, mode);
        var r = Samples.Load(Samples.Syn11);
        return DashboardBuilder.Build(AnalysisEngine.Analyze(r), r.SanitizedHtml, mode);
    }

    [Fact]
    public void JsonPayloadIsCamelCaseAndScriptSafe()
    {
        var r = Samples.Load(Samples.Syn11);
        var json = DashboardBuilder.ToJson(AnalysisEngine.Analyze(r), r.SanitizedHtml, DashboardMode.Desktop);
        Assert.Contains("\"mode\":\"desktop\"", json);
        Assert.Contains("\"analysis\"", json);
        Assert.DoesNotContain("</script", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DashboardMode.Desktop, false)]
    [InlineData(DashboardMode.Export, true)]
    public void NoExternalResourceLoads(DashboardMode mode, bool withData)
    {
        if (!Samples.WebUiPresent) return; // Web UI not embedded yet
        var html = Build(mode, withData);
        // Attribute-based loads (src/href/action/srcset) and CSS url()/@import must never point at the network.
        var attr = Regex.Matches(html, @"\b(?:src|href|action|srcset|poster|data)\s*=\s*[""']\s*(?:https?:)?//", RegexOptions.IgnoreCase);
        // Exclude the sanitised report: it is embedded in JSON, so any markup there is escaped (") and cannot match.
        Assert.Empty(attr.Select(m => m.Value));
        Assert.DoesNotMatch(@"url\(\s*[""']?\s*(?:https?:)?//", html);
        Assert.DoesNotMatch(@"@import\s+(?:url\()?\s*[""']?\s*(?:https?:)?//", html);
        Assert.DoesNotMatch(@"<link[^>]+rel\s*=\s*[""']?(?:stylesheet|preload)[^>]+https?://", html);
        Assert.DoesNotMatch(@"<script[^>]+src\s*=", html);
        Assert.DoesNotMatch(@"\bfetch\(\s*[""']https?://|XMLHttpRequest|new WebSocket\(", html);
    }

    [Fact]
    public void SummaryShowsKeyNumbersBeforeFixFirst()
    {
        if (!Samples.WebUiPresent) return;
        var js = DashboardBuilder.ReadAsset("app.js");
        var summary = js[js.IndexOf("function buildSummary()", StringComparison.Ordinal)..];
        var hero = summary.IndexOf("class: 'hero'", StringComparison.Ordinal);
        var dq = summary.IndexOf("dqbanner", StringComparison.Ordinal);
        var key = summary.IndexOf("text: 'Key numbers'", StringComparison.Ordinal);
        var fix = summary.IndexOf("text: 'Fix first'", StringComparison.Ordinal);
        Assert.True(hero > 0 && dq > hero && key > dq && fix > key, "Summary order must be hero, data quality, Key numbers, Fix first");
    }

    [Fact]
    public void ExportContainsAllSections()
    {
        if (!Samples.WebUiPresent) return; // Web UI not embedded yet
        var html = Build(DashboardMode.Export, true);
        Assert.Contains("window.__AWR__", html);
        Assert.DoesNotContain("<!--AWR:", html);
        Assert.Contains("\"mode\":\"export\"", html);
        Assert.Contains("id=\"tab-docs\"", html);
        Assert.Contains("id=\"aboutDlg\"", html);
        Assert.Contains("Fix first", html);
        Assert.Contains("dqbanner", html);
        Assert.Contains("relatedIds", html);
        Assert.Contains("https://github.com/hannawissam/quietload-community-edition", html);
        Assert.DoesNotContain("[SOURCE_URL]", html);
        Assert.Contains("over a network you must offer them the corresponding source code", html);
        Assert.Contains("Relevant to this report", html); // Documentation tab: relevant group first
        Assert.Contains("Performance Tuning Guide", html); // book priority list
        Assert.Contains("Database Reference", html);
    }
}
