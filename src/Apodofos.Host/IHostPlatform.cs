// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

namespace Apodofos.Host;

/// <summary>One entry of a file dialog filter, e.g. ("HTML dashboard", ["*.html"]).</summary>
public sealed record FileFilter(string Label, string[] Patterns);

public enum HostMessageKind { Info, Warning, Error }

/// <summary>
/// What a shell (WinForms + WebView2 today, a browser-hosted shell on Linux later) must provide to the shared host logic.
/// Everything is async so a shell may marshal to its UI thread. No method may throw for a user cancelling a dialog:
/// return null instead.
/// </summary>
public interface IHostPlatform
{
    /// <summary>True once the window is gone; the session then stops pushing results.</summary>
    bool IsClosed { get; }

    /// <summary>Per-user folder for settings (created by the host when needed).</summary>
    string DataDirectory { get; }

    /// <summary>Open-file dialog; null when cancelled.</summary>
    Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters);

    /// <summary>Save-file dialog; null when cancelled.</summary>
    Task<string?> PickSaveFileAsync(string title, IReadOnlyList<FileFilter> filters, string suggestedName);

    Task SetClipboardAsync(string text);

    Task PrintAsync();

    /// <summary>Opens an already validated https URL in the user's default browser. The app itself never connects.</summary>
    Task OpenUrlAsync(string url);

    Task ShowMessageAsync(HostMessageKind kind, string text);

    void SetWindowTitle(string title);

    /// <summary>Runs a JavaScript statement in the page (the host-to-UI calls of docs/ui-contract.md).</summary>
    Task RunScriptAsync(string script);

    /// <summary>Shows or hides the "Analyzing report..." overlay and the wait cursor.</summary>
    void SetBusy(bool on);
}
