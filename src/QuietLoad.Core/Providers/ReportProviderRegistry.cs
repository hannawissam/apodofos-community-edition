// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text;

namespace QuietLoad.Core.Providers;

/// <summary>Holds the available report providers and picks the one that can read a given file.</summary>
public static class ReportProviderRegistry
{
    private static readonly List<IReportProvider> Providers = Discover();

    /// <summary>Finds every provider in this assembly (a class with a public parameterless constructor), so adding one needs no registration.</summary>
    private static List<IReportProvider> Discover() =>
        typeof(ReportProviderRegistry).Assembly.GetTypes()
            .Where(t => typeof(IReportProvider).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false } && t.GetConstructor(Type.EmptyTypes) != null)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => (IReportProvider)Activator.CreateInstance(t)!)
            .ToList();
    private static readonly object Gate = new();

    public static IReadOnlyList<IReportProvider> All { get { lock (Gate) return [.. Providers]; } }

    /// <summary>Adds a provider (used by extension assemblies, e.g. the Pro edition). Replaces a provider with the same id.</summary>
    public static void Register(IReportProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Gate)
        {
            Providers.RemoveAll(p => p.Id == provider.Id);
            Providers.Add(provider);
        }
    }

    /// <summary>Every file extension any provider accepts, for the open-file dialog.</summary>
    public static IReadOnlyList<string> FileExtensions =>
        All.SelectMany(p => p.FileExtensions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static IReportProvider? Resolve(string content, string? fileName) =>
        All.FirstOrDefault(p => p.CanHandle(content, fileName));

    /// <summary>Finds a provider by its stable id (for example "oracle-ash"), or null.</summary>
    public static IReportProvider? ById(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Analyzes content. With <paramref name="forcedProviderId"/> the content sniff is skipped and that provider is used
    /// (the "Treat this file as" override); an unknown id throws <see cref="ArgumentException"/>.
    /// </summary>
    public static ProviderResult AnalyzeContent(string content, string sourceName, string? forcedProviderId = null)
    {
        IReportProvider provider;
        if (!string.IsNullOrWhiteSpace(forcedProviderId))
            provider = ById(forcedProviderId) ?? throw new ArgumentException("Unknown report provider '" + forcedProviderId + "'.", nameof(forcedProviderId));
        else
            provider = Resolve(content, sourceName)
                       ?? throw new ReportFormatException("This file is not a report type QuietLoad can read. Supported: " +
                                                          string.Join(", ", All.Select(p => p.DisplayName)) + ".");
        return provider.Analyze(content, sourceName);
    }

    /// <summary>Reads a file (UTF-8) and analyzes it with the matching, or the forced, provider.</summary>
    public static ProviderResult AnalyzeFile(string path, string? forcedProviderId = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Report not found.", path);
        string content;
        // Share read/write so a report that is still open in another program can be read.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, Encoding.UTF8))
            content = sr.ReadToEnd();
        return AnalyzeContent(content, path, forcedProviderId);
    }
}
