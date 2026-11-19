// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Core.Export;
using Apodofos.Core.Providers;

namespace Apodofos.Tests;

public class ReportLoaderTests
{
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    [Theory]
    [InlineData("awr-19c-layout.txt", "AWR", "oracle-awr")]
    [InlineData("ash-19c-hot-event.txt", "ASH", "oracle-ash")]
    [InlineData("statspack-19c-layout.txt", "Statspack", "oracle-statspack")]
    public void DetectsEachReportTypeFromContent(string file, string type, string id)
    {
        var o = ReportLoader.Load(Data(file));
        Assert.NotNull(o.Result);
        Assert.Equal(type, o.Result!.Analysis.Meta.ReportType);
        Assert.Equal(id, o.ProviderId);
    }

    [Fact]
    public void ForcedProviderOverridesDetection()
    {
        var path = Data("ash-19c-hot-event.txt");
        Assert.Equal("ASH", ReportProviderRegistry.AnalyzeFile(path).Analysis.Meta.ReportType);
        Assert.Equal("ASH", ReportProviderRegistry.AnalyzeFile(path, "oracle-ash").Analysis.Meta.ReportType);
        Assert.Equal("oracle-ash", ReportLoader.Load(path, "oracle-ash").ProviderId);
        // a provider that cannot read the file gives the friendly "unsupported" outcome, never a raw exception
        Assert.Equal(ReportLoadStatus.Unsupported, ReportLoader.Load(path, "oracle-awr").Status);
        Assert.Equal("AWR", ReportLoader.Load(Data("awr-19c-layout.txt"), "oracle-awr").Result!.Analysis.Meta.ReportType);
    }

    [Fact]
    public void ForcedProviderIdIsCaseInsensitiveAndUnknownIdIsRejected()
    {
        var content = File.ReadAllText(Data("statspack-19c-layout.txt"));
        Assert.Equal("Statspack", ReportProviderRegistry.AnalyzeContent(content, "s.txt", "ORACLE-STATSPACK").Analysis.Meta.ReportType);
        Assert.Throws<ArgumentException>(() => ReportProviderRegistry.AnalyzeContent(content, "s.txt", "nope"));
        Assert.Null(ReportProviderRegistry.ById("nope"));
    }

    [Fact]
    public void UnsupportedFileGivesTheAgreedMessage()
    {
        var f = Path.Combine(Path.GetTempPath(), "ql-unsupported-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(f, "hello, this is not an Oracle report");
        try
        {
            var o = ReportLoader.Load(f);
            Assert.Equal(ReportLoadStatus.Unsupported, o.Status);
            Assert.Equal("This file isn't a supported Oracle report. Apodofos can read AWR, ASH and Statspack reports saved as HTML or text. Check that you exported the full report, then try again.", o.Message);
            Assert.Null(o.Result);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void MissingFileIsUnreadable()
    {
        var o = ReportLoader.Load(Path.Combine(Path.GetTempPath(), "ql-does-not-exist.html"));
        Assert.Equal(ReportLoadStatus.Unreadable, o.Status);
        Assert.Equal("Couldn't read this file. It may be open in another program or too large.", o.Message);
    }

    [Fact]
    public void PartialMessageUsesTheRightArticle()
    {
        Assert.Equal("We recognized an AWR report, but some sections are missing. Showing what could be read.", ReportLoader.PartialMessage("AWR"));
        Assert.StartsWith("We recognized a Statspack report", ReportLoader.PartialMessage("Statspack"));
    }

    [Fact]
    public void FileOpenInAnotherProgramCanStillBeRead()
    {
        var f = Path.Combine(Path.GetTempPath(), "ql-shared-" + Guid.NewGuid().ToString("N") + ".txt");
        File.Copy(Data("awr-19c-layout.txt"), f);
        try
        {
            using var other = new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            Assert.NotNull(ReportLoader.Load(f).Result);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void UiHasSingleOpenButtonAndAgreedTexts()
    {
        if (!Samples.WebUiPresent) return;
        var html = DashboardBuilder.Build(null, null, DashboardMode.Desktop);
        Assert.Contains("title=\"Open an Oracle AWR, ASH or Statspack report (Ctrl+O)\"", html);
        Assert.Contains("aria-keyshortcuts=\"Control+O\"", html);
        Assert.Contains(">Open report</span>", html);
        Assert.Contains("Analyze an Oracle performance report", html);
        Assert.Contains("Drop an AWR, ASH or Statspack report here. HTML and text files both work. The file is read on this computer only and is never uploaded.", html);
        Assert.Contains("Drop to analyze this report", html);
        Assert.Contains("Recent reports", html);
        Assert.Contains("Clear list", html);
        Assert.Contains("Not right?", html);
        Assert.Contains("Treat this file as", html);
        Assert.Contains("data-provider=\"oracle-ash\"", html);
        // the label never changes with the report kind
        Assert.DoesNotContain("'Open ' + t + ' report", html);
        Assert.DoesNotContain("Original AWR report", html);
        Assert.DoesNotContain("Open an AWR report", html);
        foreach (var k in new[] { "AWR", "ASH", "Statspack" })
            Assert.DoesNotContain("Open " + k + " report", html);
        Assert.DoesNotContain("lblOpen').textContent", html);
    }
}
