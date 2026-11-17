/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace WebCore.Hosting.Accounts;

/// <summary>
/// Carries the email a person just typed across one redirect (a refused sign-in, a code request, a registration that needs fixing) without writing it into
/// the URL, when <see cref="AccountOptions.KeepEmailsOutOfUrls"/> is on: request URLs are recorded by telemetry, and personal data does not belong in one.
/// The address travels in a sealed (data-protected), short-lived cookie, so the page cannot be made to show an address the server did not store.
/// Lifted from Better Trivia, where it began.
/// </summary>
public sealed class AccountFormMemory
{
    private const string Purpose = "AccountFormMemory.v1";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ITimeLimitedDataProtector _protector;
    private readonly AccountOptions _options;
    private readonly ILogger<AccountFormMemory> _logger;

    public AccountFormMemory(IDataProtectionProvider provider, AccountOptions options, ILogger<AccountFormMemory> logger)
    {
        _protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        _options = options;
        _logger = logger;
    }

    /// <summary>Stores the email for the next page in this browser for ten minutes.</summary>
    public void Remember(HttpContext http, string email) =>
        http.Response.Cookies.Append(_options.FormEmailCookieName, _protector.Protect(email, Lifetime), CookieOptions(http));

    /// <summary>The remembered email, or null when there is none, it has expired, or the cookie was altered (logged at debug level and otherwise ignored).</summary>
    public string? Recall(HttpContext http)
    {
        var sealedValue = http.Request.Cookies[_options.FormEmailCookieName];
        if (string.IsNullOrEmpty(sealedValue))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(sealedValue);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            _logger.LogDebug(ex, "Ignoring a remembered-email cookie that is expired or was altered");
            return null;
        }
    }

    /// <summary>Clears the remembered email once the person has signed in and it is no longer needed.</summary>
    public void Forget(HttpContext http) => http.Response.Cookies.Delete(_options.FormEmailCookieName, CookieOptions(http));

    /// <summary>The part of a redirect that carries <paramref name="email"/>: <c>&amp;email=...</c>, or nothing once the address has been remembered in the
    /// cookie instead (<see cref="AccountOptions.KeepEmailsOutOfUrls"/>).</summary>
    public string QueryPart(HttpContext http, string email)
    {
        if (!_options.KeepEmailsOutOfUrls)
        {
            return "&email=" + Uri.EscapeDataString(email);
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            Remember(http, email);
        }

        return string.Empty;
    }

    private static CookieOptions CookieOptions(HttpContext http) => new()
    {
        HttpOnly = true,
        Secure = http.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        MaxAge = Lifetime,
    };
}
