// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text;
using System.Text.Json;
using Apodofos.Core.Analysis;
using Apodofos.Core.Export;
using Apodofos.Core.Providers;

namespace Apodofos.Host;

/// <summary>Result of loading one file: the outcome plus the dashboard payload JSON (null when nothing loaded).</summary>
public sealed record LoadedReport(ReportLoadOutcome Outcome, string? PayloadJson);

/// <summary>
/// The behaviour every shell shares: one load pipeline for the button, drag and drop, the command line, recent reports and
/// "Not right?" re-analysis; exports; the recent list; the window title. A shell supplies only <see cref="IHostPlatform"/>.
/// Not thread-safe by design: call it from the shell's UI thread (the heavy parsing runs on a worker thread inside).
/// </summary>
public sealed class HostSession
{
    public const string AppTitle = "Apodofos Report Analyzer";

    private static readonly FileFilter[] OpenFilters =
    [
        new("All supported reports", ["*.html", "*.htm", "*.txt", "*.lst", "*.log"]),
        new("AWR reports", ["*.html", "*.htm", "*.txt", "*.lst"]),
        new("ASH reports", ["*.html", "*.htm", "*.txt", "*.lst"]),
        new("Statspack reports", ["*.txt", "*.lst", "*.log"]),
        new("All files", ["*.*"])
    ];

    private readonly IHostPlatform _platform;
    private readonly string _appVersion;
    private readonly Func<string, string?, LoadedReport> _loader;
    private readonly Func<string, bool> _fileExists;
    private bool _ready;
    private bool _busy;
    private bool _dialogOpen;
    private string? _pendingFile;

    public HostSession(IHostPlatform platform, RecentStore recent, string appVersion,
        Func<string, string?, LoadedReport>? loader = null, Func<string, bool>? fileExists = null)
    {
        _platform = platform;
        Recent = recent;
        _appVersion = appVersion;
        _loader = loader ?? DefaultLoader;
        _fileExists = fileExists ?? SafeExists;
    }

    public RecentStore Recent { get; }
    public string? CurrentPath { get; private set; }
    public AnalysisResult? LastAnalysis { get; private set; }
    public string? LastReportHtml { get; private set; }
    public string? LastReportName { get; private set; }
    public bool IsReady => _ready;
    public bool IsBusy => _busy;

    public string DefaultTitle => AppTitle + " " + _appVersion;

    private static LoadedReport DefaultLoader(string path, string? forcedProviderId)
    {
        var o = ReportLoader.Load(path, forcedProviderId);
        return new(o, o.Result == null ? null : DashboardBuilder.ToJson(o.Result.Analysis, o.Result.ReportHtml, DashboardMode.Desktop));
    }

    private static bool SafeExists(string path) { try { return File.Exists(path); } catch { return false; } }

    /// <summary>The page finished loading: push the version and the recent list, then open the pending or command-line file.</summary>
    public async Task OnPageReadyAsync(string? initialFile)
    {
        if (_ready) return;
        _ready = true;
        var file = _pendingFile ?? initialFile;
        _pendingFile = null;
        await _platform.RunScriptAsync("window.AwrApp.setAppVersion(" + JsonSerializer.Serialize(_appVersion) + ")");
        await PushRecentAsync();
        if (!string.IsNullOrEmpty(file)) await LoadFileAsync(file);
    }

    public async Task OpenDialogAsync()
    {
        if (_busy || _dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var path = await _platform.PickOpenFileAsync("Open Oracle report", OpenFilters);
            if (!string.IsNullOrEmpty(path)) await LoadFileAsync(path);
        }
        finally { _dialogOpen = false; }
    }

    /// <summary>One path for the button, drag and drop, the command line, recent reports and "Not right?".</summary>
    public async Task LoadFileAsync(string path, string? forcedProviderId = null)
    {
        if (_busy || _platform.IsClosed) return;
        if (!_ready) { _pendingFile = path; return; }
        _busy = true;
        _platform.SetBusy(true);
        try
        {
            var loaded = await Task.Run(() => _loader(path, forcedProviderId));
            if (_platform.IsClosed) return;
            var outcome = loaded.Outcome;
            if (outcome.Result == null || loaded.PayloadJson == null)
            {
                // Keep whatever report is already open; just explain what went wrong.
                await NotifyAsync("error", outcome.Message);
                await PushRecentAsync();
                return;
            }
            await _platform.RunScriptAsync("window.AwrApp.load(" + loaded.PayloadJson + ")");
            await _platform.RunScriptAsync("window.AwrApp.setSource(" + JsonSerializer.Serialize(
                new { name = Path.GetFileName(path), provider = outcome.ProviderId, forced = forcedProviderId != null }) + ")");
            CurrentPath = path;
            LastAnalysis = outcome.Result.Analysis;
            LastReportHtml = outcome.Result.ReportHtml;
            LastReportName = Path.GetFileNameWithoutExtension(path);
            _platform.SetWindowTitle(Path.GetFileName(path) + " — " + DefaultTitle);
            Recent.Add(path, outcome.Result.Analysis.Meta.ReportType);
            await PushRecentAsync();
            if (outcome.Status == ReportLoadStatus.Partial) await NotifyAsync("warning", outcome.Message);
        }
        catch (Exception) when (_platform.IsClosed) { /* window closed while loading */ }
        catch (Exception)
        {
            await NotifyAsync("error", ReportLoader.UnreadableMessage);
        }
        finally
        {
            _busy = false;
            if (!_platform.IsClosed) _platform.SetBusy(false);
        }
    }

    /// <summary>"Not right?": re-reads the open file with another provider.</summary>
    public async Task ReanalyzeAsync(string providerId)
    {
        if (CurrentPath != null && providerId.Length > 0) await LoadFileAsync(CurrentPath, providerId);
    }

    public async Task RemoveRecentAsync(string path) { Recent.Remove(path); await PushRecentAsync(); }

    public async Task ClearRecentAsync() { Recent.Clear(); await PushRecentAsync(); }

    public async Task ExportHtmlAsync()
    {
        if (LastAnalysis == null)
        {
            await _platform.ShowMessageAsync(HostMessageKind.Info, "Open a report first, then export the dashboard.");
            return;
        }
        var path = await _platform.PickSaveFileAsync("Export dashboard", [new("HTML dashboard", ["*.html"])], (LastReportName ?? "report") + "-analysis.html");
        if (string.IsNullOrEmpty(path)) return;
        await File.WriteAllTextAsync(path, DashboardBuilder.Build(LastAnalysis, LastReportHtml, DashboardMode.Export), new UTF8Encoding(false));
    }

    public async Task ExportScriptAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await _platform.ShowMessageAsync(HostMessageKind.Info, "There is no SQL script to save.");
            return;
        }
        var path = await _platform.PickSaveFileAsync("Save SQL script", [new("SQL script", ["*.sql"])], (LastReportName ?? "report") + "-findings.sql");
        if (string.IsNullOrEmpty(path)) return;
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
    }

    public Task CopyAsync(string text) => text.Length > 0 ? _platform.SetClipboardAsync(text) : Task.CompletedTask;

    public Task PrintAsync() => _platform.PrintAsync();

    public async Task PushRecentAsync()
    {
        if (!_ready || _platform.IsClosed) return;
        var items = Recent.Items.Select(r => new { path = r.Path, name = Path.GetFileName(r.Path), type = r.Type, opened = r.Opened, exists = _fileExists(r.Path) });
        await _platform.RunScriptAsync("window.AwrApp.setRecent(" + JsonSerializer.Serialize(items) + ")");
    }

    public async Task NotifyAsync(string kind, string message)
    {
        if (_platform.IsClosed) return;
        await _platform.RunScriptAsync("window.AwrApp.notify(" + JsonSerializer.Serialize(new { kind, message }) + ")");
    }
}
