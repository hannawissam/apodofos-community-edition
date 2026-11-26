// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Desktop;

namespace Apodofos.Tests;

/// <summary>
/// The desktop shell starts the WebView2 runtime so that it has nothing to fetch. These tests pin the configuration; that it
/// really keeps the runtime silent is measured with a network log on Windows, which a unit test cannot do.
/// </summary>
public class WebView2NetworkTests
{
    [Theory]
    [InlineData("--disable-background-networking")]
    [InlineData("--disable-component-update")]
    [InlineData("--disable-features=msOneAuthWAM,msForceOneAuthWAM")]
    [InlineData("--no-proxy-server")]
    [InlineData("--host-resolver-rules=\"MAP * ~NOTFOUND\"")]
    public void TheRuntimeIsStartedWithTheSwitch(string expected)
    {
        Assert.Contains(expected, WebView2Network.Switches);
        Assert.Contains(expected, WebView2Network.BrowserArguments, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--proxy-server")]
    [InlineData("--proxy-pac-url")]
    [InlineData("--remote-debugging")]
    [InlineData("--log-net-log")]
    [InlineData("--no-sandbox")]
    [InlineData("--disable-web-security")]
    [InlineData("--ignore-certificate-errors")]
    public void NothingThatOpensTheRuntimeUpIsPassed(string forbidden) =>
        Assert.DoesNotContain(forbidden, WebView2Network.BrowserArguments, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void FeaturesAreDisabledInOneSwitchOnly() => // a second --disable-features would replace the first one
        Assert.Single(WebView2Network.Switches, s => s.StartsWith("--disable-features=", StringComparison.Ordinal));

    [Fact]
    public void TheDesktopShellAppliesThemAndKeepsCrashAndReputationDataLocal()
    {
        var shell = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Apodofos.Desktop", "MainForm.cs"));
        Assert.Contains("AdditionalBrowserArguments = WebView2Network.BrowserArguments", shell, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2Environment.CreateAsync(null, _dataDir, options)", shell, StringComparison.Ordinal);
        Assert.Contains("IsCustomCrashReportingEnabled = true", shell, StringComparison.Ordinal);
        Assert.Contains("IsReputationCheckingRequired = false", shell, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Apodofos.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
