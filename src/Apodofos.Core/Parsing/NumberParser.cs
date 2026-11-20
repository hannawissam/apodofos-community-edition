// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Globalization;
using System.Text.RegularExpressions;

namespace Apodofos.Core.Parsing;

/// <summary>
/// Parses the number formats that appear in AWR reports, in any of the supported conventions (see <see cref="NumberConvention"/>):
///  "1,942.31", "1.942,31", "1 942,31" (space / NBSP / narrow NBSP), "1’942.31" (Swiss), "12,34,567.8" (Indian), ".7", "90.1K",
///  "13.4M", "-1.2", "2.01ms", "512.34us", "59.42 (mins)", "104,704M", "12,5 %".
/// The decimal separator is decided once per report (<see cref="NumberConventions"/>) and held in an ambient scope, so a
/// cell such as "1,234" is read as 1234 or as 1.234 by the report's convention, never by a guess on the single cell.
/// A cell whose grouping is not valid for the convention ("1,2,3", "12 3456") is not a number: null, never a wrong value.
/// Values with a time-unit suffix (ns/us/ms/s) are normalised to milliseconds. Magnitude suffixes K/M/G/T/P are multiples of 1000.
/// </summary>
public static partial class NumberParser
{
    [GeneratedRegex(@"^\s*(?<num>[-+]?(?:\d(?:[\d.,' ]*\d)?|[.,]\d+))(?<exp>[eE][-+]?\d+)?\s*(?<suf>[KMGTP]|ns|us|µs|ms|s|sec|secs)?\s*%?\s*(?:\((?<unit>[^)]{1,12})\))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();

    private static readonly AsyncLocal<NumberConvention?> Ambient = new();
    private static readonly AsyncLocal<bool> MixedAmbient = new();

    /// <summary>The convention in force for the report being parsed / analyzed (dot decimal when none was set).</summary>
    public static NumberConvention Current => Ambient.Value ?? NumberConvention.Invariant;

    /// <summary>True when a convention scope is already open (nested parsers reuse the outer decision).</summary>
    internal static bool HasScope => Ambient.Value is not null;

    /// <summary>Makes <paramref name="convention"/> the convention used by <see cref="Parse(string?)"/> until disposed.</summary>
    /// <param name="convention">The report's decimal convention.</param>
    /// <param name="mixedDecimals">True when the report itself uses both decimal characters (some databases print some columns with
    /// a decimal point and others with a decimal comma): a cell that is not valid under the convention but is valid as a decimal in
    /// the other style ("89,3" in a dot report, "823.63" in a comma report) is then read as that decimal. Never set for a report
    /// that uses one style only, so malformed numbers stay rejected there.</param>
    public static IDisposable Use(NumberConvention convention, bool mixedDecimals = false)
    {
        var previous = Ambient.Value;
        var previousMixed = MixedAmbient.Value;
        Ambient.Value = convention;
        MixedAmbient.Value = mixedDecimals;
        return new Scope(previous, previousMixed);
    }

    private sealed class Scope(NumberConvention? previous, bool previousMixed) : IDisposable
    {
        public void Dispose() { Ambient.Value = previous; MixedAmbient.Value = previousMixed; }
    }

    public static double? Parse(string? text) => Parse(text, Current);

    public static double? Parse(string? text, NumberConvention convention) => ParseCore(text, convention, out _);

    private static double? ParseCore(string? text, NumberConvention convention, out string unit)
    {
        unit = "";
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Replace(' ', ' ').Replace(' ', ' ').Replace(' ', ' ').Replace('’', '\'').Replace('′', '\'').Trim();
        if (s.Length == 0 || s.StartsWith('#')) return null;
        var m = NumberRegex().Match(s);
        if (!m.Success) return null;
        var canon = Canonical(m.Groups["num"].Value, convention == NumberConvention.European ? ',' : '.')
                    ?? (MixedAmbient.Value ? OtherDecimal(m.Groups["num"].Value, convention) : null);
        if (canon is null) return null;
        if (!double.TryParse(canon + m.Groups["exp"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
        unit = m.Groups["unit"].Value.Trim().ToLowerInvariant();
        return m.Groups["suf"].Value switch
        {
            "K" => v * 1e3,
            "M" => v * 1e6,
            "G" => v * 1e9,
            "T" => v * 1e12,
            "P" => v * 1e15,
            "ns" => v / 1e6,
            "us" or "µs" => v / 1e3,
            "s" or "sec" or "secs" => v * 1e3,
            _ => v
        };
    }

    /// <summary>
    /// Mixed-decimals reports only. A single separator followed by one or two digits cannot be a thousands group (those have
    /// exactly three digits), so under a dot convention "89,3" is 89.3 and under a comma convention "823.63" is 823.63.
    /// Anything else stays "not a number".
    /// </summary>
    private static string? OtherDecimal(string num, NumberConvention convention)
    {
        var other = convention == NumberConvention.European ? '.' : ',';
        var body = num.Trim();
        var sign = "";
        if (body.Length > 0 && (body[0] == '-' || body[0] == '+')) { sign = body[0] == '-' ? "-" : ""; body = body[1..]; }
        var at = body.IndexOf(other);
        if (at < 1 || body.IndexOf(other, at + 1) >= 0) return null;
        var intPart = body[..at];
        var frac = body[(at + 1)..];
        if (frac.Length is < 1 or > 2 || !frac.All(char.IsAsciiDigit) || !intPart.All(char.IsAsciiDigit)) return null;
        return sign + intPart + "." + frac;
    }

    /// <summary>
    /// Turns "-1 234,5", "1.234.567", "12,34,567.8" or "1’234.5" into "-1234.5", given the decimal separator.
    /// Every other separator is a thousands separator and must sit where one belongs (groups of 3, or the Indian 2-2-3).
    /// </summary>
    private static string? Canonical(string num, char dec)
    {
        var sign = "";
        var body = num;
        if (body.Length > 0 && body[0] is '-' or '+') { sign = body[0] == '-' ? "-" : ""; body = body[1..]; }
        if (body.Length == 0) return null;
        var decAt = body.IndexOf(dec);
        if (decAt >= 0 && body.IndexOf(dec, decAt + 1) >= 0) return null; // two decimal separators
        var intPart = decAt >= 0 ? body[..decAt] : body;
        var frac = decAt >= 0 ? body[(decAt + 1)..] : "";
        if (frac.Any(c => !char.IsAsciiDigit(c))) return null; // a group separator after the decimal point
        if (intPart.Length == 0) intPart = "0";
        char? g = null;
        foreach (var c in intPart)
            if (!char.IsAsciiDigit(c)) { if (g is null) g = c; else if (g != c) return null; }
        if (g is not null)
        {
            var groups = intPart.Split(g.Value);
            if (groups.Any(x => x.Length == 0 || x.Any(c => !char.IsAsciiDigit(c)))) return null;
            var rest = groups.Skip(1).ToArray();
            var standard = groups[0].Length is >= 1 and <= 3 && rest.All(x => x.Length == 3);
            var indian = groups.Length >= 3 && groups[0].Length is >= 1 and <= 2 && rest[..^1].All(x => x.Length == 2) && rest[^1].Length == 3;
            if (!standard && !indian) return null;
            intPart = string.Concat(groups);
        }
        return sign + intPart + (frac.Length > 0 ? "." + frac : "");
    }

    /// <summary>
    /// Formats a number the way the report being parsed prints it, so that values the parsers derive and store in cells
    /// ("0.##") are read back correctly under the same convention.
    /// </summary>
    public static string Format(double v, string format = "0.##")
    {
        var s = v.ToString(format, CultureInfo.InvariantCulture);
        return Current == NumberConvention.European ? s.Replace('.', ',') : s;
    }

    /// <summary>Parses a value and returns 0 when it is blank or non-numeric.</summary>
    public static double ParseOrZero(string? text) => Parse(text) ?? 0d;

    /// <summary>
    /// Parses a duration to minutes, honouring a unit word in any supported language: "(mins)", "(Min.)", "(Std.)", "(heures)",
    /// "(horas)", "(sec)". A bare number is minutes (the unit of the AWR Elapsed and DB Time rows).
    /// </summary>
    public static double? ParseMinutes(string? text)
    {
        var v = ParseCore(text, Current, out var unit);
        if (v is null) return null;
        if (unit.Length == 0) return v;
        if (Regex.IsMatch(unit, @"^(h|hr|hrs|hour|hours|std\.?|stunde|stunden|heure|heures|hora|horas|ora|ore|uur|uren)$")) return v * 60;
        if (Regex.IsMatch(unit, @"^(s|sec|secs|sec\.|second|seconds|sek\.?|sekunden|seg\.?|segundos|sec\.?|secondes|secondi|seconden)$")) return v / 60;
        return v;
    }

    /// <summary>Extracts the first number inside free text, e.g. "Host CPU (CPUs:    1 Cores: ...".</summary>
    public static double? FirstNumberIn(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = Regex.Match(text, @"[-+]?\d[\d.,]*");
        return m.Success ? Parse(m.Value.TrimEnd('.', ',')) : null;
    }
}
