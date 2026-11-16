/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Accounts;

namespace WebCore.Tests.Accounts;

public class LocalReturnUrlTests
{
    [Theory]
    [InlineData("/quizzes", "/quizzes")]
    [InlineData("/quizzes/edit/abc?x=1&y=2", "/quizzes/edit/abc?x=1&y=2")]
    [InlineData("/host/abc?test=true", "/host/abc?test=true")]
    [InlineData("/", "/")]
    [InlineData("quizzes", "/quizzes")]
    [InlineData("  /quizzes  ", "/quizzes")]
    public void Sanitize_LocalPaths_ArePreserved(string input, string expected)
    {
        Assert.Equal(expected, LocalReturnUrl.Sanitize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("//evil.example")]
    [InlineData("//evil.example/path")]
    [InlineData("///evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("\\\\evil.example")]
    [InlineData("\\evil.example")]
    [InlineData("/quizzes\r\nSet-Cookie: x=1")]
    [InlineData("/quizzes\t//evil.example")]
    public void Sanitize_AnythingThatCouldLeaveTheSite_BecomesRoot(string? input)
    {
        Assert.Equal("/", LocalReturnUrl.Sanitize(input));
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://evil.example/x")]
    [InlineData("javascript:alert(1)")]
    public void Sanitize_AbsoluteUrls_BecomeALocalPath_NeverAnExternalTarget(string input)
    {
        var result = LocalReturnUrl.Sanitize(input);

        Assert.StartsWith("/", result);
        Assert.False(result.StartsWith("//", StringComparison.Ordinal));
        Assert.False(Uri.TryCreate(result, UriKind.Absolute, out var uri) && uri.Scheme != Uri.UriSchemeFile);
    }
}
