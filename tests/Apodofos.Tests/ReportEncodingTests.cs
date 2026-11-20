// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text;
using Apodofos.Core.Parsing;
using Apodofos.Core.Providers;

namespace Apodofos.Tests;

/// <summary>Reports from non-English databases are often Windows-1252 with no declared charset ("Exécution PL/SQL").</summary>
public class ReportEncodingTests
{
    private static readonly Encoding Win1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    [Fact]
    public void Windows1252WithoutCharsetIsDecodedWithItsAccents()
    {
        var text = "<html><body>Exécution PL/SQL – Connexion et déconnexion de session €</body></html>";
        Assert.Equal(text, ReportTextDecoder.Decode(Win1252.GetBytes(text)));
    }

    [Fact]
    public void ValidUtf8StaysUtf8()
    {
        var text = "<html><body>Exécution 日本語</body></html>";
        Assert.Equal(text, ReportTextDecoder.Decode(new UTF8Encoding(false).GetBytes(text)));
        Assert.Equal(text, ReportTextDecoder.Decode(new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(text)).ToArray()));
    }

    [Fact]
    public void Utf16WithBomIsDecoded()
    {
        var text = "<html><body>déconnexion</body></html>";
        Assert.Equal(text, ReportTextDecoder.Decode(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray()));
        Assert.Equal(text, ReportTextDecoder.Decode(Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray()));
    }

    [Fact]
    public void ADeclaredCharsetIsHonouredWhenTheBytesAreNotUtf8()
    {
        var latin2 = Encoding.GetEncoding("iso-8859-2");
        var text = "<html><head><meta charset=\"iso-8859-2\"></head><body>Łódź – Gdańsk</body></html>".Replace("–", "-");
        Assert.Equal(text, ReportTextDecoder.Decode(latin2.GetBytes(text)));
    }

    [Fact]
    public void EmptyAndPlainAsciiAreFine()
    {
        Assert.Equal(string.Empty, ReportTextDecoder.Decode([]));
        Assert.Equal("abc", ReportTextDecoder.Decode("abc"u8.ToArray()));
    }

    [Fact]
    public void ALatin1ReportFileKeepsItsAccentsThroughTheWholePipeline()
    {
        var html = Synthetic.AwrHtml().Replace("Commit", "Validé (commit)");
        Assert.Contains("é", html);
        var path = Path.Combine(Path.GetTempPath(), "apodofos-latin1-" + Guid.NewGuid().ToString("N") + ".html");
        try
        {
            File.WriteAllBytes(path, Win1252.GetBytes(html));
            var viaProvider = ReportProviderRegistry.AnalyzeFile(path);
            var loaded = AwrReportLoader.LoadFile(path);
            Assert.DoesNotContain('�', loaded.SanitizedHtml);
            Assert.Contains("Validé", loaded.SanitizedHtml);
            Assert.Contains("Validé", string.Join("|", viaProvider.Analysis.Tables.SelectMany(t => t.Rows.SelectMany(r => r.Select(c => c?.ToString() ?? "")))));
        }
        finally { File.Delete(path); }
    }
}
