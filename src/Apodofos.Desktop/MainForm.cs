// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Reflection;
using System.Text.Json;
using Apodofos.Core.Export;
using Apodofos.Host;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Apodofos.Desktop;

/// <summary>
/// The Windows shell: window, dialogs, WebView2 wiring, drag and drop and window placement. Everything else (message
/// routing, the load pipeline, exports, the recent list) lives in <see cref="Apodofos.Host"/> and is shared by every shell.
/// </summary>
public sealed class MainForm : Form, IHostPlatform
{
    private static readonly ProductInfo Product = ProductInfo.Current;
    private static string AppTitle => Product.Name;
    private static readonly string AppVersion = ReadAppVersion();

    private static string ReadAppVersion()
    {
        var asm = typeof(MainForm).Assembly;
        var v = asm.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .OfType<AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
            ?? asm.GetName().Version?.ToString(3) ?? "";
        var plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? v[..plus] : v;
    }

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
    private readonly HostSession _session;
    private readonly HostMessageRouter _router;

    public MainForm(string? initialFile)
    {
        _initialFile = initialFile;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _dataDir = Product.DataDirectory(localAppData); // %LOCALAPPDATA%\Apodofos\<product>: never shared with another product or edition
        DataDirectoryMigration.Run(localAppData, Product); // one-time move from the 1.0.x flat layout
        _session = new HostSession(this, new RecentStore(_dataDir), AppVersion, appTitle: Product.Name);
        _router = new HostMessageRouter(_session, this);
        Text = _session.DefaultTitle;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1400, 900);
        MinimumSize = new Size(1100, 700);
        KeyPreview = true;
        AllowDrop = true;
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Apodofos.ico");
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

        RestoreWindowPlacement();

        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) Run(_session.LoadFileAsync(files[0]));
        };
        FormClosing += (_, _) => SaveWindowPlacement();
        Shown += async (_, _) => await InitWebViewAsync();
    }

    // ---- window placement (<data folder>\window.json); the recent list belongs to the host (recent.json) ----
    private sealed record Placement(int X, int Y, int W, int H, bool Max);

    private string PlacementFile => Path.Combine(_dataDir, "window.json");

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

    private void SaveWindowPlacement()
    {
        try
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(PlacementFile, JsonSerializer.Serialize(
                new Placement(b.X, b.Y, b.Width, b.Height, WindowState == FormWindowState.Maximized)));
        }
        catch { /* best effort */ }
    }

    // Fire-and-forget entry points (keys, drag and drop): failures are shown, never thrown into the message loop.
    private async void Run(Task work)
    {
        try { await work; }
        catch (Exception ex) when (!IsDisposed) { await ((IHostPlatform)this).ShowMessageAsync(HostMessageKind.Error, ex.Message); }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is (Keys.Control | Keys.O) or Keys.F5) { Run(_session.OpenDialogAsync()); return true; }
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
        core.NavigationCompleted += (_, _) => Run(_session.OnPageReadyAsync(_initialFile));
        // F5 / Ctrl+O pressed while the web view has focus never reach the form: relay them.
        _web.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5 || (e.Control && e.KeyCode == Keys.O)) { e.Handled = true; Run(_session.OpenDialogAsync()); }
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

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Files dropped on the page arrive as CoreWebView2File objects carrying the native path.
        string[]? dropped = e.AdditionalObjects is { Count: > 0 }
            ? e.AdditionalObjects.OfType<CoreWebView2File>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray()
            : null;
        Run(_router.HandleAsync(e.WebMessageAsJson, dropped));
    }

    // ---- IHostPlatform ----
    bool IHostPlatform.IsClosed => IsDisposed;

    string IHostPlatform.DataDirectory => _dataDir;

    Task<string?> IHostPlatform.PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters)
    {
        using var dlg = new OpenFileDialog { Title = title, Filter = ToDialogFilter(filters), FilterIndex = 1, CheckFileExists = true };
        return Task.FromResult(dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null);
    }

    Task<string?> IHostPlatform.PickSaveFileAsync(string title, IReadOnlyList<FileFilter> filters, string suggestedName)
    {
        using var dlg = new SaveFileDialog { Title = title, Filter = ToDialogFilter(filters), FileName = suggestedName };
        return Task.FromResult(dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null);
    }

    private static string ToDialogFilter(IReadOnlyList<FileFilter> filters) =>
        string.Join("|", filters.Select(f =>
        {
            var patterns = string.Join(";", f.Patterns);
            return f.Label + " (" + patterns + ")|" + patterns;
        }));

    Task IHostPlatform.SetClipboardAsync(string text)
    {
        Clipboard.SetText(text);
        return Task.CompletedTask;
    }

    Task IHostPlatform.PrintAsync()
    {
        _web.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
        return Task.CompletedTask;
    }

    async Task<bool> IHostPlatform.PrintToPdfAsync(string path)
    {
        if (_web.CoreWebView2 == null) return false;
        // The page's print layout (report.css): A4, backgrounds on, no browser header/footer (the report carries its own page footer).
        var json = await _web.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.printToPDF",
            "{\"printBackground\":true,\"preferCSSPageSize\":true,\"displayHeaderFooter\":false,\"transferMode\":\"ReturnAsBase64\"}");
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetString() is not { Length: > 0 } b64) return false;
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(b64));
        return true;
    }

    Task IHostPlatform.OpenUrlAsync(string url)
    {
        // The host has validated the URL (https, oracle.com). The default browser opens it; this app makes no connection.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    Task IHostPlatform.ShowMessageAsync(HostMessageKind kind, string text)
    {
        if (IsDisposed) return Task.CompletedTask;
        MessageBox.Show(this, text, AppTitle, MessageBoxButtons.OK,
            kind switch { HostMessageKind.Error => MessageBoxIcon.Error, HostMessageKind.Warning => MessageBoxIcon.Warning, _ => MessageBoxIcon.Information });
        return Task.CompletedTask;
    }

    void IHostPlatform.SetWindowTitle(string title) => Text = title;

    async Task IHostPlatform.RunScriptAsync(string script)
    {
        if (IsDisposed || _web.CoreWebView2 == null) return;
        await _web.CoreWebView2.ExecuteScriptAsync(script);
    }

    void IHostPlatform.SetBusy(bool on)
    {
        if (IsDisposed) return;
        _busy.Visible = on;
        UseWaitCursor = on;
    }
}
