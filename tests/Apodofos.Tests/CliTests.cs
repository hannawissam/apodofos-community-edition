// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Apodofos.Tests;

/// <summary>Runs the real apodofos CLI (built on demand) as a child process.</summary>
public class CliTests : IDisposable
{
    private static readonly Lazy<string> CliDll = new(BuildCli);
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "awr-cli-" + Guid.NewGuid().ToString("N"));

    public CliTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private static string BuildCli()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Apodofos.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var proj = Path.Combine(dir!.FullName, "src", "Apodofos.Cli");
        var cfg = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var (code, _, err) = Exec("dotnet", $"build \"{proj}\" -c {cfg} -nologo -v q");
        Assert.True(code == 0, "CLI build failed: " + err);
        return Path.Combine(proj, "bin", cfg, "net11.0", Apodofos.Core.ProductInfo.Current.CliName + ".dll");
    }

    private static (int Code, string Out, string Err) Exec(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { p.Kill(true); throw new TimeoutException(file + " " + args); }
        return (p.ExitCode, o.Result, e.Result);
    }

    private static (int Code, string Out, string Err) Cli(params string[] args) =>
        Exec("dotnet", $"\"{CliDll.Value}\" " + string.Join(" ", args.Select(a => "\"" + a + "\"")));

    [Fact]
    public void ExitCode0_TextSummary()
    {
        var (c, o, _) = Cli(Samples.Syn11);
        Assert.Equal(0, c);
        Assert.Contains("Health:", o);
        Assert.Contains("Findings", o);
    }

    [Fact]
    public void ExitCode0_Help() => Assert.Equal(0, Cli("--help").Code);

    [Fact]
    public void Version_PrintsProductAndEdition()
    {
        var (c, o, _) = Cli("--version");
        Assert.Equal(0, c);
        var p = Apodofos.Core.ProductInfo.Current; // the Community name carries "(Community Edition)", the Professional name already says so
        var suffix = p.Edition == "Community" ? @" \(Community Edition\)" : "";
        Assert.Matches("^" + System.Text.RegularExpressions.Regex.Escape(p.Name) + @" \d+\.\d+\.\d+\S*" + suffix + "$", o.Trim());
    }

    [Fact]
    public void License_PrintsNoticesWithoutReport()
    {
        var (c, o, _) = Cli("--license");
        Assert.Equal(0, c);
        Assert.Contains("Copyright (c) 2026 Wissam HANNA", o);
        Assert.Contains("AGPL-3.0-only", o);
        Assert.Contains("NO WARRANTY", o);
        Assert.Contains("https://github.com/hannawissam/apodofos-community-edition", o);
        Assert.DoesNotContain("[SOURCE_URL]", o);
        Assert.Contains("not affiliated with, sponsored by or endorsed by Oracle", o);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--bogus|x.html")]
    [InlineData("a.html|b.html")]
    [InlineData("a.html|--html")]
    public void ExitCode1_Usage(string args) => Assert.Equal(1, Cli(args.Split("|", StringSplitOptions.RemoveEmptyEntries)).Code);

    [Fact]
    public void ExitCode2_MissingFile()
    {
        var (c, _, e) = Cli(Path.Combine(_tmp, "nope.html"));
        Assert.Equal(2, c);
        Assert.Contains("cannot read", e);
    }

    [Fact]
    public void ExitCode3_NotAnAwrReport()
    {
        var f = Path.Combine(_tmp, "bad.txt");
        File.WriteAllText(f, "this is not an AWR report");
        Assert.Equal(3, Cli(f).Code);
    }

    [Fact]
    public void HtmlOutput_IsSelfContained_NoExternalUrls()
    {
        var f = Path.Combine(_tmp, "o.html");
        var (c, _, _) = Cli(Samples.Syn11, "--html", f);
        Assert.Equal(0, c);
        var html = File.ReadAllText(f);
        Assert.StartsWith("<!DOCTYPE html", html, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Regex.Matches(html, @"\b(?:src|href|action|srcset|poster)\s*=\s*[""']\s*(?:https?:)?//", RegexOptions.IgnoreCase).Select(m => m.Value));
        Assert.DoesNotMatch(@"url\(\s*[""']?\s*(?:https?:)?//", html);
        Assert.DoesNotMatch(@"<script[^>]+src\s*=", html);
    }

    [Fact]
    public void ScriptOutput_IsTextWithSql()
    {
        var f = Path.Combine(_tmp, "o.sql");
        var (c, _, _) = Cli(Samples.Syn19, "--script", f);
        Assert.Equal(0, c);
        var s = File.ReadAllText(f);
        Assert.StartsWith("-- Apodofos Report Analyzer", s);
        Assert.DoesNotContain('\0', s);
        Assert.Matches(@"(?im)^\s*(select|with|alter|show)\b", s);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void JsonOutput_RoundTrips(bool ebs)
    {
        var f = Path.Combine(_tmp, "o.json");
        var (c, _, _) = Cli(ebs ? Samples.Syn19 : Samples.Syn11, "--json", f);
        Assert.Equal(0, c);
        var text = File.ReadAllText(f);
        using var doc = JsonDocument.Parse(text);
        var a = doc.RootElement.GetProperty("analysis");
        Assert.True(a.GetProperty("findings").GetArrayLength() > 0);
        Assert.Equal(text.Length, JsonSerializer.Serialize(doc.RootElement).Length); // parse/serialize round trip is lossless
    }

    [Fact]
    public void ExitCode0_ShowsFormatNoteWarningsAndNotReliableHealth()
    {
        var path = Path.Combine(_tmp, "de-truncated.html");
        File.WriteAllText(path, Synthetic.Localise(Synthetic.AwrHtml(o => o.Truncate = true), NumStyle.German));
        var (c, o, _) = Cli(path, "--text");
        Assert.Equal(0, c);
        Assert.Contains("Numbers read as 1.234,56 (comma decimal, dot thousands)", o);
        Assert.Contains("WARNING:", o);
        Assert.Contains("End of Report", o);
        Assert.Contains("Health: not reliable", o);
        Assert.DoesNotContain("grade F", o);
    }

    [Fact]
    public void ExitCode0_HeaderHasNoEmptyInstanceParentheses()
    {
        var path = Path.Combine(_tmp, "noinst.html");
        File.WriteAllText(path, Synthetic.AwrHtml(o => o.Instance = ""));
        var (c, o, _) = Cli(path, "--text");
        Assert.Equal(0, c);
        Assert.Contains("AWR analysis: SYNTH 19.0.0.0.0", o);
        Assert.DoesNotContain("()", o.Split('\n').First(l => l.Contains("AWR analysis:")));
    }

    [Fact]
    public void JsonCarriesTheReliabilityFields()
    {
        var path = Path.Combine(_tmp, "short.html");
        var json = Path.Combine(_tmp, "short.json");
        File.WriteAllText(path, Synthetic.AwrHtml(o => { o.ElapsedText = "3.00 (mins)"; o.EndTime = "10-Mar-26 10:03:00"; }));
        var (c, _, _) = Cli(path, "--json", json);
        Assert.Equal(0, c);
        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        var a = doc.RootElement.GetProperty("analysis");
        Assert.False(a.GetProperty("health").GetProperty("reliable").GetBoolean());
        Assert.Equal("n/a", a.GetProperty("health").GetProperty("grade").GetString());
        Assert.True(a.GetProperty("dataQuality").GetProperty("alerts").GetArrayLength() > 0);
        Assert.Contains("dot decimal", a.GetProperty("meta").GetProperty("numberFormat").GetString());
        Assert.True(a.GetProperty("rootCauses").GetArrayLength() >= 0);
    }
}
