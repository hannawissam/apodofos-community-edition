// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text.RegularExpressions;

namespace Apodofos.Core.Analysis;

/// <summary>
/// Report files are untrusted input, and the generated SQL is meant to be copied into a database session. Every value taken
/// from a report goes through one of these before it is put into a statement: literals are escaped, identifiers, SQL_IDs and
/// numbers must look like what they claim to be, otherwise a SQL*Plus substitution variable is offered instead.
/// </summary>
public static partial class SqlSafe
{
    // an unquoted Oracle identifier: a letter, then letters, digits, _ $ #
    [GeneratedRegex(@"\A\p{L}[\p{L}\p{M}\p{Nd}_$#]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    // SQL_ID (13 letters and digits) or an older Statspack hash value
    [GeneratedRegex(@"\A[A-Za-z0-9]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SqlIdPattern();

    /// <summary>The text on one line: control characters and line separators become spaces, so it cannot leave a comment.</summary>
    public static string OneLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (!text.Any(IsBreak)) return text;
        return string.Create(text.Length, text, static (span, s) =>
        {
            for (var i = 0; i < s.Length; i++) span[i] = IsBreak(s[i]) ? ' ' : s[i];
        });
    }

    private static bool IsBreak(char c) => char.IsControl(c) || c is (char)0x2028 or (char)0x2029; // Unicode line and paragraph separators

    /// <summary>The content of a '...' string literal (the caller writes the surrounding quotes).</summary>
    public static string Literal(string? text) => OneLine(text).Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// An unquoted identifier, or <c>&amp;variable</c> when the value is not one. Pass a variable ending in a period when the
    /// identifier is followed by one (SQL*Plus consumes the first period).
    /// </summary>
    public static string Identifier(string? value, string variable) =>
        value is not null && IdentifierPattern().IsMatch(value) ? value : "&" + variable;

    /// <summary>A SQL_ID or hash value, or <c>&amp;sql_id</c>.</summary>
    public static string SqlId(string? value) =>
        value is not null && SqlIdPattern().IsMatch(value) ? value : "&sql_id";

    /// <summary>An unsigned whole number, or <c>&amp;variable</c>.</summary>
    public static string Digits(string? value, string variable) =>
        !string.IsNullOrEmpty(value) && value.Length <= 38 && value.All(char.IsAsciiDigit) ? value : "&" + variable;
}
