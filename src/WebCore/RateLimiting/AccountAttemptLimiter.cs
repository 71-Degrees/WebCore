/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */



namespace WebCore.RateLimiting;

/// <summary>
/// The ceilings on registration, password sign-in and sign-in-code requests. Three layers, narrowest first, so one abusive client uses up its own allowance and not everyone's:
/// <list type="bullet">
/// <item><description>per address - 10 attempts in 10 minutes from one address (a household or an office shares one comfortably);</description></item>
/// <item><description>per account - 10 password attempts in 15 minutes against one email address, so a password cannot be guessed at speed from many addresses (the cost is that someone can briefly slow
/// a particular person's sign-in; the response is the same generic failure either way, so it never reveals whether an account exists);</description></item>
/// <item><description>global - 20 registration and password attempts in 10 minutes across everyone, the last backstop.</description></item>
/// </list>
/// The narrow limits are checked first and a refusal there does not spend the global allowance, which is what used to let one noisy client lock every real person out.
/// </summary>
public sealed class AccountAttemptLimiter
{
    /// <summary>The shared ceiling per 10 minutes when an app does not choose its own.</summary>
    public const int DefaultGlobalMax = 20;

    private readonly SlidingWindowRateLimiter _global;
    private readonly KeyedRateLimiter _perAddress;
    private readonly KeyedRateLimiter _perAccount;

    /// <summary>
    /// <paramref name="scale"/> multiplies every ceiling (1, the default, is what runs in production). A development or test site raises it so that a whole suite of browser tests, which all come from one address, is not
    /// <paramref name="globalMax"/> replaces the shared ceiling of 20 per 10 minutes (before scaling); an app with a real audience raises it so a handful of noisy clients cannot lock everyone out, since the per-address and per-account limits are the real protection.
    /// held back by the limits that protect a real site.
    /// </summary>
    public AccountAttemptLimiter(int scale = 1, int? globalMax = null)
    {
        var factor = Math.Max(1, scale);
        _global = new SlidingWindowRateLimiter(maxInWindow: Math.Max(1, globalMax ?? DefaultGlobalMax) * factor, window: TimeSpan.FromMinutes(10));
        _perAddress = new KeyedRateLimiter(maxInWindow: 10 * factor, window: TimeSpan.FromMinutes(10));
        _perAccount = new KeyedRateLimiter(maxInWindow: 10 * factor, window: TimeSpan.FromMinutes(15));
    }

    /// <summary>A registration attempt: allowed when this address has attempts left and the shared ceiling has room.</summary>
    public bool TryAcquire(string? address) => TryAcquireAddress(address) && _global.TryAcquire();

    /// <summary>A password sign-in attempt for <paramref name="email"/>: the account's own allowance first, then the address's, then the shared ceiling.</summary>
    public bool TryAcquireLogin(string? address, string email) =>
        _perAccount.TryAcquire(email.Trim().ToLowerInvariant()) && TryAcquire(address);

    /// <summary>A request for, or a guess at, an emailed sign-in code: only the address's own allowance (the code service has its own per-email and per-browser limits behind this).</summary>
    public bool TryAcquireAddress(string? address) => string.IsNullOrEmpty(address) || _perAddress.TryAcquire(address);
}
