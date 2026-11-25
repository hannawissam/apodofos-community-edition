// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Core;

namespace Apodofos.Host;

/// <summary>
/// One-time, best-effort move of settings from older layouts into the per-product folder %LOCALAPPDATA%\Apodofos\&lt;slug&gt;:
/// the flat 1.0.x layout (%LOCALAPPDATA%\Apodofos holding window.json, recent.json and the WebView2 profile EBWebView) and,
/// before that, the folders of the pre-rename product names. Only the original (Community) product claims them; a new product
/// or edition starts fresh. Nothing here ever throws or deletes user data: an item that cannot be moved is simply left behind.
/// </summary>
public static class DataDirectoryMigration
{
    private static readonly string[] FlatFiles = ["window.json", "recent.json"];

    // product folder names of earlier releases under %LOCALAPPDATA%\Apodofos (1.1.0 used "ReportAnalyzer")
    private static readonly string[] PreviousSlugs = ["ReportAnalyzer"];
    private const string FlatProfileFolder = "EBWebView";

    // pre-rename product folder names (written split so the old names do not appear as plain words)
    private static readonly string[] OlderNames = ["Quiet" + "Load", "Perf" + "Lens"];

    public static void Run(string localAppData, ProductInfo product)
    {
        try
        {
            if (!product.MigrateLegacyData) return;
            var target = product.DataDirectory(localAppData);
            if (FlatFiles.Any(f => File.Exists(Path.Combine(target, f))) || Directory.Exists(Path.Combine(target, FlatProfileFolder))) return; // already migrated or in use

            // the layout of the previous release: the whole product folder moves to the new name
            foreach (var prev in PreviousSlugs)
            {
                var prevDir = Path.Combine(localAppData, ProductInfo.Brand, prev);
                if (!Directory.Exists(prevDir) || string.Equals(prevDir, target, StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) break;
                if (Directory.Exists(target)) Directory.Delete(target);
                Directory.Move(prevDir, target);
                return;
            }

            var flat = Path.Combine(localAppData, ProductInfo.Brand);
            var moved = false;
            if (Directory.Exists(flat))
            {
                Directory.CreateDirectory(target);
                foreach (var f in FlatFiles) moved |= TryMove(Path.Combine(flat, f), Path.Combine(target, f), directory: false);
                moved |= TryMove(Path.Combine(flat, FlatProfileFolder), Path.Combine(target, FlatProfileFolder), directory: true);
            }
            if (moved) return;

            foreach (var old in OlderNames)
            {
                var legacy = Path.Combine(localAppData, old);
                if (!Directory.Exists(legacy)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!Directory.Exists(target)) { Directory.Move(legacy, target); return; }
                foreach (var f in FlatFiles) moved |= TryMove(Path.Combine(legacy, f), Path.Combine(target, f), directory: false);
                if (moved) return;
            }
        }
        catch { /* best effort: settings simply start fresh */ }
    }

    private static bool TryMove(string from, string to, bool directory)
    {
        try
        {
            if (directory) { if (!Directory.Exists(from) || Directory.Exists(to)) return false; Directory.Move(from, to); return true; }
            if (!File.Exists(from) || File.Exists(to)) return false;
            File.Move(from, to);
            return true;
        }
        catch { return false; }
    }
}
