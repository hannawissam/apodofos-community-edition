// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Reflection;
using System.Text;
using System.Text.Json;
using QuietLoad.Core.Analysis;
using QuietLoad.Core.Export;
using QuietLoad.Core.Parsing;
using QuietLoad.Core.Providers;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace QuietLoad.Desktop;

/// <summary>Single main window hosting the embedded offline dashboard in WebView2.</summary>
public sealed class MainForm : Form
{
    private const string AppTitle = "QuietLoad Report Analyzer";
    private const int MaxRecent = 5;

    // Host-injected (documented in docs/ui-contract.md): turns a file dropped on the page into a native
    // path message. WebView2 gives the host a CoreWebView2File (with Path) for File objects posted this way.
    private const string DropScript = """
        (function () {
          var w = window.chrome && window.chrome.webview;
          if (!w || typeof w.postMessageWithAdditionalObjects !== 'function') return;
          window.addEventListener('dragover', function (e) { e.preventDefault(); }, true);
          window.addEventListener('drop', function (e) {
            var f = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
            if (!f) return;
            e.preventDefault(); e.stopImmediatePropagation();
            try { window.dispatchEvent(new CustomEvent('ql:dropped')); } catch (x) { }
            try { w.postMessageWithAdditionalObjects({ type: 'dropFile' }, [f]); } catch (x) { }
          }, true);
        })();
        """;

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, AllowExternalDrop = true };
    private readonly Panel _busy = new() { Dock = DockStyle.Fill, Visible = false, BackColor = Color.FromArgb(235, 245, 245, 245) };
    private readonly string? _initialFile;
    private readonly string _dataDir;
    private bool _ready;
    private string? _pendingFile;
    private bool _busyNow;

    private bool _dialogOpen;
    private string? _currentPath;
    private List<RecentItem> _recent = [];

    private string? _lastReportName;
    private AnalysisResult? _lastAnalysis;
    private string? _lastReportHtml;

    public MainForm(string? initialFile)
    {
        _initialFile = initialFile;
        _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietLoad");
        MigrateLegacyDataDir(_dataDir);
        Text = AppTitle;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1400, 900);
        MinimumSize = new Size(1100, 700);
        KeyPreview = true;
        AllowDrop = true;
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("QuietLoad.ico");
            if (s != null) Icon = new Icon(s);
        }
        catch { /* default icon */ }

        _busy.Controls.Add(new Label
        {
            Text = "Analyzing report...",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, 16f, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 40, 40)
        });
        Controls.Add(_busy);
        Controls.Add(_web);
        _busy.BringToFront();

        LoadRecent();
        RestoreWindowPlacement();

        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) await LoadFileAsync(files[0]);
        };
        FormClosing += (_, _) => SaveSettings();
        Shown += async (_, _) => await InitWebViewAsync();
    }

    // One-time migration of the settings folder used before the product was renamed.
    private static void MigrateLegacyDataDir(string newDir)
    {
        try
        {
            var legacy = Path.Combine(Path.GetDirectoryName(newDir)!, "Perf" + "Lens");
            if (Directory.Exists(legacy) && !Directory.Exists(newDir)) Directory.Move(legacy, newDir);
        }
        catch { /* best effort; settings simply start fresh */ }
    }

    // ---- window placement (%LOCALAPPDATA%\QuietLoad\window.json) ----
    private sealed record RecentItem(string Path, string Type, string Opened);
    private sealed record Placement(int X, int Y, int W, int H, bool Max, List<RecentItem>? Recent = null);

    private string PlacementFile => Path.Combine(_dataDir, "window.json");

    private void LoadRecent()
    {
        try
        {
            if (!File.Exists(PlacementFile)) return;
            var p = JsonSerializer.Deserialize<Placement>(File.ReadAllText(PlacementFile));
            _recent = (p?.Recent ?? []).Where(r => !string.IsNullOrWhiteSpace(r.Path)).Take(MaxRecent).ToList();
        }
        catch { /* ignore corrupt settings */ }
    }

    private void RestoreWindowPlacement()
    {
        try
        {
            if (!File.Exists(PlacementFile)) return;
            var p = JsonSerializer.Deserialize<Placement>(File.ReadAllText(PlacementFile));
            if (p == null || p.W < MinimumSize.Width || p.H < MinimumSize.Height) return;
            var rect = new Rectangle(p.X, p.Y, p.W, p.H);
            // Only restore when a reasonable part of the title bar is still on a connected screen.
            var strip = new Rectangle(rect.X, rect.Y, rect.Width, 40);
            if (!Screen.AllScreens.Any(s => Rectangle.Intersect(s.WorkingArea, strip) is { Width: > 100, Height: > 20 })) return;
            StartPosition = FormStartPosition.Manual;
            Bounds = rect;
            if (p.Max) WindowState = FormWindowState.Maximized;
        }
        catch { /* ignore corrupt settings */ }
    }

    // Window placement and the recent-reports list (paths only) share %LOCALAPPDATA%\QuietLoad\window.json.
    private void SaveSettings()
    {
        try
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(PlacementFile, JsonSerializer.Serialize(
                new Placement(b.X, b.Y, b.Width, b.Height, WindowState == FormWindowState.Maximized, _recent)));
        }
        catch { /* best effort */ }
    }

    private void AddRecent(string path, string type)
    {
        _recent.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        _recent.Insert(0, new RecentItem(path, type, DateTime.Now.ToString("yyyy-MM-dd")));
        if (_recent.Count > MaxRecent) _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
        SaveSettings();
    }

    private async Task PushRecentAsync()
    {
        if (!_ready || _web.CoreWebView2 == null || IsDisposed) return;
        var items = _recent.Select(r => new { path = r.Path, name = Path.GetFileName(r.Path), type = r.Type, opened = r.Opened, exists = SafeExists(r.Path) });
        await _web.CoreWebView2.ExecuteScriptAsync("window.AwrApp.setRecent(" + JsonSerializer.Serialize(items) + ")");
    }

    private static bool SafeExists(string path) { try { return File.Exists(path); } catch { return false; } }

    private async Task NotifyAsync(string kind, string message)
    {
        if (_web.CoreWebView2 == null || IsDisposed) return;
        await _web.CoreWebView2.ExecuteScriptAsync("window.AwrApp.notify(" + JsonSerializer.Serialize(new { kind, message }) + ")");
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is (Keys.Control | Keys.O) or Keys.F5) { _ = OpenDialogAsync(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            // Never downloads anything: throws when no Evergreen runtime is installed.
            var env = await CoreWebView2Environment.CreateAsync(null, _dataDir);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException
                                       or FileNotFoundException or DllNotFoundException or BadImageFormatException
                                       or InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(this,
                "The Microsoft Edge WebView2 Runtime is required but was not found (or could not be started).\n\n" +
                "This application works offline and never downloads anything by itself. Please install the " +
                "WebView2 \"Evergreen\" runtime (it ships with Windows 11 and Microsoft Edge) and start the application again.\n\n" +
                "Details: " + ex.Message,
                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        var settings = core.Settings;
        settings.IsStatusBarEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsWebMessageEnabled = true;
        settings.IsZoomControlEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false; // we own F5 / Ctrl+O; no reload/navigation via browser keys
#if DEBUG
        settings.AreDevToolsEnabled = true;
        settings.AreDefaultContextMenusEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
#endif
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += async (_, _) =>
        {
            if (_ready) return;
            _ready = true;
            var file = _pendingFile ?? _initialFile;
            await PushRecentAsync();
            if (!string.IsNullOrEmpty(file)) await LoadFileAsync(file);
        };
        // F5 / Ctrl+O pressed while the web view has focus never reach the form: relay them.
        _web.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5 || (e.Control && e.KeyCode == Keys.O)) { e.Handled = true; _ = OpenDialogAsync(); }
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(DropScript);

        core.NavigateToString(DashboardBuilder.Build(null, null, DashboardMode.Desktop));
    }

    private static void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Only our own in-memory document (NavigateToString => data: URI) and about: pages are allowed.
        var uri = e.Uri ?? "";
        var own = uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase) ||
                  uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase);
        if (!own) e.Cancel = true;
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var t)) return;
            var text = root.TryGetProperty("text", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
            switch (t.GetString())
            {
                case "open": await OpenDialogAsync(); break;
                case "exportHtml": ExportHtml(); break;
                case "exportScript": ExportScript(text); break;
                case "copy": if (text.Length > 0) Clipboard.SetText(text); break;
                case "print": _web.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); break;
                case "openPath":
                    if (root.TryGetProperty("path", out var pp) && pp.ValueKind == JsonValueKind.String && pp.GetString() is { Length: > 0 } rp) await LoadFileAsync(rp);
                    break;
                case "removeRecent":
                    if (root.TryGetProperty("path", out var rr) && rr.GetString() is { } rpath)
                    { _recent.RemoveAll(r => string.Equals(r.Path, rpath, StringComparison.OrdinalIgnoreCase)); SaveSettings(); await PushRecentAsync(); }
                    break;
                case "clearRecent": _recent.Clear(); SaveSettings(); await PushRecentAsync(); break;
                case "reanalyze":
                    if (_currentPath != null && root.TryGetProperty("provider", out var pv) && pv.ValueKind == JsonValueKind.String)
                        await LoadFileAsync(_currentPath, pv.GetString());
                    break;
                case "dropFile":
                    if (e.AdditionalObjects is { Count: > 0 } && e.AdditionalObjects[0] is CoreWebView2File f && !string.IsNullOrEmpty(f.Path))
                        await LoadFileAsync(f.Path);
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task OpenDialogAsync()
    {
        if (_busyNow || _dialogOpen) return;
        _dialogOpen = true;
        try
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Open Oracle report",
                Filter = "All supported reports (*.html;*.htm;*.txt;*.lst;*.log)|*.html;*.htm;*.txt;*.lst;*.log|" +
                         "AWR reports (*.html;*.htm;*.txt;*.lst)|*.html;*.htm;*.txt;*.lst|" +
                         "ASH reports (*.html;*.htm;*.txt;*.lst)|*.html;*.htm;*.txt;*.lst|" +
                         "Statspack reports (*.txt;*.lst;*.log)|*.txt;*.lst;*.log|" +
                         "All files (*.*)|*.*",
                FilterIndex = 1,
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) await LoadFileAsync(dlg.FileName);
        }
        finally { _dialogOpen = false; }
    }

    // One path for the button, drag and drop, the command line, recent reports and "Not right?".
    private async Task LoadFileAsync(string path, string? forcedProviderId = null)
    {
        if (_busyNow || IsDisposed) return;
        if (!_ready || _web.CoreWebView2 == null) { _pendingFile = path; return; }
        SetBusy(true);
        try
        {
            var (outcome, json) = await Task.Run(() =>
            {
                var o = ReportLoader.Load(path, forcedProviderId);
                return (o, o.Result == null ? null : DashboardBuilder.ToJson(o.Result.Analysis, o.Result.ReportHtml, DashboardMode.Desktop));
            });
            if (IsDisposed || _web.CoreWebView2 == null) return;
            if (outcome.Result == null || json == null)
            {
                // Keep whatever report is already open; just explain what went wrong.
                await NotifyAsync("error", outcome.Message);
                await PushRecentAsync();
                return;
            }
            await _web.CoreWebView2.ExecuteScriptAsync("window.AwrApp.load(" + json + ")");
            await _web.CoreWebView2.ExecuteScriptAsync("window.AwrApp.setSource(" + JsonSerializer.Serialize(
                new { name = Path.GetFileName(path), provider = outcome.ProviderId, forced = forcedProviderId != null }) + ")");
            _currentPath = path;
            _lastAnalysis = outcome.Result.Analysis;
            _lastReportHtml = outcome.Result.ReportHtml;
            _lastReportName = Path.GetFileNameWithoutExtension(path);
            Text = Path.GetFileName(path) + " — " + AppTitle;
            AddRecent(path, outcome.Result.Analysis.Meta.ReportType);
            await PushRecentAsync();
            if (outcome.Status == ReportLoadStatus.Partial) await NotifyAsync("warning", outcome.Message);
        }
        catch (Exception) when (IsDisposed) { /* window closed while loading */ }
        catch (Exception)
        {
            await NotifyAsync("error", ReportLoader.UnreadableMessage);
        }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private void SetBusy(bool on)
    {
        _busyNow = on;
        _busy.Visible = on;
        UseWaitCursor = on;
    }

    private void ExportHtml()
    {
        if (_lastAnalysis == null) { MessageBox.Show(this, "Open a report first, then export the dashboard.", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        using var dlg = new SaveFileDialog
        {
            Title = "Export dashboard",
            Filter = "HTML dashboard (*.html)|*.html",
            FileName = (_lastReportName ?? "report") + "-analysis.html"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, DashboardBuilder.Build(_lastAnalysis, _lastReportHtml, DashboardMode.Export), new UTF8Encoding(false));
    }

    private void ExportScript(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { MessageBox.Show(this, "There is no SQL script to save.", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        using var dlg = new SaveFileDialog
        {
            Title = "Save SQL script",
            Filter = "SQL script (*.sql)|*.sql",
            FileName = (_lastReportName ?? "report") + "-findings.sql"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, text, new UTF8Encoding(false));
    }
}
