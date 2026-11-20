// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Wissam HANNA

using System.Globalization;
using System.Text.RegularExpressions;
using Apodofos.Core.Export;

namespace Apodofos.Tests;

/// <summary>
/// Cheap regression tests for UI layout / usability fixes. They assert on the embedded web assets (no browser needed);
/// the browser-driven checks live in tools/ui-qa (see its README).
/// </summary>
public class UiLayoutTests
{
    private static string Css => DashboardBuilder.ReadAsset("app.css");
    private static string Js => DashboardBuilder.ReadAsset("app.js");
    private static string Index => DashboardBuilder.ReadAsset("index.html");

    // ---- tall tables / scrollbars far down the page ----

    [Fact]
    public void SqlAndScriptTabsFillTheVisiblePanelSoScrollbarsAndPagerStayOnScreen()
    {
        // the grid / script gets the room left in the panel (flex), never a fixed height taller than the window
        Assert.Contains(".panel.fit", Css);
        Assert.Contains(".fitbody", Css);
        Assert.Contains(".gridbox > .gridwrap", Css);
        Assert.DoesNotContain("max-height: min(68vh", Css);
        Assert.Matches(@"pre\.code\.scriptpre\s*\{[^}]*flex:\s*1 0 \d+px", Css);
        Assert.Matches(@"\.gridwrap\.sqlgrid\s*\{\s*max-height:\s*none", Css);
        // the panels opt in, and the SQL grid is built as a fitted grid
        Assert.Matches(@"classList\.toggle\('fit',\s*A\.topSql\.length > 0\)", Js);
        Assert.Matches(@"classList\.toggle\('fit',\s*n > 0\)", Js);
        Assert.Contains("fit: true", Js);
        Assert.Contains("opts.fit ? 'gridbox'", Js);
    }

    [Fact]
    public void SqlIdColumnStaysVisibleWhileScrollingSideways()
    {
        Assert.Matches(@"\.sqlgrid thead th:first-child[^{]*\{[^}]*position:\s*sticky;\s*left:\s*0", Css);
        Assert.Contains(".sqlgrid tr.sqlrow td:first-child", Css);
    }

    [Fact]
    public void FindingsToolbarIsNotStickyOnShortWindows()
    {
        // a four-row sticky toolbar used to take half of a 700 px window
        Assert.Matches(@"@media \(max-height: 8\d\dpx\)\s*\{\s*\.toolbar\s*\{\s*position:\s*static", Css);
    }

    // ---- long words, long titles ----

    [Fact]
    public void LongUnbrokenWordsWrapInsteadOfOverflowingThePanel()
    {
        Assert.Matches(@"\.panel\s*\{[^}]*overflow-wrap:\s*anywhere", Css);
        Assert.Matches(@"\.fhead\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\)\s*auto", Css); // 1fr alone grows with the longest word
        Assert.Contains(".hero-text { min-width: 0; }", Css);
    }

    [Fact]
    public void TruncatedHeaderTextCarriesATooltip()
    {
        Assert.Contains("$('appTitle').title = name", Js);
        Assert.Contains("$('appSub').title = $('appSub').textContent", Js);
    }

    // ---- duplicate ids (the logo is cloned into the empty state) ----

    [Fact]
    public void ClonedLogoGetsItsOwnIds()
    {
        Assert.Contains("function uniqueIds(", Js);
        Assert.Contains("uniqueIds(logo.cloneNode(true))", Js);
    }

    [Fact]
    public void IndexHtmlIdsAreUniqueAndEveryStaticLookupResolves()
    {
        var ids = Regex.Matches(Index, "\\bid=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Empty(ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key));
        var icon = DashboardBuilder.ReadAsset("icon.svg");
        var iconIds = Regex.Matches(icon, "\\bid=\"([^\"]+)\"").Select(m => m.Groups[1].Value);
        Assert.Empty(iconIds.Intersect(ids)); // the inline logo must not clash with page ids

        var known = new HashSet<string>(ids);
        foreach (Match m in Regex.Matches(Js, @"\bid:\s*'([^']+)'")) known.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(Js, @"\bid=""([^""]+)""")) known.Add(m.Groups[1].Value);
        var missing = Regex.Matches(Js, @"\$\('([A-Za-z][\w-]*)'\)").Select(m => m.Groups[1].Value).Distinct()
            .Where(id => !known.Contains(id) && !id.EndsWith('-')).ToList();
        Assert.True(missing.Count == 0, "app.js looks up ids that exist nowhere: " + string.Join(", ", missing));
    }

    // ---- targets and focus ----

    [Fact]
    public void ControlsMeetTheMinimumTargetSize()
    {
        Assert.Matches(@"\.btn\.sm, \.chipbtn\s*\{[^}]*min-height:\s*24px", Css);
        Assert.Matches(@"\.links a, \.linkbtn\s*\{\s*min-height:\s*24px", Css);
        Assert.Matches(@"\.grid th button\s*\{[^}]*min-height:\s*24px", Css);
        Assert.Matches(@"\.doc h4 a\s*\{[^}]*min-height:\s*24px", Css);
    }

    [Fact]
    public void ScrollbarCornerDoesNotShowAWhiteSquareInDarkTheme()
    {
        Assert.Contains("::-webkit-scrollbar-corner", Css);
    }

    // ---- colour contrast (WCAG AA) computed from the design tokens ----

    private static readonly string[] Sev = { "critical", "high", "medium", "low", "info", "ok" };

    private static Dictionary<string, string> Tokens(string block)
    {
        var d = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(block, @"--([\w-]+):\s*(#[0-9A-Fa-f]{6})\b")) d[m.Groups[1].Value] = m.Groups[2].Value;
        return d;
    }

    private static (Dictionary<string, string> Light, Dictionary<string, string> Dark) ThemeTokens()
    {
        var css = Css.Replace("\r\n", "\n");
        var light = css[css.IndexOf(":root {", StringComparison.Ordinal)..];
        light = light[..light.IndexOf("\n}", StringComparison.Ordinal)];
        var di = css.IndexOf(":root[data-theme=\"dark\"] {", StringComparison.Ordinal);
        var dark = css[di..];
        dark = dark[..dark.IndexOf("\n}", StringComparison.Ordinal)];
        return (Tokens(light), Tokens(dark));
    }

    private static double[] Rgb(string hex) => new[] { Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16) }.Select(v => (double)v).ToArray();
    private static double[] Mix(double[] fg, double[] bg, double a) => fg.Select((v, i) => v * a + bg[i] * (1 - a)).ToArray();
    private static double Lum(double[] c)
    {
        double F(double v) { v /= 255; return v <= .03928 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        return .2126 * F(c[0]) + .7152 * F(c[1]) + .0722 * F(c[2]);
    }
    private static double Ratio(double[] a, double[] b) { var x = Lum(a); var y = Lum(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeverityBadgeTextHasAaContrastOnItsTint(bool dark)
    {
        var (l, d) = ThemeTokens();
        var t = dark ? d : l;
        foreach (var surface in new[] { "surface", "surface2" })
            foreach (var s in Sev)
            {
                var fg = Rgb(t["sev-" + s]);
                var bg = Mix(fg, Rgb(t[surface]), .14); // .badge background is a 14% tint of the severity colour
                var r = Ratio(fg, bg);
                Assert.True(r >= 4.5, $"{(dark ? "dark" : "light")} sev-{s} on {surface}: contrast {r.ToString("0.00", CultureInfo.InvariantCulture)} < 4.5");
            }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BodyTextAndAccentTextHaveAaContrast(bool dark)
    {
        var (l, d) = ThemeTokens();
        var t = dark ? d : l;
        foreach (var (fg, bg) in new[] { ("text", "surface"), ("muted", "surface"), ("muted", "surface2"), ("muted", "bg"), ("accent-strong", "surface"), ("good", "surface2"), ("warn", "surface2"), ("bad", "surface2") })
        {
            var r = Ratio(Rgb(t[fg]), Rgb(t[bg]));
            Assert.True(r >= 4.5, $"{(dark ? "dark" : "light")} --{fg} on --{bg}: contrast {r.ToString("0.00", CultureInfo.InvariantCulture)} < 4.5");
        }
        // tab counters: accent-strong on the 10% accent tint of surface2
        var tint = Mix(Rgb(t["accent"]), Rgb(t["surface2"]), .10);
        Assert.True(Ratio(Rgb(t["accent-strong"]), tint) >= 4.5);
        Assert.Matches(@"\.tabs \.count\s*\{[^}]*color:\s*var\(--accent-strong\)", Css);
    }

    // ---- security: report-derived text is never parsed as HTML ----

    [Fact]
    public void UiNeverAssignsInnerHtml()
    {
        Assert.DoesNotMatch(@"\.innerHTML\s*=|\.outerHTML\s*=|insertAdjacentHTML|document\.write\(", Js);
        Assert.DoesNotMatch(@"\.innerHTML\s*=|\.outerHTML\s*=|insertAdjacentHTML|document\.write\(", DashboardBuilder.ReadAsset("charts.js"));
    }

    [Theory]
    [InlineData(".toplist svg")]
    [InlineData(".plan svg")]
    [InlineData(".linkbtn svg")]
    [InlineData(".search svg")]
    [InlineData(".btn svg, .iconbtn svg")]
    public void EveryInlineIconContextHasAnExplicitSize(string selector)
    {
        // an icon is an <svg viewBox> with no width/height: without a CSS size it stretches to its row (a huge arrow)
        var rule = Css.Split('\n').FirstOrDefault(l => l.StartsWith(selector + " {", StringComparison.Ordinal)
                                                   && l.Contains("width:", StringComparison.Ordinal) && l.Contains("height:", StringComparison.Ordinal));
        Assert.True(rule != null, $"'{selector}' has no rule with a width and a height");
    }
}
