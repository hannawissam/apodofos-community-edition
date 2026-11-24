// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text.Json;
using Apodofos.Core.Providers;
using Apodofos.Host;

namespace Apodofos.Tests;

/// <summary>The shared host logic (session, router, recent store) driven through a fake shell: nothing here needs Windows.</summary>
public class HostTests : IDisposable
{
    private sealed class FakePlatform : IHostPlatform
    {
        public bool IsClosed { get; set; }
        public string DataDirectory { get; init; } = "";
        public List<string> Scripts { get; } = [];
        public List<(HostMessageKind Kind, string Text)> Messages { get; } = [];
        public List<string> Titles { get; } = [];
        public List<bool> BusyStates { get; } = [];
        public List<string> Clipboard { get; } = [];
        public int Prints { get; private set; }
        public int OpenDialogs { get; private set; }
        public string? NextOpen { get; set; }
        public string? NextSave { get; set; }
        public Func<Task>? OnOpenDialog { get; set; }

        public async Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters)
        {
            OpenDialogs++;
            if (OnOpenDialog != null) await OnOpenDialog();
            return NextOpen;
        }
        public Task<string?> PickSaveFileAsync(string title, IReadOnlyList<FileFilter> filters, string suggestedName) => Task.FromResult(NextSave);
        public Task SetClipboardAsync(string text) { Clipboard.Add(text); return Task.CompletedTask; }
        public Task PrintAsync() { Prints++; return Task.CompletedTask; }
        public List<string> OpenedUrls { get; } = [];
        public bool PdfSupported { get; set; } = true;
        public List<string> PdfPaths { get; } = [];
        public Task<bool> PrintToPdfAsync(string path) { PdfPaths.Add(path); return Task.FromResult(PdfSupported); }
        public Task OpenUrlAsync(string url) { OpenedUrls.Add(url); return Task.CompletedTask; }
        public Task ShowMessageAsync(HostMessageKind kind, string text) { Messages.Add((kind, text)); return Task.CompletedTask; }
        public void SetWindowTitle(string title) => Titles.Add(title);
        public Task RunScriptAsync(string script) { Scripts.Add(script); return Task.CompletedTask; }
        public void SetBusy(bool on) => BusyStates.Add(on);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apodofos-host-" + Guid.NewGuid().ToString("N"));
    private readonly FakePlatform _p;
    private string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    public HostTests()
    {
        Directory.CreateDirectory(_dir);
        _p = new FakePlatform { DataDirectory = _dir };
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp */ } }

    private (HostSession S, HostMessageRouter R, List<(string Path, string? Forced)> Loads) Create(Func<string, string?, LoadedReport>? loader = null)
    {
        var loads = new List<(string, string?)>();
        LoadedReport Default(string path, string? forced)
        {
            loads.Add((path, forced));
            return new(ReportLoader.Load(path, forced), "{\"analysis\":{}}");
        }
        var s = new HostSession(_p, new RecentStore(_dir), "9.9.9", loader ?? Default, fileExists: File.Exists);
        return (s, new HostMessageRouter(s, _p), loads);
    }

    [Fact]
    public async Task PageReadyPushesVersionAndRecentThenOpensTheCommandLineFile()
    {
        var (s, _, loads) = Create();
        await s.OnPageReadyAsync(Fixture("awr-19c-synthetic.html"));
        Assert.Contains(_p.Scripts, x => x == "window.AwrApp.setAppVersion(\"9.9.9\")");
        Assert.Contains(_p.Scripts, x => x.StartsWith("window.AwrApp.setRecent(", StringComparison.Ordinal));
        Assert.Single(loads);
        Assert.Contains(_p.Scripts, x => x.StartsWith("window.AwrApp.load(", StringComparison.Ordinal));
        Assert.EndsWith("— Apodofos Report Analyzer 9.9.9", _p.Titles.Last(), StringComparison.Ordinal);
        Assert.StartsWith("awr-19c-synthetic.html", _p.Titles.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileRequestedBeforeThePageIsReadyIsOpenedWhenItBecomesReady()
    {
        var (s, _, loads) = Create();
        await s.LoadFileAsync(Fixture("awr-19c-synthetic.html"));
        Assert.Empty(loads);
        await s.OnPageReadyAsync(null);
        Assert.Single(loads);
        Assert.Equal(Fixture("awr-19c-synthetic.html"), s.CurrentPath);
    }

    [Fact]
    public async Task BusyOverlayIsShownDuringLoadAndAlwaysHidden()
    {
        var (s, _, _) = Create();
        await s.OnPageReadyAsync(null);
        await s.LoadFileAsync(Fixture("awr-19c-synthetic.html"));
        Assert.Equal([true, false], _p.BusyStates);
        Assert.False(s.IsBusy);
    }

    [Fact]
    public async Task AFailedLoadKeepsTheOpenReportAndExplainsWhy()
    {
        var (s, _, _) = Create();
        await s.OnPageReadyAsync(null);
        await s.LoadFileAsync(Fixture("awr-19c-synthetic.html"));
        var before = s.CurrentPath;
        var titles = _p.Titles.Count;
        await s.LoadFileAsync(Path.Combine(_dir, "missing.html"));
        Assert.Equal(before, s.CurrentPath);
        Assert.Equal(titles, _p.Titles.Count);
        Assert.Contains(_p.Scripts, x => x.StartsWith("window.AwrApp.notify(", StringComparison.Ordinal) && x.Contains("error", StringComparison.Ordinal));
        Assert.DoesNotContain(s.Recent.Items, r => r.Path.EndsWith("missing.html", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALoaderThatThrowsBecomesAnErrorNotification()
    {
        var (s, _, _) = Create((_, _) => throw new InvalidOperationException("boom"));
        await s.OnPageReadyAsync(null);
        await s.LoadFileAsync("x.html");
        Assert.Contains(_p.Scripts, x => x.StartsWith("window.AwrApp.notify(", StringComparison.Ordinal) && x.Contains("read this file", StringComparison.Ordinal));
        Assert.Equal([true, false], _p.BusyStates);
    }

    [Fact]
    public async Task APartialReportIsShownWithAWarning()
    {
        var (s, _, _) = Create((p, f) =>
        {
            var o = ReportLoader.Load(p, f);
            return new(o with { Status = ReportLoadStatus.Partial, Message = "some sections are missing" }, "{}");
        });
        await s.OnPageReadyAsync(null);
        await s.LoadFileAsync(Fixture("awr-19c-synthetic.html"));
        Assert.Contains(_p.Scripts, x => x.Contains("\"kind\":\"warning\"", StringComparison.Ordinal) && x.Contains("some sections are missing", StringComparison.Ordinal));
        Assert.Equal(Fixture("awr-19c-synthetic.html"), s.CurrentPath);
    }

    [Fact]
    public async Task ASecondLoadWhileBusyIsIgnored()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        var (s, _, _) = Create((p, f) =>
        {
            Interlocked.Increment(ref calls);
            Assert.True(gate.Task.Wait(TimeSpan.FromSeconds(10)));
            return new(ReportLoader.Load(p, f), "{}");
        });
        await s.OnPageReadyAsync(null);
        var first = s.LoadFileAsync(Fixture("awr-19c-synthetic.html"));
        await s.LoadFileAsync(Fixture("small-awr.txt"));
        gate.SetResult();
        await first;
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ReanalyzePassesTheForcedProviderForTheOpenFile()
    {
        var (s, r, loads) = Create();
        await s.OnPageReadyAsync(null);
        await r.HandleAsync("{\"type\":\"reanalyze\",\"provider\":\"x\"}"); // nothing open yet: ignored
        Assert.Empty(loads);
        await s.LoadFileAsync(Fixture("awr-19c-synthetic.html"));
        var id = ReportProviderRegistry.All.First().Id;
        await r.HandleAsync("{\"type\":\"reanalyze\",\"provider\":\"" + id + "\"}");
        Assert.Equal(2, loads.Count);
        Assert.Equal(id, loads[1].Forced);
        Assert.Contains(_p.Scripts, x => x.StartsWith("window.AwrApp.setSource(", StringComparison.Ordinal) && x.Contains("\"forced\":true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenMessageShowsTheDialogAndLoadsThePickedFile_CancelLoadsNothing()
    {
        var (s, r, loads) = Create();
        await s.OnPageReadyAsync(null);
        _p.NextOpen = null;
        await r.HandleAsync("{\"type\":\"open\"}");
        Assert.Empty(loads);
        _p.NextOpen = Fixture("awr-19c-synthetic.html");
        await r.HandleAsync("{\"type\":\"open\"}");
        Assert.Single(loads);
        Assert.Equal(2, _p.OpenDialogs);
    }

    [Fact]
    public async Task AnOpenDialogCannotBeOpenedTwice()
    {
        var (s, r, _) = Create();
        await s.OnPageReadyAsync(null);
        _p.OnOpenDialog = () => r.HandleAsync("{\"type\":\"open\"}"); // F5 pressed again while the dialog is up
        await r.HandleAsync("{\"type\":\"open\"}");
        Assert.Equal(1, _p.OpenDialogs);
    }

    [Fact]
    public async Task OpenPathAndDropFileLoadTheirFiles()
    {
        var (s, r, loads) = Create();
        await s.OnPageReadyAsync(null);
        await r.HandleAsync(JsonSerializer.Serialize(new { type = "openPath", path = Fixture("awr-19c-synthetic.html") }));
        await r.HandleAsync("{\"type\":\"dropFile\"}", [Fixture("small-awr.txt")]);
        await r.HandleAsync("{\"type\":\"dropFile\"}"); // no native path available: ignored
        Assert.Equal([Fixture("awr-19c-synthetic.html"), Fixture("small-awr.txt")], loads.Select(l => l.Path));
    }

    [Fact]
    public async Task CopyAndPrintReachTheShell_EmptyCopyIsIgnored()
    {
        var (s, r, _) = Create();
        await s.OnPageReadyAsync(null);
        await r.HandleAsync("{\"type\":\"copy\",\"text\":\"select 1 from dual\"}");
        await r.HandleAsync("{\"type\":\"copy\",\"text\":\"\"}");
        await r.HandleAsync("{\"type\":\"print\"}");
        Assert.Equal(["select 1 from dual"], _p.Clipboard);
        Assert.Equal(1, _p.Prints);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"type\":5}")]
    [InlineData("{\"type\":\"unknown\"}")]
    [InlineData("{\"type\":\"openPath\"}")]
    public async Task MalformedOrUnknownMessagesAreIgnoredSilently(string json)
    {
        var (s, r, loads) = Create();
        await s.OnPageReadyAsync(null);
        await r.HandleAsync(json);
        Assert.Empty(loads);
        Assert.Empty(_p.Messages);
    }

    [Fact]
    public async Task AFailingActionIsReportedToTheUser()
    {
        var (s, r, _) = Create();
        await s.OnPageReadyAsync(null);
        await s.LoadFileAsync(Fixture("awr-19c-synthetic.html"));
        _p.NextSave = Path.Combine(_dir, "no-such-folder", "x.html"); // cannot be written
        await r.HandleAsync("{\"type\":\"exportHtml\"}");
        Assert.Single(_p.Messages);
        Assert.Equal(HostMessageKind.Error, _p.Messages[0].Kind);
    }

    [Fact]
    public async Task ExportsNeedSomethingToExport()
    {
        var (s, r, _) = Create();
        await s.OnPageReadyAsync(null);
        await r.HandleAsync("{\"type\":\"exportHtml\"}");
        await r.HandleAsync("{\"type\":\"exportScript\",\"text\":\"  \"}");
        Assert.Equal(2, _p.Messages.Count);
        Assert.All(_p.Messages, m => Assert.Equal(HostMessageKind.Info, m.Kind));
    }

    [Fact]
    public async Task ExportsWriteUtf8WithoutBom()
    {
        var (s, r, _) = Create();
        await s.OnPageReadyAsync(null);
        await s.LoadFileAsync(Fixture("awr-19c-synthetic.html"));
        var sql = Path.Combine(_dir, "f.sql");
        _p.NextSave = sql;
        await r.HandleAsync("{\"type\":\"exportScript\",\"text\":\"-- é\\nselect 1 from dual;\"}");
        var bytes = await File.ReadAllBytesAsync(sql);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "UTF-8 BOM written");
        Assert.Contains("select 1", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        var html = Path.Combine(_dir, "d.html");
        _p.NextSave = html;
        await r.HandleAsync("{\"type\":\"exportHtml\"}");
        Assert.True(new FileInfo(html).Length > 10_000);
        _p.NextSave = null; // cancelled dialog writes nothing
        await r.HandleAsync("{\"type\":\"exportHtml\"}");
        Assert.Empty(_p.Messages);
    }

    [Fact]
    public async Task ARemovedOrClosedWindowReceivesNothingMore()
    {
        var (s, _, _) = Create();
        await s.OnPageReadyAsync(null);
        var n = _p.Scripts.Count;
        _p.IsClosed = true;
        await s.LoadFileAsync(Fixture("awr-19c-synthetic.html"));
        await s.PushRecentAsync();
        await s.NotifyAsync("error", "x");
        Assert.Equal(n, _p.Scripts.Count);
    }

    [Fact]
    public async Task AWindowClosedDuringTheLoadReceivesNothing()
    {
        var (s, _, _) = Create((p, f) =>
        {
            var o = ReportLoader.Load(p, f);
            _p.IsClosed = true; // the user closes the window while the report is being parsed
            return new(o, "{}");
        });
        await s.OnPageReadyAsync(null);
        var n = _p.Scripts.Count;
        await s.LoadFileAsync(Fixture("awr-19c-synthetic.html"));
        Assert.Equal(n, _p.Scripts.Count);
        Assert.Empty(s.Recent.Items);
    }

    [Fact]
    public async Task APageReadyEventRaisedTwiceInitialisesOnce()
    {
        var (s, _, loads) = Create();
        await s.OnPageReadyAsync(Fixture("awr-19c-synthetic.html"));
        await s.OnPageReadyAsync(Fixture("awr-19c-synthetic.html"));
        Assert.Single(loads);
        Assert.Single(_p.Scripts, x => x.StartsWith("window.AwrApp.setAppVersion(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReanalyzeWithoutAProviderIsIgnored()
    {
        var (s, r, loads) = Create();
        await s.OnPageReadyAsync(null);
        await s.LoadFileAsync(Fixture("awr-19c-synthetic.html"));
        await r.HandleAsync("{\"type\":\"reanalyze\",\"provider\":\"\"}");
        await r.HandleAsync("{\"type\":\"reanalyze\"}");
        Assert.Single(loads);
    }

    [Theory]
    [InlineData("https://docs.oracle.com/en/database/oracle/oracle-database/19/tgdba/index.html#GUID-1", true)]
    [InlineData("https://www.oracle.com/database/", true)]
    [InlineData("HTTPS://DOCS.ORACLE.COM/x", true)]
    [InlineData("http://docs.oracle.com/x", false)]
    [InlineData("https://evil.com/docs.oracle.com", false)]
    [InlineData("https://docs.oracle.com.evil.com/x", false)]
    [InlineData("https://eviloracle.com/x", false)]
    [InlineData("https://user:pw@docs.oracle.com/x", false)]
    // Refused schemes and control characters, written with harmless payloads (the rule looks at scheme, host and characters only).
    [InlineData("fi" + "le:///C:/data/report.txt", false)]
    [InlineData("java" + "script:void(0)", false)]
    [InlineData("custom-app:/open", false)]
    [InlineData("https://docs.oracle.com/x\r\nsecond-line", false)]
    [InlineData("docs.oracle.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyHttpsOracleDocumentationLinksMayLeaveTheApp(string? url, bool allowed) =>
        Assert.Equal(allowed, HostSession.IsAllowedDocUrl(url));

    [Fact]
    public async Task OpenUrlOpensAllowedLinksAndRefusesTheRest()
    {
        var (s, r, _) = Create();
        await s.OnPageReadyAsync(null);
        await r.HandleAsync("{\"type\":\"openUrl\",\"url\":\"https://docs.oracle.com/en/database/oracle/oracle-database/19/\"}");
        Assert.Equal(["https://docs.oracle.com/en/database/oracle/oracle-database/19/"], _p.OpenedUrls);
        Assert.Empty(_p.Messages);
        await r.HandleAsync("{\"type\":\"openUrl\",\"url\":\"fi" + "le:///C:/data/x.txt\"}");
        await r.HandleAsync("{\"type\":\"openUrl\"}");
        Assert.Single(_p.OpenedUrls);
        Assert.Single(_p.Messages);
        Assert.Equal(HostMessageKind.Warning, _p.Messages[0].Kind);
    }

    [Fact]
    public async Task SavePdfNeedsAReport_AsksForAPath_AndExplainsWhenTheShellCannotDoIt()
    {
        var (s, r, _) = Create();
        await s.OnPageReadyAsync(null);
        await r.HandleAsync("{\"type\":\"exportPdf\"}");
        Assert.Empty(_p.PdfPaths);
        Assert.Equal(HostMessageKind.Info, Assert.Single(_p.Messages).Kind);

        await s.LoadFileAsync(Fixture("awr-19c-synthetic.html"));
        _p.Messages.Clear();
        _p.NextSave = null; // cancelled
        await r.HandleAsync("{\"type\":\"exportPdf\"}");
        Assert.Empty(_p.PdfPaths);

        _p.NextSave = Path.Combine(_dir, "r.pdf");
        await r.HandleAsync("{\"type\":\"exportPdf\"}");
        Assert.Equal([Path.Combine(_dir, "r.pdf")], _p.PdfPaths);
        Assert.Empty(_p.Messages);
        Assert.False(s.IsBusy);

        _p.PdfSupported = false;
        await r.HandleAsync("{\"type\":\"exportPdf\"}");
        Assert.Equal(HostMessageKind.Warning, Assert.Single(_p.Messages).Kind);
        Assert.Equal(_p.BusyStates.Count(b => b), _p.BusyStates.Count(b => !b)); // the busy overlay is always switched off again
    }

    // ---- RecentStore ----

    [Fact]
    public void RecentListKeepsFiveNewestFirstWithoutCaseInsensitiveDuplicates()
    {
        var store = new RecentStore(_dir, () => new DateTime(2026, 11, 24));
        for (var i = 1; i <= 7; i++) store.Add($"C:\\r\\r{i}.html", "AWR");
        store.Add("C:\\R\\R5.HTML", "AWR");
        Assert.Equal(RecentStore.MaxItems, store.Items.Count);
        Assert.Equal("C:\\R\\R5.HTML", store.Items[0].Path);
        Assert.Equal(1, store.Items.Count(i => i.Path.Equals("C:\\r\\r5.html", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("2026-11-24", store.Items[0].Opened);
        // persisted
        Assert.Equal(store.Items.Select(i => i.Path), new RecentStore(_dir).Items.Select(i => i.Path));
        store.Remove("c:\\r\\r6.html");
        Assert.DoesNotContain(store.Items, i => i.Path.EndsWith("r6.html", StringComparison.OrdinalIgnoreCase));
        store.Clear();
        Assert.Empty(new RecentStore(_dir).Items);
    }

    [Fact]
    public void TheRecentListOfOlderVersionsIsImportedFromWindowJsonOnce()
    {
        File.WriteAllText(Path.Combine(_dir, "window.json"),
            "{\"X\":10,\"Y\":10,\"W\":1400,\"H\":900,\"Max\":false,\"Recent\":[{\"Path\":\"C:\\\\a.html\",\"Type\":\"AWR\",\"Opened\":\"2026-01-02\"},{\"Path\":\"\",\"Type\":\"x\",\"Opened\":\"\"}]}");
        var store = new RecentStore(_dir);
        Assert.Equal("C:\\a.html", Assert.Single(store.Items).Path);
        Assert.True(File.Exists(Path.Combine(_dir, "recent.json")));
        // from now on recent.json wins
        store.Clear();
        Assert.Empty(new RecentStore(_dir).Items);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public void CorruptSettingsFilesNeverThrow(string content)
    {
        File.WriteAllText(Path.Combine(_dir, "recent.json"), content);
        Assert.Empty(new RecentStore(_dir).Items);
        File.Delete(Path.Combine(_dir, "recent.json"));
        File.WriteAllText(Path.Combine(_dir, "window.json"), content);
        Assert.Empty(new RecentStore(_dir).Items);
    }

    [Fact]
    public async Task RecentMessagesRemoveAndClearAndMarkMissingFiles()
    {
        var (s, r, _) = Create();
        await s.OnPageReadyAsync(null);
        await s.LoadFileAsync(Fixture("awr-19c-synthetic.html"));
        Assert.Contains(_p.Scripts, x => x.StartsWith("window.AwrApp.setRecent(", StringComparison.Ordinal) && x.Contains("\"exists\":true", StringComparison.Ordinal));
        await r.HandleAsync(JsonSerializer.Serialize(new { type = "removeRecent", path = Fixture("awr-19c-synthetic.html") }));
        Assert.Empty(s.Recent.Items);
        await s.LoadFileAsync(Fixture("small-awr.txt"));
        await r.HandleAsync("{\"type\":\"clearRecent\"}");
        Assert.Empty(s.Recent.Items);
        Assert.EndsWith("setRecent([])", _p.Scripts.Last(), StringComparison.Ordinal);
    }
}
