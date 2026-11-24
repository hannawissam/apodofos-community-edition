// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text.Json;

namespace Apodofos.Host;

/// <summary>
/// Turns the UI-to-host JSON messages of docs/ui-contract.md into <see cref="HostSession"/> calls. Unknown or malformed
/// messages are ignored; a failing action is reported to the user instead of crashing the shell.
/// </summary>
public sealed class HostMessageRouter
{
    private readonly HostSession _session;
    private readonly IHostPlatform _platform;

    public HostMessageRouter(HostSession session, IHostPlatform platform)
    {
        _session = session;
        _platform = platform;
    }

    /// <param name="json">The message as posted by the page.</param>
    /// <param name="droppedPaths">Native paths of files that travelled with the message (drag and drop), when the shell can provide them.</param>
    public async Task HandleAsync(string json, IReadOnlyList<string>? droppedPaths = null)
    {
        try
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { return; } // not our message
            using var _ = doc;
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return;
            var text = Str(root, "text") ?? "";
            switch (t.GetString())
            {
                case "open": await _session.OpenDialogAsync(); break;
                case "exportHtml": await _session.ExportHtmlAsync(); break;
                case "exportScript": await _session.ExportScriptAsync(text); break;
                case "copy": await _session.CopyAsync(text); break;
                case "openUrl": if (Str(root, "url") is { Length: > 0 } url) await _session.OpenUrlAsync(url); break;
                case "print": await _session.PrintAsync(); break;
                case "openPath": if (Str(root, "path") is { Length: > 0 } p) await _session.LoadFileAsync(p); break;
                case "removeRecent": if (Str(root, "path") is { } rp) await _session.RemoveRecentAsync(rp); break;
                case "clearRecent": await _session.ClearRecentAsync(); break;
                case "reanalyze": if (Str(root, "provider") is { Length: > 0 } pv) await _session.ReanalyzeAsync(pv); break;
                case "dropFile": if (droppedPaths is { Count: > 0 } && !string.IsNullOrEmpty(droppedPaths[0])) await _session.LoadFileAsync(droppedPaths[0]); break;
            }
        }
        catch (Exception ex)
        {
            await _platform.ShowMessageAsync(HostMessageKind.Error, ex.Message);
        }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
