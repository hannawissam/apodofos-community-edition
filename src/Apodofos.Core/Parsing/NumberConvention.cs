// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text.RegularExpressions;
using Apodofos.Core.Model;

namespace Apodofos.Core.Parsing;

/// <summary>
/// The decimal separator of a report. Invariant = '.' (US/UK, Swiss, Indian, ...); European = ',' (DE/FR/ES/IT/NL/BR/RU/PL ...).
/// Every other separator ('.', ',', space, NBSP, narrow NBSP, apostrophe) is a thousands separator, including Indian 2-2-3 grouping.
/// </summary>
public enum NumberConvention { Invariant, European }

/// <summary>Result of looking at the numeric cells of one report.</summary>
/// <param name="Convention">The decimal convention chosen for the whole report.</param>
/// <param name="Ambiguous">True when the evidence is conflicting or missing: the choice may be wrong.</param>
/// <param name="GroupStyle">Thousands separator seen most: comma, dot, space, apostrophe, indian or "" when none was seen.</param>
/// <param name="Note">Human-readable statement of the detected format ("Numbers read as 1.234,56 (comma decimal, dot thousands)").</param>
public sealed record NumberConventionDetection(NumberConvention Convention, bool Ambiguous, int EuropeanVotes, int InvariantVotes, string GroupStyle, string Note, bool NlsUsed);

/// <summary>
/// Decides the number convention once per REPORT, by voting across every numeric cell: patterns only one convention can produce
/// ("1.942,31", "12,5", "0,25" vs "1,234,567.8", "12,34,567.8", "1’234.5") vote strongly, values with 1-2 decimals (the shape of
/// percentages, ratios and per-second rates) vote weakly, and an NLS_NUMERIC_CHARACTERS value printed by the report adds a vote.
/// Cells such as "1,234" carry no vote of their own: they are read by the report's convention. Conflicting evidence, or only
/// ambiguous cells, is flagged <c>Ambiguous</c> (data quality Poor, no severity escalation).
/// </summary>
public static partial class NumberConventions
{
    // a whitespace/markup-delimited token (or a whole HTML cell) made only of digits and separators, plus an optional unit suffix
    [GeneratedRegex(@"(?<=^|[\s>])[-+]?\d[\d.,'’\u00a0\u202f]*(?=[KMGT%]?(?:$|[\s<]))", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    // a whole HTML cell whose thousands separator is an ordinary space: "<td>1 234 567,8</td>"
    [GeneratedRegex(@">\s*[-+]?(?<n>\d{1,3}(?: \d{3})+(?:[.,]\d+)?)\s*(?:%|[KMGT])?\s*<", RegexOptions.CultureInvariant)]
    private static partial Regex SpacedCell();

    [GeneratedRegex(@"nls_numeric_characters(?:[^<>\w]{0,12}|</t[dh]>\s*<t[dh][^>]*>\s*)[""']?(?<dec>[.,])(?<grp>[.,'\s\u00a0]?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Nls();

    // strong evidence for a comma decimal
    [GeneratedRegex(@"^\d{1,3}(\.\d{3})+,\d+$")] private static partial Regex EuroDotGroups();
    [GeneratedRegex(@"^\d{1,3}( \d{3})+,\d+$")] private static partial Regex EuroSpaceGroups();
    [GeneratedRegex(@"^\d{1,3}('\d{3})+,\d+$")] private static partial Regex EuroApostropheGroups();
    [GeneratedRegex(@"^\d{1,3}(\.\d{3}){2,}$")] private static partial Regex EuroDotIntegers();
    [GeneratedRegex(@"^0,\d+$")] private static partial Regex EuroLeadingZero();
    // strong evidence for a dot decimal
    [GeneratedRegex(@"^\d{1,3}(,\d{3})+\.\d+$")] private static partial Regex InvCommaGroups();
    [GeneratedRegex(@"^\d{1,3}( \d{3})+\.\d+$")] private static partial Regex InvSpaceGroups();
    [GeneratedRegex(@"^\d{1,3}('\d{3})+\.\d+$")] private static partial Regex InvApostropheGroups();
    [GeneratedRegex(@"^\d{1,2}(,\d{2})+,\d{3}(\.\d+)?$")] private static partial Regex IndianGroups();
    [GeneratedRegex(@"^\d{1,3}(,\d{3}){2,}$")] private static partial Regex InvCommaIntegers();
    [GeneratedRegex(@"^0?\.\d+$")] private static partial Regex InvLeadingZero();
    // weak evidence: one or two decimals, the shape of percentages, ratios and per-second rates
    [GeneratedRegex(@"^\d+,\d{1,2}$")] private static partial Regex EuroShort();
    [GeneratedRegex(@"^\d+\.\d{1,2}$")] private static partial Regex InvShort();
    // no evidence: "1,234" / "1.234" read by the report's convention
    [GeneratedRegex(@"^\d{1,3}[.,]\d{3}$")] private static partial Regex Lone();

    public static NumberConventionDetection Detect(string? content)
    {
        if (string.IsNullOrEmpty(content)) return Describe(NumberConvention.Invariant, false, 0, 0, new(), 0, false);
        content = content.Replace("&nbsp;", " ").Replace("&#160;", " ").Replace("&#8239;", " ").Replace("&#x202f;", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("&rsquo;", "'").Replace("&#8217;", "'").Replace("&#39;", "'").Replace("&apos;", "'");
        if (content.Length > 1_500_000) // a large report is voted on by three slices: the vote converges long before that
            content = content[..500_000] + "\n" + content.Substring(content.Length / 2 - 250_000, 500_000) + "\n" + content[^500_000..];
        int euro = 0, inv = 0, lone = 0;
        var groups = new Dictionary<string, int>();
        void G(string k, int n = 1) => groups[k] = groups.GetValueOrDefault(k) + n;

        foreach (Match m in Token().Matches(content))
        {
            var t = m.Value.TrimStart('-', '+').Replace('\u00a0', ' ').Replace('\u202f', ' ').Replace('’', '\'');
            if (EuroDotGroups().IsMatch(t)) { euro += 3; G("dot"); }
            else if (EuroSpaceGroups().IsMatch(t)) { euro += 3; G("space"); }
            else if (EuroApostropheGroups().IsMatch(t)) { euro += 3; G("apostrophe"); }
            else if (EuroDotIntegers().IsMatch(t)) { euro += 2; G("dot"); }
            else if (EuroLeadingZero().IsMatch(t)) euro += 2;
            else if (InvCommaGroups().IsMatch(t)) { inv += 3; G("comma"); }
            else if (InvSpaceGroups().IsMatch(t)) { inv += 3; G("space"); }
            else if (InvApostropheGroups().IsMatch(t)) { inv += 3; G("apostrophe"); }
            else if (IndianGroups().IsMatch(t)) { inv += 3; G("indian"); }
            else if (InvCommaIntegers().IsMatch(t)) { inv += 2; G("comma"); }
            else if (InvLeadingZero().IsMatch(t)) inv += 2;
            else if (EuroShort().IsMatch(t)) euro++;
            else if (InvShort().IsMatch(t)) inv++;
            else if (Lone().IsMatch(t)) lone++;
        }
        foreach (Match m in SpacedCell().Matches(content))
        {
            var n = m.Groups["n"].Value;
            G("space");
            if (Regex.IsMatch(n, @",\d+$")) euro += 3;
            else if (Regex.IsMatch(n, @"\.\d+$")) inv += 3;
        }

        // NLS_NUMERIC_CHARACTERS printed by the report: the database's own statement of the decimal character
        var nlsUsed = false;
        var nls = Nls().Match(content);
        if (nls.Success)
        {
            nlsUsed = true;
            if (nls.Groups["dec"].Value == ",") { euro += 10; var g = nls.Groups["grp"].Value; if (g == ".") G("dot", 0); }
            else inv += 10;
        }

        if (euro == 0 && inv == 0)
        {
            // only cells like "1,234": nothing says which character is the decimal one
            return Describe(NumberConvention.Invariant, lone >= 3, 0, 0, groups, lone, false);
        }
        NumberConvention convention;
        bool ambiguous;
        if (inv == 0) { convention = NumberConvention.European; ambiguous = false; }
        else if (euro == 0) { convention = NumberConvention.Invariant; ambiguous = false; }
        else
        {
            var (major, minor) = euro > inv ? (euro, inv) : (inv, euro);
            convention = euro > inv ? NumberConvention.European : NumberConvention.Invariant;
            // a few stray tokens in a long report (a SQL literal such as 1,5) must not make it ambiguous
            ambiguous = !(major >= 20 && minor * 20 <= major);
        }
        return Describe(convention, ambiguous, euro, inv, groups, lone, nlsUsed);
    }

    private static NumberConventionDetection Describe(NumberConvention c, bool ambiguous, int euro, int inv, Dictionary<string, int> groups, int lone, bool nls)
    {
        var style = groups.Count == 0 ? "" : groups.OrderByDescending(kv => kv.Value).First().Key;
        if (style == "" || (c == NumberConvention.European && style == "comma")) style = c == NumberConvention.European ? "dot" : "comma";
        var sample = (c, style) switch
        {
            (NumberConvention.Invariant, "indian") => "12,34,567.89 (dot decimal, Indian grouping)",
            (NumberConvention.Invariant, "space") => "1 234.56 (dot decimal, space thousands)",
            (NumberConvention.Invariant, "apostrophe") => "1’234.56 (dot decimal, apostrophe thousands)",
            (NumberConvention.Invariant, _) => "1,234.56 (dot decimal, comma thousands)",
            (NumberConvention.European, "space") => "1 234,56 (comma decimal, space thousands)",
            (NumberConvention.European, "apostrophe") => "1’234,56 (comma decimal, apostrophe thousands)",
            (NumberConvention.European, _) => "1.234,56 (comma decimal, dot thousands)",
            _ => "1,234.56 (dot decimal, comma thousands)"
        };
        var note = ambiguous
            ? $"The number format could not be determined safely ({euro} indicators for a comma decimal, {inv} for a dot decimal" + (lone > 0 && euro + inv == 0 ? $", {lone} values such as 1,234 that fit both" : "") + $"); numbers were read as {sample}."
            : $"Numbers read as {sample}" + (nls ? " (confirmed by NLS_NUMERIC_CHARACTERS)" : "") + ".";
        return new(c, ambiguous, euro, inv, style, note, nls);
    }

    /// <summary>
    /// Opens the convention scope for one parse call: detects the convention from the raw content unless an outer
    /// parser already decided (the Statspack parser re-uses the AWR text parser on slices of the same report).
    /// </summary>
    internal static Parsed Begin(string? content)
    {
        if (NumberParser.HasScope) return new Parsed(null, null);
        var d = Detect(content);
        return new Parsed(d, NumberParser.Use(d.Convention, IsMixed(d)), IsMixed(d));
    }

    /// <summary>Real evidence for BOTH decimal characters in one report (a few stray tokens do not count).</summary>
    internal static bool IsMixed(NumberConventionDetection d) => d.EuropeanVotes >= 5 && d.InvariantVotes >= 5;

    internal sealed class Parsed(NumberConventionDetection? detection, IDisposable? scope, bool mixed = false) : IDisposable
    {
        /// <summary>Records the decision (and the raw-content integrity facts) on the report.</summary>
        public void Apply(AwrReport report, string? content)
        {
            if (detection is null) return;
            report.NumberConvention = detection.Convention;
            report.NumberConventionAmbiguous = detection.Ambiguous;
            report.NumberVotes = (detection.EuropeanVotes, detection.InvariantVotes);
            report.NumberFormatNote = detection.Note + (mixed ? " This report prints some columns with a decimal point and others with a decimal comma; both were read as decimals." : "");
            ReportIntegrity.InspectRaw(report, content);
        }

        public void Dispose() => scope?.Dispose();
    }
}
