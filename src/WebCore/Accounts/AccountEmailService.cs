/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using WebCore.Email;
using WebCore.RateLimiting;

namespace WebCore.Accounts;

public enum EmailRequestStatus
{
    /// <summary>A code was sent (or, in local development, is shown on screen).</summary>
    Sent,
    InvalidEmail,
    NotSignedIn,
    RateLimited,

    /// <summary>The server has no email delivery configured.</summary>
    Unavailable,

    /// <summary>The mail provider refused or timed out; nothing was sent and the code was discarded.</summary>
    SendFailed,
}

public enum CodeCheckStatus
{
    Confirmed,
    WrongCode,

    /// <summary>No code is pending, or it has expired.</summary>
    Expired,
    TooManyAttempts,
    NotSignedIn,

    /// <summary>Changing email only: another account already uses that address.</summary>
    EmailInUse,

    /// <summary>The account is an Admin and has a password, so it must sign in with it - an emailed code alone
    /// is not enough to open an account that can manage other accounts.</summary>
    UsePassword,
}

/// <param name="DevCode">Only set when the service was told to expose codes (local development with
/// no email configured); always null in production.</param>
public record EmailRequestResult(EmailRequestStatus Status, string? DevCode = null);

/// <param name="User">The account that was signed in, created or updated by a confirmed code.</param>
/// <param name="IsNewAccount">True when this was the first confirmation of that email, so an account was created.</param>
public record AccountCodeResult(CodeCheckStatus Status, User? User = null, bool IsNewAccount = false);

/// <summary>How an app's code emails are sent and limited.</summary>
/// <param name="BrandName">The site's name in the email subject and as the account label ("Your CrateDigger code").</param>
/// <param name="DeliveryConfigured">True when codes are really emailed (a provider is configured and, in development, sending was asked for).</param>
/// <param name="ExposeCodeForDevelopment">Local development only: the code is shown on screen instead of emailed.</param>
public sealed record AccountEmailOptions(
    string BrandName,
    bool DeliveryConfigured,
    bool ExposeCodeForDevelopment = false,
    int MaxPerClientPerHour = AccountEmailService.DefaultMaxPerClientPerHour,
    int MaxPerHour = AccountEmailService.DefaultMaxPerHour,
    int MaxPerDay = AccountEmailService.DefaultMaxPerDay);

/// <summary>
/// Email is how an account proves its address. Confirming a six-digit code either signs into the account for that
/// address (creating it the first time, with no password) or, for an account that is already signed in, confirms - or
/// changes - its email. A code is single-use, expires after ten minutes, allows five wrong guesses and is only ever
/// kept as a salted hash. Sending is rate-limited per address, per browser and overall, so the mail account can
/// neither be abused nor used up.
/// </summary>
public sealed class AccountEmailService : IDisposable
{
    private const int CodeLifetimeMinutes = 10;
    private const int MaxAttempts = 5;
    private const int MaxRequestsPerHour = 5;
    private const int MaxTrackedKeys = 5000;

    // Ceilings that protect the mail account itself, whatever a caller does: one browser can't ask for many codes,
    // and all code emails together stay well inside a personal mailbox's daily sending allowance (about 500),
    // leaving the rest for error alerts and feedback notifications.
    public const int DefaultMaxPerClientPerHour = 10;
    public const int DefaultMaxPerHour = 60;
    public const int DefaultMaxPerDay = 200;

    private sealed class Pending
    {
        public string Email { get; init; } = string.Empty;

        public string Salt { get; init; } = string.Empty;

        public string CodeHash { get; init; } = string.Empty;

        public DateTimeOffset ExpiresAt { get; init; }

        public int Attempts { get; set; }
    }

    private readonly IUserRepository _users;
    private readonly IUserProfileRepository _profiles;
    private readonly IEmailSender _email;
    private readonly AccountRoles _roles;
    private readonly string _brand;
    private readonly bool _deliveryConfigured;
    private readonly bool _exposeCode;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly ConcurrentDictionary<string, SlidingWindowRateLimiter> _limiters = new();
    private readonly SlidingWindowRateLimiter _hourlyCap;
    private readonly SlidingWindowRateLimiter _dailyCap;
    private readonly int _maxPerClientPerHour;
    private readonly Action<Exception>? _onSendFailure;

    // One account per email, so the check-then-write of a confirmation must not interleave with another one.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public AccountEmailService(
        IUserRepository users,
        IUserProfileRepository profiles,
        IEmailSender email,
        AccountRoles roles,
        AccountEmailOptions options,
        Func<DateTimeOffset>? now = null,
        Action<Exception>? onSendFailure = null)
    {
        _users = users;
        _profiles = profiles;
        _email = email;
        _roles = roles;
        _brand = options.BrandName;
        _deliveryConfigured = options.DeliveryConfigured;
        _exposeCode = options.ExposeCodeForDevelopment;
        _now = now ?? TimeProvider.System.GetUtcNow;
        _onSendFailure = onSendFailure;
        _maxPerClientPerHour = options.MaxPerClientPerHour;
        _hourlyCap = new SlidingWindowRateLimiter(options.MaxPerHour, TimeSpan.FromHours(1));
        _dailyCap = new SlidingWindowRateLimiter(options.MaxPerDay, TimeSpan.FromDays(1));
    }

    public static string? NormalizeEmail(string? raw)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 254 || !MailAddress.TryCreate(trimmed, out var address)
            || !string.Equals(address.Address, trimmed, StringComparison.OrdinalIgnoreCase)
            || !address.Host.Contains('.'))
        {
            return null;
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>"sa***@example.com": the first two letters and the domain, enough to tell two players with the
    /// same display name apart wherever an address is shown publicly, without publishing it.</summary>
    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0)
        {
            return "***";
        }

        var shown = at <= 2 ? 1 : 2;
        return email[..shown] + "***" + email[at..];
    }

    /// <summary>Signing in by code, step one: sends a code to the address.
    /// <paramref name="clientKey"/> identifies the requesting browser and must be the same one used to confirm.</summary>
    public Task<EmailRequestResult> RequestSignInCodeAsync(string? rawEmail, string clientKey, CancellationToken cancellationToken = default)
    {
        var email = NormalizeEmail(rawEmail);
        return email is null || string.IsNullOrWhiteSpace(clientKey)
            ? Task.FromResult(new EmailRequestResult(EmailRequestStatus.InvalidEmail))
            : SendCodeAsync("signin|" + Hash(email) + "|" + clientKey, email, "sign in to", _brand, clientKey, cancellationToken);
    }

    /// <summary>Step two: the code from the email. Returns the account for that address - created, without a password,
    /// the first time - with its email marked confirmed. The caller signs the browser in.
    /// <paramref name="desiredName"/> becomes the new account's display name when it is a valid name.</summary>
    public async Task<AccountCodeResult> ConfirmSignInAsync(string? rawEmail, string clientKey, string? code, string? desiredName = null, CancellationToken cancellationToken = default)
    {
        var (status, confirmedEmail) = CheckAddressCode("signin", rawEmail, clientKey, code);
        if (status != CodeCheckStatus.Confirmed)
        {
            return new AccountCodeResult(status);
        }

        var email = confirmedEmail!;
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var user = await _users.GetByEmailAsync(email, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                user = new User
                {
                    Email = email,
                    DisplayName = DisplayNames.IsValid(desiredName) ? DisplayNames.Tidy(desiredName) : DefaultDisplayName(email),
                    EmailVerifiedAt = _now(),
                };
                user = await _users.CreateAsync(user, cancellationToken).ConfigureAwait(false);
                return new AccountCodeResult(CodeCheckStatus.Confirmed, user, IsNewAccount: true);
            }

            var profile = await _profiles.GetAsync(user.Id, cancellationToken).ConfigureAwait(false);
            if (profile is not null && _roles.IsAdmin(profile.Role) && !string.IsNullOrEmpty(user.PasswordHash))
            {
                return new AccountCodeResult(CodeCheckStatus.UsePassword);
            }

            if (user.EmailVerifiedAt is null)
            {
                user.EmailVerifiedAt = _now();
                await _users.SaveAsync(user, cancellationToken).ConfigureAwait(false);
            }

            return new AccountCodeResult(CodeCheckStatus.Confirmed, user);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Forgot password, step one: a code goes to the address if an account uses it. The answer is the same (Sent) whether
    /// or not one does, so this can't be used to find out who is registered.</summary>
    public async Task<EmailRequestResult> RequestPasswordResetAsync(string? rawEmail, string clientKey, CancellationToken cancellationToken = default)
    {
        var email = NormalizeEmail(rawEmail);
        if (email is null || string.IsNullOrWhiteSpace(clientKey))
        {
            return new EmailRequestResult(EmailRequestStatus.InvalidEmail);
        }

        if (await _users.GetByEmailAsync(email, cancellationToken).ConfigureAwait(false) is null)
        {
            return new EmailRequestResult(_deliveryConfigured || _exposeCode ? EmailRequestStatus.Sent : EmailRequestStatus.Unavailable);
        }

        return await SendCodeAsync("reset|" + Hash(email) + "|" + clientKey, email, "reset your password for", _brand, clientKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Step two: checks the code from the email and returns the account it proves. The caller sets the new password with
    /// <see cref="PasswordService.ResetAsync"/> (checking the password first, so a typo does not spend one of the code's tries) and sends the person to
    /// sign in: a reset doesn't sign anyone in.</summary>
    public async Task<AccountCodeResult> ConfirmPasswordResetAsync(string? rawEmail, string clientKey, string? code, CancellationToken cancellationToken = default)
    {
        var (status, email) = CheckAddressCode("reset", rawEmail, clientKey, code);
        if (status != CodeCheckStatus.Confirmed)
        {
            return new AccountCodeResult(status);
        }

        var user = await _users.GetByEmailAsync(email!, cancellationToken).ConfigureAwait(false);
        return user is null ? new AccountCodeResult(CodeCheckStatus.Expired) : new AccountCodeResult(CodeCheckStatus.Confirmed, user);
    }

    /// <summary>Checks a code sent to an address for <paramref name="purpose"/> from the browser <paramref name="clientKey"/>; the normalized address comes
    /// back with a confirmed code. An unusable address or browser key reads as an expired code, the same answer a stale one gets.</summary>
    private (CodeCheckStatus Status, string? Email) CheckAddressCode(string purpose, string? rawEmail, string clientKey, string? code)
    {
        var email = NormalizeEmail(rawEmail);
        if (email is null || string.IsNullOrWhiteSpace(clientKey))
        {
            return (CodeCheckStatus.Expired, null);
        }

        return (Check(purpose + "|" + Hash(email) + "|" + clientKey, code).Status, email);
    }

    /// <summary>For a signed-in account: a code goes to <paramref name="rawEmail"/> - its current address to confirm it, or a
    /// new one to change it. The answer is the same whether or not another account already uses the address; that
    /// is only reported at confirmation, to someone who holds the code.</summary>
    public async Task<EmailRequestResult> RequestEmailConfirmationAsync(string? userId, string? rawEmail, CancellationToken cancellationToken = default)
    {
        var email = NormalizeEmail(rawEmail);
        if (email is null)
        {
            return new EmailRequestResult(EmailRequestStatus.InvalidEmail);
        }

        var user = string.IsNullOrWhiteSpace(userId) ? null : await _users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        return user is null
            ? new EmailRequestResult(EmailRequestStatus.NotSignedIn)
            : await SendCodeAsync("confirm|" + user.Id, email, "confirm your email for", user.DisplayName, "user|" + user.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Confirms the code: the address it was sent to becomes the account's confirmed email.</summary>
    public async Task<AccountCodeResult> ConfirmEmailAsync(string? userId, string? code, CancellationToken cancellationToken = default)
    {
        var user = string.IsNullOrWhiteSpace(userId) ? null : await _users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return new AccountCodeResult(CodeCheckStatus.NotSignedIn);
        }

        var (status, pending) = Check("confirm|" + user.Id, code);
        if (status != CodeCheckStatus.Confirmed)
        {
            return new AccountCodeResult(status);
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            user = await _users.GetByIdAsync(user.Id, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                return new AccountCodeResult(CodeCheckStatus.NotSignedIn);
            }

            user.EmailVerifiedAt = _now();
            if (string.Equals(user.Email, pending!.Email, StringComparison.Ordinal))
            {
                await _users.SaveAsync(user, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    await _users.ChangeEmailAsync(user, pending.Email, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    return new AccountCodeResult(CodeCheckStatus.EmailInUse);
                }
            }

            return new AccountCodeResult(CodeCheckStatus.Confirmed, user);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string DefaultDisplayName(string email)
    {
        var local = email[..email.IndexOf('@')];
        return local.Length > DisplayNames.MaxLength ? local[..DisplayNames.MaxLength] : local;
    }

    private async Task<EmailRequestResult> SendCodeAsync(string pendingKey, string email, string what, string label, string clientKey, CancellationToken cancellationToken)
    {
        if (!_deliveryConfigured && !_exposeCode)
        {
            return new EmailRequestResult(EmailRequestStatus.Unavailable);
        }

        // Per-address and per-browser limits first (they protect other people's inboxes); then the ceilings that
        // protect the mail account. Global counters are only spent by requests that get all the way through.
        if (!TryAcquire("email|" + email) || !TryAcquire("client|" + clientKey, _maxPerClientPerHour))
        {
            return new EmailRequestResult(EmailRequestStatus.RateLimited);
        }

        var now = _now();
        if (!_hourlyCap.TryAcquire(now) || !_dailyCap.TryAcquire(now))
        {
            return new EmailRequestResult(EmailRequestStatus.RateLimited);
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        _pending[pendingKey] = new Pending
        {
            Email = email,
            Salt = salt,
            CodeHash = Hash(salt + ":" + code),
            ExpiresAt = now.AddMinutes(CodeLifetimeMinutes),
        };

        if (_deliveryConfigured)
        {
            try
            {
                await _email.SendAsync(
                    email,
                    $"Your {_brand} code",
                    $"<p>Your code to {what} <strong>{WebUtility.HtmlEncode(label)}</strong> is:</p><p style=\"font-size:1.6rem; letter-spacing:0.2rem;\"><strong>{code}</strong></p><p>It works for {CodeLifetimeMinutes} minutes. If you did not ask for this, you can ignore this email.</p>",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The mail provider refused or timed out (throttled, blocked, unreachable). Nothing went out, so
                // discard the code rather than leave a live code nobody received, and let the caller say so.
                _pending.TryRemove(pendingKey, out _);
                _onSendFailure?.Invoke(ex);
                return new EmailRequestResult(EmailRequestStatus.SendFailed);
            }
        }

        return new EmailRequestResult(EmailRequestStatus.Sent, _exposeCode && !_deliveryConfigured ? code : null);
    }

    private (CodeCheckStatus Status, Pending? Pending) Check(string pendingKey, string? code)
    {
        if (!_pending.TryGetValue(pendingKey, out var pending) || pending.ExpiresAt < _now())
        {
            _pending.TryRemove(pendingKey, out _);
            return (CodeCheckStatus.Expired, null);
        }

        if (pending.Attempts >= MaxAttempts)
        {
            _pending.TryRemove(pendingKey, out _);
            return (CodeCheckStatus.TooManyAttempts, null);
        }

        pending.Attempts++;
        var supplied = Hash(pending.Salt + ":" + (code ?? string.Empty).Trim());
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(pending.CodeHash)))
        {
            return (CodeCheckStatus.WrongCode, null);
        }

        _pending.TryRemove(pendingKey, out _);
        return (CodeCheckStatus.Confirmed, pending);
    }

    private bool TryAcquire(string key, int max = MaxRequestsPerHour)
    {
        if (_limiters.Count > MaxTrackedKeys)
        {
            _limiters.Clear();
        }

        return _limiters.GetOrAdd(key, _ => new SlidingWindowRateLimiter(max, TimeSpan.FromHours(1))).TryAcquire(_now());
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Releases the disposable fields this type owns.</summary>
    public void Dispose()
    {
        _writeLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
