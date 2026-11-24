// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apodofos.Core.Analysis;

namespace Apodofos.Core.Export;

/// <summary>Where the dashboard runs; the UI uses it to decide which toolbar actions are available.</summary>
public enum DashboardMode { Desktop, Export }

/// <summary>
/// Produces the single, self-contained HTML dashboard (UI + data + sanitised original report) used by
/// the desktop app, the CLI export and tests. No external resources: everything is inlined.
/// See docs/ui-contract.md for the template placeholders and the JSON shape.
/// </summary>
public static class DashboardBuilder
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
        // Default encoder escapes <, >, & and quotes, so the JSON is safe inside a <script> element.
    };

    public static string ReadAsset(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream("Apodofos.Web." + name)
                      ?? throw new InvalidOperationException($"Embedded web asset '{name}' not found.");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    public static string ToJson(AnalysisResult? analysis, string? reportHtml, DashboardMode mode) =>
        JsonSerializer.Serialize(new DashboardPayload(analysis, reportHtml, mode == DashboardMode.Desktop ? "desktop" : "export"), JsonOptions);

    /// <summary>Builds the dashboard. Pass null analysis for the empty start screen.</summary>
    public static string Build(AnalysisResult? analysis, string? reportHtml, DashboardMode mode)
    {
        var html = ReadAsset("index.html");
        var css = ReadAsset("app.css") + "\n" + ReadAsset("report.css");
        var charts = ReadAsset("charts.js") + "\n</script>\n<script>\n" + ReadAsset("report.js");
        var app = ReadAsset("app.js");
        var icon = TryReadAsset("icon.svg");
        var json = ToJson(analysis, reportHtml, mode);

        return html
            .Replace("<!--AWR:CSS-->", "<style>\n" + css + "\n</style>")
            .Replace("<!--AWR:ICON-->", icon ?? "")
            .Replace("<!--AWR:DATA-->", "<script>window.__AWR__ = " + json + ";</script>")
            .Replace("<!--AWR:SCRIPTS-->", "<script>\n" + charts + "\n</script>\n<script>\n" + app + "\n</script>");
    }

    private static string? TryReadAsset(string name)
    {
        try { return ReadAsset(name); } catch (InvalidOperationException) { return null; }
    }

    private sealed record DashboardPayload(AnalysisResult? Analysis, string? ReportHtml, string Mode);
}
