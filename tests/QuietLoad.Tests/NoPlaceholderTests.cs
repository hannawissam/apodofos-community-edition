// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA
using Xunit;

namespace QuietLoad.Tests;

public class NoPlaceholderTests
{
    [Fact]
    public void SourceUrlPlaceholder_IsGoneFromSourceAndDocs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QuietLoad.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var bad = new List<string>();
        foreach (var sub in new[] { "src", "docs" })
        {
            foreach (var f in Directory.EnumerateFiles(Path.Combine(dir!.FullName, sub), "*", SearchOption.AllDirectories))
            {
                if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".cs" or ".md" or ".html" or ".js" or ".css" or ".json")) continue;
                var t = File.ReadAllText(f);
                if (t.Contains("[SOURCE_URL]") || t.Contains("{SOURCE_URL}") && !f.EndsWith("licensing.md")) bad.Add(f);
            }
        }
        Assert.Empty(bad);
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QuietLoad.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void RepositoryUrl_NeverUsesTheOldNameOrAPrivateRepo()
    {
        var root = Root();
        var oldUrl = new System.Text.RegularExpressions.Regex(@"github\.com/hannawissam/quietload(?![-\w])|quietload-report-analyzer|quietload-professional-edition");
        var bad = new List<string>();
        var files = new List<string> { "README.md", "NOTICE", "SECURITY.md", "CONTRIBUTING.md", "TRADEMARK.md", "CLA.md" };
        foreach (var sub in new[] { "src", "docs" })
            files.AddRange(Directory.EnumerateFiles(Path.Combine(root, sub), "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                            && Path.GetExtension(f).ToLowerInvariant() is ".cs" or ".md" or ".html" or ".js")
                .Select(f => Path.GetRelativePath(root, f)));
        foreach (var f in files)
        {
            // legal-checklist.md and github-manager docs may name the private repo for the maintainer; public-facing files must not.
            if (f.EndsWith("legal-checklist.md") || f.EndsWith("architecture.md")) continue;
            if (oldUrl.IsMatch(File.ReadAllText(Path.Combine(root, f)))) bad.Add(f);
        }
        Assert.Empty(bad);
    }

    [Theory]
    [InlineData("CLA.md")]
    [InlineData("docs/privacy.md")]
    public void LegalTexts_HaveNoEmptyBracketPlaceholders(string file)
    {
        var t = File.ReadAllText(Path.Combine(Root(), file));
        Assert.DoesNotMatch(@"\[[^\]
]*\]", t.Replace("[x]", ""));
        Assert.DoesNotContain("TBD", t);
    }
}
