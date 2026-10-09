/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

using WebCore.Hosting.Security;

namespace WebCore.Hosting.Tests;

public class ApiKeyGateTests
{
    private static DefaultHttpContext WithKey(string? header)
    {
        var http = new DefaultHttpContext();
        if (header is not null)
        {
            http.Request.Headers["X-Api-Key"] = header;
        }

        return http;
    }

    [Fact]
    public void The_right_key_passes() => Assert.Null(new ApiKeyGate("secret").Check(WithKey("secret")));

    [Theory]
    [InlineData("wrong")]
    [InlineData("secre")]
    [InlineData("secret!")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_unauthorized(string? header) =>
        Assert.IsType<UnauthorizedHttpResult>(new ApiKeyGate("secret").Check(WithKey(header)));

    [Fact]
    public void With_no_key_configured_the_endpoints_stay_off()
    {
        var result = new ApiKeyGate(null).Check(WithKey("anything"));

        Assert.NotNull(result);
        Assert.IsNotType<UnauthorizedHttpResult>(result);
    }
}
