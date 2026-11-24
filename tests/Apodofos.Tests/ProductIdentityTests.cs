// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using Apodofos.Core;
using Apodofos.Core.Export;
using Apodofos.Host;

namespace Apodofos.Tests;

/// <summary>
/// Product identity ("Apodofos" is the brand, a product is "Apodofos &lt;Tool&gt;"), the one-time data migration to the per-product
/// folder, and the guard that keeps the pending trade mark text intact through renames.
/// </summary>
public class ProductIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apodofos-id-" + Guid.NewGuid().ToString("N"));

    public ProductIdentityTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* temp */ } }

    private static readonly ProductInfo Community = new("Apodofos Report Analyzer", "Community", "ReportAnalyzer", "apodofos-report", true);
    private static readonly ProductInfo Professional = new("Apodofos Report Analyzer Professional", "Professional", "ReportAnalyzerPro", "apodofos-report-pro", false);

    // ---- identity ----

    [Fact]
    public void ThisBuildCarriesItsEditionIdentityFromEditionProps()
    {
        var p = ProductInfo.Current;
        Assert.StartsWith(ProductInfo.Brand + " ", p.Name, StringComparison.Ordinal);
        Assert.Equal("Apodofos", ProductInfo.Brand);
        Assert.False(string.IsNullOrWhiteSpace(p.Slug));
        Assert.DoesNotContain(' ', p.Slug);
        Assert.StartsWith("apodofos-report", p.CliName, StringComparison.Ordinal);
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Edition.props"));
        Assert.Contains("<ApodofosProductSlug>" + p.Slug + "</ApodofosProductSlug>", props, StringComparison.Ordinal);
        Assert.Contains("<ApodofosCliExe>" + p.CliName + "</ApodofosCliExe>", props, StringComparison.Ordinal);
        Assert.Contains("<ApodofosProductName>" + p.Name + "</ApodofosProductName>", props, StringComparison.Ordinal);
    }

    [Fact]
    public void EachProductAndEditionHasItsOwnDataFolderSoNothingIsShared()
    {
        var a = Community.DataDirectory(_root);
        var b = Professional.DataDirectory(_root);
        Assert.Equal(Path.Combine(_root, "Apodofos", "ReportAnalyzer"), a);
        Assert.Equal(Path.Combine(_root, "Apodofos", "ReportAnalyzerPro"), b);
        Assert.NotEqual(a, b);
        Assert.False(b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)); // neither folder is inside the other
    }

    [Fact]
    public void VersionLineNamesTheEditionOnlyWhenTheNameDoesNot()
    {
        Assert.Equal("Apodofos Report Analyzer 1.1.0 (Community Edition)", Community.VersionLine("1.1.0"));
        Assert.Equal("Apodofos Report Analyzer Professional 1.1.0", Professional.VersionLine("1.1.0"));
    }

    [Fact]
    public void AssemblyMetadataFallsBackToTheCommunityReportAnalyzer()
    {
        var p = ProductInfo.FromAssembly(typeof(string).Assembly); // no Apodofos metadata there
        Assert.Equal("Apodofos Report Analyzer", p.Name);
        Assert.Equal("ReportAnalyzer", p.Slug);
        Assert.Equal("apodofos-report", p.CliName);
        Assert.True(p.MigrateLegacyData);
    }

    [Fact]
    public void WindowTitleAndDialogsUseTheProductName()
    {
        var s = new HostSession(new NullPlatform(), new RecentStore(_root), "1.2.3", appTitle: Professional.Name);
        Assert.Equal("Apodofos Report Analyzer Professional 1.2.3", s.DefaultTitle);
        Assert.Equal("Apodofos Report Analyzer 1.2.3", new HostSession(new NullPlatform(), new RecentStore(_root), "1.2.3").DefaultTitle);
    }

    // ---- migration from the 1.0.x flat layout ----

    private string Flat => Path.Combine(_root, "Apodofos");

    private void MakeOldInstallData()
    {
        Directory.CreateDirectory(Path.Combine(Flat, "EBWebView", "Default"));
        File.WriteAllText(Path.Combine(Flat, "window.json"), "{\"X\":1}");
        File.WriteAllText(Path.Combine(Flat, "recent.json"), "[]");
        File.WriteAllText(Path.Combine(Flat, "EBWebView", "Default", "Preferences"), "p");
    }

    [Fact]
    public void OldFlatSettingsMoveIntoTheProductFolderExactlyOnce()
    {
        MakeOldInstallData();
        DataDirectoryMigration.Run(_root, Community);
        var target = Community.DataDirectory(_root);
        Assert.Equal("{\"X\":1}", File.ReadAllText(Path.Combine(target, "window.json")));
        Assert.True(File.Exists(Path.Combine(target, "recent.json")));
        Assert.True(File.Exists(Path.Combine(target, "EBWebView", "Default", "Preferences")));
        Assert.False(File.Exists(Path.Combine(Flat, "window.json")));
        Assert.False(Directory.Exists(Path.Combine(Flat, "EBWebView")));

        File.WriteAllText(Path.Combine(target, "window.json"), "{\"X\":2}"); // the user keeps working
        DataDirectoryMigration.Run(_root, Community); // second start: nothing more to do
        Assert.Equal("{\"X\":2}", File.ReadAllText(Path.Combine(target, "window.json")));
    }

    [Fact]
    public void AnotherProductsFolderInsideTheBrandFolderIsNeverTouched()
    {
        MakeOldInstallData();
        var other = Path.Combine(Flat, "OtherTool");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "window.json"), "other"); // same file name, different product
        DataDirectoryMigration.Run(_root, Community);
        Assert.Equal("other", File.ReadAllText(Path.Combine(other, "window.json")));
        Assert.True(File.Exists(Path.Combine(Community.DataDirectory(_root), "window.json")));
    }

    [Fact]
    public void TheProfessionalEditionStartsFreshAndLeavesTheOldSettingsToCommunity()
    {
        MakeOldInstallData();
        DataDirectoryMigration.Run(_root, Professional);
        Assert.True(File.Exists(Path.Combine(Flat, "window.json")));
        Assert.False(Directory.Exists(Professional.DataDirectory(_root)));
        DataDirectoryMigration.Run(_root, Community);
        Assert.True(File.Exists(Path.Combine(Community.DataDirectory(_root), "window.json")));
    }

    [Fact]
    public void AnExistingNewFolderWithSettingsWinsAndOldFilesStayPut()
    {
        MakeOldInstallData();
        var target = Community.DataDirectory(_root);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "recent.json"), "[\"new\"]");
        DataDirectoryMigration.Run(_root, Community);
        Assert.Equal("[\"new\"]", File.ReadAllText(Path.Combine(target, "recent.json")));
        Assert.True(File.Exists(Path.Combine(Flat, "window.json"))); // not overwritten, not lost
    }

    [Fact]
    public void FoldersOfThePreRenameProductNamesAreAdoptedWhenThereIsNothingFlat()
    {
        var older = Path.Combine(_root, "Quiet" + "Load");
        Directory.CreateDirectory(older);
        File.WriteAllText(Path.Combine(older, "window.json"), "old");
        DataDirectoryMigration.Run(_root, Community);
        Assert.Equal("old", File.ReadAllText(Path.Combine(Community.DataDirectory(_root), "window.json")));
        Assert.False(Directory.Exists(older));
    }

    [Fact]
    public void MigrationNeverThrowsWhateverIsOnDisk()
    {
        DataDirectoryMigration.Run(Path.Combine(_root, "does-not-exist"), Community);
        File.WriteAllText(Flat.TrimEnd(Path.DirectorySeparatorChar), "a file where the folder should be");
        DataDirectoryMigration.Run(_root, Community);
        DataDirectoryMigration.Run("", Community);
    }

    [Fact]
    public void TheAnalysisNamesTheProductThatProducedIt()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "awr-19c-synthetic.html"));
        var a = Apodofos.Core.Analysis.AnalysisEngine.Analyze(Apodofos.Core.Parsing.AwrReportLoader.LoadContent(html, "t.html"));
        Assert.Equal(ProductInfo.Current.Name, a.Meta.Product);
        Assert.Equal(ProductInfo.Current.Edition, a.Meta.Edition);
        Assert.Contains("\"product\":\"" + ProductInfo.Current.Name + "\"", DashboardBuilder.ToJson(a, null, DashboardMode.Export), StringComparison.Ordinal);
    }

    // ---- trade mark guard ----

    [Fact]
    public void TheTradeMarkNoticeIsPresentWhereUsersAndReadersSeeIt()
    {
        const string facts = "French trade mark application no. 5322937, not yet registered";
        Assert.Contains("Apodofos&trade; is a trademark of Wissam HANNA (" + facts + ")", DashboardBuilder.ReadAsset("index.html"), StringComparison.Ordinal);
        var root = RepoRoot();
        foreach (var f in new[] { "NOTICE", "README.md", Path.Combine("src", "Apodofos.Cli", "Program.cs"), Path.Combine("src", "Apodofos.Core", "Web", "report.js") })
        {
            var t = File.ReadAllText(Path.Combine(root, f));
            Assert.True(t.Contains("5322937", StringComparison.Ordinal) && t.Contains("not yet registered", StringComparison.Ordinal), f + " must keep the trade mark notice");
        }
    }

    [Fact]
    public void TheBrandWordIsNeverMisspeltAndNeverCarriesTheRegisteredSign()
    {
        // the mark is only applied for: no registered-sign before registration, and never a variant spelling in text people read
        string[] typos = ["Apodo" + "fus", "Apodo" + "phos", "Apodof" + "fos", "Apodo" + "fox"];
        string[] registered = ["Apodofos" + "®", "Apodofos" + "&reg;", "Apodofos" + " (R)", "Apodofos" + "(R)"];
        var bad = new List<string>();
        foreach (var file in TextFiles(RepoRoot()))
        {
            var t = File.ReadAllText(file);
            foreach (var w in typos.Concat(registered))
                if (t.Contains(w, StringComparison.OrdinalIgnoreCase)) bad.Add(Path.GetRelativePath(RepoRoot(), file) + ": " + w);
        }
        Assert.True(bad.Count == 0, "Brand word problems:\n" + string.Join("\n", bad));
    }

    private static IEnumerable<string> TextFiles(string root)
    {
        string[] ext = [".md", ".html", ".cs", ".js", ".css", ".wxs", ".txt", ".ps1", ".props", ".yml", ".slnx", ".csproj", ".wixproj"];
        string[] skip = ["bin", "obj", ".git", "node_modules", "website", "_parked", "EXAMPLES", "EXAMPLES-local", "MARKETING-local", "artifacts", "TestData"];
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, f);
            var parts = rel.Split(Path.DirectorySeparatorChar);
            if (parts.Any(p => skip.Contains(p, StringComparer.OrdinalIgnoreCase))) continue;
            if (!ext.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
            if (Path.GetFileName(f) == "THIRD-PARTY-NOTICES.md") continue;
            yield return f;
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Apodofos.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private sealed class NullPlatform : IHostPlatform
    {
        public bool IsClosed => false;
        public string DataDirectory => "";
        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveFileAsync(string title, IReadOnlyList<FileFilter> filters, string suggestedName) => Task.FromResult<string?>(null);
        public Task SetClipboardAsync(string text) => Task.CompletedTask;
        public Task PrintAsync() => Task.CompletedTask;
        public Task<bool> PrintToPdfAsync(string path) => Task.FromResult(false);
        public Task OpenUrlAsync(string url) => Task.CompletedTask;
        public Task ShowMessageAsync(HostMessageKind kind, string text) => Task.CompletedTask;
        public void SetWindowTitle(string title) { }
        public Task RunScriptAsync(string script) => Task.CompletedTask;
        public void SetBusy(bool on) { }
    }
}
