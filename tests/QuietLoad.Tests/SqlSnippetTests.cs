// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Text.RegularExpressions;
using QuietLoad.Core.Analysis;
using QuietLoad.Core.Providers;

namespace QuietLoad.Tests;

/// <summary>
/// Static review of every SQL snippet the rules emit (no database needed): structural sanity, non-Oracle syntax,
/// the ROWNUM-before-ORDER-BY trap, and version gating (FETCH FIRST and other 12c+ syntax must not appear for older releases).
/// </summary>
public class SqlSnippetTests
{
    private static IEnumerable<string> Reports()
    {
        foreach (var f in Samples.ExampleFiles()) yield return f;
        foreach (var f in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "TestData"))) yield return f;
        foreach (var f in LocalCorpusTests.CorpusFiles()) yield return f;
    }

    private static readonly string[] NonOracle = ["ILIKE", "STRING_AGG", "GETDATE(", "ISNULL(", "IFNULL(", "NOW()", " LIMIT ", "SELECT TOP ", "LEN(", "::"];
    // 12c release 1 and later; must not be offered for older reports unless the text says so
    private static readonly Regex Needs12 = new(@"\bFETCH\s+FIRST\b|\bOFFSET\s+\d+\s+ROWS\b|\bCON_ID\b|\bV\$CONTAINERS\b|\bCDB_\w+|\bMOVE\s+DATAFILE\b|\bCROSS\s+APPLY\b|\bOUTER\s+APPLY\b|\bLATERAL\b|\bAPPROX_(COUNT_DISTINCT|MEDIAN|PERCENTILE)\w*\s*\(|\bON\s+OVERFLOW\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Problems of one snippet; empty when it passes.</summary>
    internal static List<string> Lint(SqlSnippet s, int major)
    {
        var p = new List<string>();
        var code = s.Code;
        // strip string literals and comments for structural checks
        var bare = Regex.Replace(Regex.Replace(code, @"'(?:[^']|'')*'", "''"), @"--[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
        if (bare.Count(c => c == '(') != bare.Count(c => c == ')')) p.Add("unbalanced parentheses");
        if (code.Count(c => c == '\'') % 2 != 0) p.Add("unbalanced quotes");
        if (string.IsNullOrWhiteSpace(code)) p.Add("empty");
        var trimmed = bare.TrimEnd();
        var isPlsql = Regex.IsMatch(trimmed, @"^\s*(BEGIN|DECLARE|EXEC|@)", RegexOptions.IgnoreCase); // PL/SQL blocks and SQL*Plus scripts
        if (!trimmed.EndsWith(';') && !trimmed.EndsWith('/') && !isPlsql) p.Add("statement does not end with ;");
        foreach (var bad in NonOracle) if (bare.Contains(bad, StringComparison.OrdinalIgnoreCase)) p.Add($"non-Oracle syntax '{bad.Trim()}'");
        if (Regex.IsMatch(bare, @"\bSELECT\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(bare, @"\bFROM\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(bare, @"\bDUAL\b", RegexOptions.IgnoreCase) && !isPlsql)
            p.Add("SELECT without FROM");

        // ROWNUM filters are applied before ORDER BY: the ORDER BY must be inside an inline view
        var rn = Regex.Match(bare, @"\bWHERE\s+ROWNUM\s*<=?\s*\d+", RegexOptions.IgnoreCase);
        if (rn.Success)
        {
            var before = bare[..rn.Index];
            var afterWhere = bare[rn.Index..];
            if (Regex.IsMatch(afterWhere, @"\bORDER\s+BY\b", RegexOptions.IgnoreCase)) p.Add("ORDER BY after WHERE ROWNUM (sorts after limiting)");
            if (!Regex.IsMatch(before, @"\bFROM\s*\(", RegexOptions.IgnoreCase) && Regex.IsMatch(before, @"\bORDER\s+BY\b", RegexOptions.IgnoreCase)) p.Add("ORDER BY and ROWNUM at the same level");
            if (Regex.IsMatch(before, @"\bFROM\s*\(", RegexOptions.IgnoreCase) && !Regex.IsMatch(before, @"\bORDER\s+BY\b", RegexOptions.IgnoreCase)) p.Add("ROWNUM wrapper without ORDER BY inside");
        }

        // version gating
        var caution = s.Caution ?? "";
        if (major is > 0 and < 12 && Needs12.IsMatch(bare))
            p.Add("12c+ syntax offered for a pre-12c report (" + Needs12.Match(bare).Value + ")");
        if (Regex.IsMatch(bare, @"\bFETCH\s+FIRST\b", RegexOptions.IgnoreCase) && major == 0 && !caution.Contains("12", StringComparison.Ordinal) && !code.Contains("12c", StringComparison.OrdinalIgnoreCase))
            p.Add("FETCH FIRST without a 12c note on an unknown release");
        // a caution that suggests FETCH FIRST must say "12c" (the code itself uses ROWNUM or nothing)
        if (caution.Contains("FETCH FIRST", StringComparison.OrdinalIgnoreCase) && !caution.Contains("12", StringComparison.Ordinal))
            p.Add("caution suggests FETCH FIRST without naming the release (12c)");
        return p;
    }

    [Fact]
    public void LinterCatchesKnownBadPatterns()
    {
        Assert.Contains(Lint(new("t", "SELECT a FROM t WHERE ROWNUM <= 5 ORDER BY a;"), 19), x => x.Contains("ORDER BY after WHERE ROWNUM"));
        Assert.Contains(Lint(new("t", "SELECT a FROM t FETCH FIRST 5 ROWS ONLY;"), 11), x => x.Contains("12c+"));
        Assert.Contains(Lint(new("t", "SELECT a FROM t LIMIT 5;"), 19), x => x.Contains("non-Oracle"));
        Assert.Contains(Lint(new("t", "SELECT (a FROM t;"), 19), x => x.Contains("parentheses"));
        Assert.Empty(Lint(new("t", "SELECT * FROM (SELECT a FROM t ORDER BY a) WHERE ROWNUM <= 5;"), 11));
        Assert.Empty(Lint(new("t", "SELECT a FROM t ORDER BY a FETCH FIRST 5 ROWS ONLY;"), 19));
    }

    [Fact]
    public void EverySqlSnippetPassesTheLint()
    {
        var problems = new SortedSet<string>();
        var count = 0;
        foreach (var f in Reports())
        {
            AnalysisResult a;
            try { a = ReportProviderRegistry.AnalyzeFile(f).Analysis; }
            catch (ReportFormatException) { continue; }
            var major = int.TryParse(a.Meta.Release.Split('.', 2)[0], out var m) ? m : 0;
            foreach (var finding in a.Findings)
                foreach (var s in finding.Sql)
                {
                    count++;
                    foreach (var p in Lint(s, major)) problems.Add($"[{finding.Title[..Math.Min(50, finding.Title.Length)]}] '{s.Title}': {p}");
                }
        }
        Assert.True(count > 0, "no SQL snippets were produced");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
