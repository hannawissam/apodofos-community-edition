// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Core.Export;

namespace Apodofos.Tests;

/// <summary>
/// The printable report must be part of every dashboard and must be the only thing printed, whichever tab is open (the old print
/// mode printed just the visible tab, so for example the KPI tiles were missing when Findings was open). The behaviour itself is
/// verified in a real browser by tools/ui-qa/print.mjs; these tests pin the wiring.
/// </summary>
public class PrintReportTests
{
    private static string Page => DashboardBuilder.Build(null, null, DashboardMode.Desktop);

    [Fact]
    public void EveryDashboardCarriesTheReportBuilderItsStylesAndTheContainer()
    {
        var page = Page;
        Assert.Contains("window.AwrReport = {", page, StringComparison.Ordinal);
        Assert.Contains("id=\"printreport\"", page, StringComparison.Ordinal);
        Assert.Contains("#printreport", page, StringComparison.Ordinal);
        Assert.Contains("body > *:not(#printreport)", page, StringComparison.Ordinal); // print shows only the report
    }

    [Fact]
    public void PrintAndSavePdfButtonsBuildTheReportFirst()
    {
        var js = DashboardBuilder.ReadAsset("app.js");
        Assert.Matches(@"btnPrint'\)\.addEventListener\('click', function \(\) \{\s*if \(!buildReport\(\)\)", js);
        Assert.Matches(@"btnPdf'\)\.addEventListener\('click', function \(\) \{\s*if \(!buildReport\(\)\)", js);
        Assert.Contains("host({ type: 'exportPdf' })", js, StringComparison.Ordinal);
        Assert.Contains("addEventListener('beforeprint'", js, StringComparison.Ordinal); // Ctrl+P in a browser prints the same report
    }

    [Fact]
    public void TheReportPrintsTheProductNameAndTheTradeMarkNotice()
    {
        var js = DashboardBuilder.ReadAsset("report.js");
        Assert.Contains("A.meta.product", js, StringComparison.Ordinal); // each edition prints its own name
        Assert.Contains("\\u2122 is a trademark of Wissam HANNA (French trade mark application no. 5322937, not yet registered)", js, StringComparison.Ordinal);
        Assert.Contains("rpt-tm", js, StringComparison.Ordinal);        // on the cover
        Assert.Contains("@bottom-left", js, StringComparison.Ordinal);  // the running page footer carries the product name
    }

    [Fact]
    public void SavePdfIsADesktopButton()
    {
        var html = DashboardBuilder.ReadAsset("index.html");
        Assert.Matches("id=\"btnPdf\" data-host=\"desktop\"", html);
    }

    [Fact]
    public void TheReportBuilderNeverParsesReportTextAsHtml()
    {
        // report text is untrusted: the builder must only use textContent / text nodes
        var js = DashboardBuilder.ReadAsset("report.js");
        Assert.DoesNotContain("innerHTML", js, StringComparison.Ordinal);
        Assert.DoesNotContain("insertAdjacentHTML", js, StringComparison.Ordinal);
        Assert.DoesNotContain("document.write", js, StringComparison.Ordinal);
        Assert.DoesNotContain("eval(", js, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportMakesNoExternalRequests()
    {
        var css = DashboardBuilder.ReadAsset("report.css");
        var js = DashboardBuilder.ReadAsset("report.js");
        foreach (var asset in new[] { css, js })
        {
            Assert.DoesNotContain("@import", asset, StringComparison.Ordinal);
            Assert.DoesNotContain("url(http", asset, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fetch(", asset, StringComparison.Ordinal);
            Assert.DoesNotContain("XMLHttpRequest", asset, StringComparison.Ordinal);
        }
    }
}
