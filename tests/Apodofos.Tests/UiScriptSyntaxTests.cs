// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Diagnostics;
using Apodofos.Core.Export;

namespace Apodofos.Tests;

/// <summary>
/// A syntax error in the embedded UI scripts leaves the whole page dead (no buttons, no start screen) while every C# test stays
/// green. When Node.js is installed (GitHub runners have it) each script is parsed with <c>node --check</c>; without Node the
/// test does nothing, so a machine without Node is never blocked.
/// </summary>
public class UiScriptSyntaxTests
{
    [Theory]
    [InlineData("app.js")]
    [InlineData("charts.js")]
    public void EmbeddedUiScriptsParse(string asset)
    {
        var node = FindNode();
        if (node == null) return;
        var dir = Path.Combine(Path.GetTempPath(), "apodofos-js-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, asset);
            File.WriteAllText(file, DashboardBuilder.ReadAsset(asset));
            var psi = new ProcessStartInfo(node) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("--check");
            psi.ArgumentList.Add(file);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            Assert.True(p.WaitForExit(30_000), "node --check timed out");
            Assert.True(p.ExitCode == 0, asset + " has a JavaScript syntax error:\n" + err);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp */ } }
    }

    private static string? FindNode()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "node.exe" } : new[] { "node" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in names)
            {
                try { var f = Path.Combine(dir.Trim('"'), n); if (File.Exists(f)) return f; } catch { /* bad PATH entry */ }
            }
        return null;
    }
}
