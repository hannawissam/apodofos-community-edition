// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text;
using System.Text.RegularExpressions;

namespace Apodofos.Core.Parsing;

/// <summary>
/// Turns the bytes of a report file into text without damaging accented characters. Oracle writes reports in the database
/// server's character set, so a French, German or Spanish AWR is often Windows-1252 / ISO-8859-1 with no declared charset,
/// and reading it as UTF-8 prints "Ex�cution". Order: byte-order mark, then strict UTF-8 (valid UTF-8 is almost never
/// anything else), then a charset declared in the first bytes (&lt;meta charset&gt; or an XML encoding), then Windows-1252.
/// </summary>
public static partial class ReportTextDecoder
{
    static ReportTextDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [GeneratedRegex(@"(?:charset|encoding)\s*=\s*[""']?\s*([A-Za-z0-9_\-:.]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeclaredCharset();

    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0) return string.Empty;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { /* not UTF-8: look for a declared charset */ }

        var enc = Declared(bytes) ?? Encoding.GetEncoding(1252);
        return enc.GetString(bytes);
    }

    private static Encoding? Declared(byte[] bytes)
    {
        var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        foreach (Match m in DeclaredCharset().Matches(head))
        {
            var name = m.Groups[1].Value.Trim();
            if (name.Equals("utf-8", StringComparison.OrdinalIgnoreCase) || name.Equals("utf8", StringComparison.OrdinalIgnoreCase)) continue; // the bytes are not valid UTF-8
            try { return Encoding.GetEncoding(name); }
            catch (ArgumentException) { /* unknown name: try the next declaration */ }
        }
        return null;
    }

    /// <summary>Reads a file (shared read/write, so a report still open in another program can be read) and decodes it.</summary>
    public static string ReadFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return Decode(ms.ToArray());
    }
}
