/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Accessibility;

namespace WebCore.Tests.Accessibility;

public class CssContrastTests
{
    [Fact]
    public void BlackOnWhiteIsTheMaximumAndSimilarGreysFail()
    {
        Assert.Equal(21, CssContrast.Ratio("#000000", "#ffffff"), 1);
        Assert.True(CssContrast.Ratio("#777777", "#ffffff") > 4.4 && CssContrast.Ratio("#777777", "#ffffff") < 4.6);
    }

    [Fact]
    public void ARuleWithAWeakPairIsReportedWithItsSelectorAndRatio()
    {
        var failures = CssContrast.FindFailures(".tile { color: #fff; background: #e63946; } .ok { color: #000; background: #fff; }");

        var tile = Assert.Single(failures.Where(f => f.Theme == "light"));
        Assert.Equal(".tile", tile.Selector);
        Assert.Equal("#ffffff", tile.Foreground);
        Assert.True(tile.Ratio < 4.5);
    }

    [Fact]
    public void TokensAreResolvedPerThemeSoADarkOverrideCanFailOnItsOwn()
    {
        const string css = """
            :root { --text: #111111; --bg: #ffffff; }
            @media (prefers-color-scheme: dark) { :root { --text: #555555; --bg: #444444; } }
            body { color: var(--text); background: var(--bg); }
            """;

        var failures = CssContrast.FindFailures(css);

        var dark = Assert.Single(failures);
        Assert.Equal("dark", dark.Theme);
        Assert.Equal("body", dark.Selector);
    }

    [Fact]
    public void WhatItCannotResolveIsLeftAlone()
    {
        const string css = ".a { color: #fff; background: linear-gradient(#fff, #eee); } .b { color: red; background: #fff; } .c { color: #fff; background: rgba(0,0,0,.5); } .d { color: var(--missing); background: #fff; }";

        Assert.Empty(CssContrast.FindFailures(css));
    }

    [Fact]
    public void ShortHexRgbAndFallbacksAreUnderstoodAndSelectorsCanBeIgnored()
    {
        const string css = ".a { color: #fff; background: rgb(230, 57, 70); } .b { color: var(--nope, #fff); background: #eee; } .skip-me { color: #fff; background: #fff; }";

        var failures = CssContrast.FindFailures(css, ignoreSelectors: new[] { "skip-me" });

        Assert.Equal(new[] { ".a", ".b" }, failures.Where(f => f.Theme == "light").Select(f => f.Selector));
    }
}
