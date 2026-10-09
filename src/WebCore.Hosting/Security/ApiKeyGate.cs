/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Security.Cryptography;
using System.Text;

namespace WebCore.Hosting.Security;

/// <summary>
/// The trusted-script key (conventionally <c>Api:ImportKey</c>, sent as the <c>X-Api-Key</c> header) that guards every scripted endpoint: imports and syncs, the maintenance and operations endpoints.
/// Not configured, the endpoints stay off (503) rather than silently accepting unauthenticated writes. The key is never hardcoded - see the README.
/// </summary>
public sealed class ApiKeyGate(string? key, string notConfiguredMessage = "The scripted API is not configured.")
{
    /// <summary>Null when the caller may proceed, else the response to send straight back.</summary>
    public IResult? Check(HttpContext http)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Results.Content(
                notConfiguredMessage,
                "text/plain",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return http.Request.Headers.TryGetValue("X-Api-Key", out var providedKey) && KeysMatch(providedKey.ToString(), key)
            ? null
            : Results.Unauthorized();
    }

    /// <summary>Compared in constant time, so the time a refusal takes does not reveal how much of a guess was right.</summary>
    private static bool KeysMatch(string provided, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
}
