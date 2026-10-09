/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Hosting.Security;

public static class ApiKeyEndpointExtensions
{
    /// <summary>A route group whose endpoints all need the trusted-script key: <c>var keyed = app.MapGroup(string.Empty).RequireApiKey();</c> then <c>keyed.MapPost(...)</c>.</summary>
    public static RouteGroupBuilder RequireApiKey(this RouteGroupBuilder group) => group.AddEndpointFilter<ApiKeyFilter>();

    /// <summary>The group the key-gated routes of a file are mapped on; its routes keep their full paths.</summary>
    public static RouteGroupBuilder MapKeyedGroup(this WebApplication app) => app.MapGroup(string.Empty).RequireApiKey();
}
