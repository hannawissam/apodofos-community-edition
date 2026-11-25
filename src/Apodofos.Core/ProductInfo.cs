// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Reflection;

namespace Apodofos.Core;

/// <summary>
/// Who this build is (shared by the analysis metadata, the report, the CLI and every shell). "Apodofos" is the brand (suite); a product is "Apodofos &lt;Tool&gt;" with its own folders, executables and
/// installer identity, so several products and both editions can live side by side and never touch each other's data.
/// The values come from Edition.props at build time (assembly metadata); the defaults below are the Community Report Analyzer.
/// The word "Apodofos" is a pending trade mark: never respell, abbreviate or fuse it in user-visible text.
/// </summary>
public sealed record ProductInfo(string Name, string Edition, string Slug, string CliName, bool MigrateLegacyData)
{
    /// <summary>The brand word, exactly as filed.</summary>
    public const string Brand = "Apodofos";

    public static ProductInfo Current { get; } = FromAssembly(typeof(ProductInfo).Assembly);

    public static ProductInfo FromAssembly(Assembly assembly)
    {
        string? Meta(string key) => assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal))?.Value;
        static string Or(string? v, string fallback) => string.IsNullOrWhiteSpace(v) ? fallback : v;
        return new ProductInfo(
            Or(Meta("ApodofosProductName"), "Apodofos Report Analyzer"),
            Or(Meta("ApodofosEdition"), "Community"),
            Or(Meta("ApodofosProductSlug"), "ReportAnalyzer"),
            Or(Meta("ApodofosCliName"), "apodofos-report"),
            !string.Equals(Meta("ApodofosMigrateLegacyData"), "false", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>%LOCALAPPDATA%\Apodofos\&lt;slug&gt;: settings, recent files and the WebView2 profile of this product only.</summary>
    public string DataDirectory(string localAppData) => Path.Combine(localAppData, Brand, Slug);

    public string DefaultDataDirectory => DataDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>"Apodofos Report Analyzer 1.1.0 (Community Edition)"; the Professional product name already says so.</summary>
    public string VersionLine(string version) =>
        string.Equals(Edition, "Community", StringComparison.Ordinal) ? $"{Name} {version} (Community Edition)" : $"{Name} {version}";
}
