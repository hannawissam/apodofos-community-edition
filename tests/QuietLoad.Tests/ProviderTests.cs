// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using QuietLoad.Core.Analysis;
using QuietLoad.Core.Providers;

namespace QuietLoad.Tests;

public class ProviderTests
{
    private sealed class FakePostgresProvider : IReportProvider
    {
        public string Id => "fake-postgres";
        public string DisplayName => "Fake PostgreSQL report";
        public string Engine => "PostgreSQL";
        public IReadOnlyList<string> FileExtensions { get; } = [".pgrep"];
        public bool CanHandle(string content, string? fileName) => content.StartsWith("PGREPORT");
        public ProviderResult Analyze(string content, string sourceName) =>
            new(new AnalysisResult { Meta = new ReportMeta { DbName = "pg" } }, "<html></html>");
    }

    [Fact]
    public void OracleProviderIsRegisteredByDefault()
    {
        var p = Assert.Single(ReportProviderRegistry.All, x => x.Id == "oracle-awr");
        Assert.Equal("Oracle Database", p.Engine);
        Assert.Contains(".html", ReportProviderRegistry.FileExtensions);
    }

    [Fact]
    public void AnalyzesSyntheticReportThroughRegistry()
    {
        var r = ReportProviderRegistry.AnalyzeFile(Samples.Syn11);
        Assert.Equal("TESTDB11", r.Analysis.Meta.DbName);
        Assert.NotEmpty(r.Analysis.Findings);
        Assert.Contains("<html", r.ReportHtml, StringComparison.OrdinalIgnoreCase);
    }

    [ThirdPartyFact]
    public void AnalyzesRealReportThroughRegistry()
    {
        var r = ReportProviderRegistry.AnalyzeFile(Samples.Db11);
        Assert.Equal("DB11G", r.Analysis.Meta.DbName);
        Assert.NotEmpty(r.Analysis.Findings);
        Assert.Contains("<html", r.ReportHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownContentGivesFriendlyError()
    {
        var ex = Assert.Throws<ReportFormatException>(() => ReportProviderRegistry.AnalyzeContent("hello world, not a report", "x.txt"));
        Assert.Contains("Oracle AWR report", ex.Message);
    }

    [Fact]
    public void AdditionalProvidersPlugIn()
    {
        ReportProviderRegistry.Register(new FakePostgresProvider());
        try
        {
            var r = ReportProviderRegistry.AnalyzeContent("PGREPORT 1", "a.pgrep");
            Assert.Equal("pg", r.Analysis.Meta.DbName);
            Assert.Contains(".pgrep", ReportProviderRegistry.FileExtensions);
            // Oracle reports still resolve to the Oracle provider
            Assert.Equal("oracle-awr", ReportProviderRegistry.Resolve("WORKLOAD REPOSITORY report for", null)!.Id);
        }
        finally
        {
            // keep the shared registry clean for other tests
            var field = typeof(ReportProviderRegistry).GetField("Providers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            ((List<IReportProvider>)field.GetValue(null)!).RemoveAll(p => p.Id == "fake-postgres");
        }
    }
}
