/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Globalization;
using System.Net;
using WebCore.Email;

namespace WebCore.Accounts;

public enum PasswordChangeStatus
{
    Changed,

    /// <summary>The new password broke a rule; <see cref="PasswordChangeResult.Message"/> says which.</summary>
    Invalid,
    WrongCurrentPassword,

    /// <summary>Changing needs an existing password; this account has none (use Set).</summary>
    NoPasswordSet,

    /// <summary>Setting is for an account without a password; this one has one (use Change).</summary>
    AlreadyHasPassword,

    /// <summary>Setting a first password needs the account's email to be confirmed.</summary>
    EmailNotConfirmed,
    NotSignedIn,
}

public record PasswordChangeResult(PasswordChangeStatus Status, string? Message = null);

/// <summary>
/// Changing an account's password, in the three ways it happens: changing it (the current one is proved), setting a first one on an account made
/// by emailed code (the email must be confirmed), and resetting it after the caller has proved the email with a code (<see cref="AccountEmailService"/>
/// checks that code). Every path validates with <see cref="PasswordRules"/> and ends the same way: the hash is saved and a notice goes to the
/// account's address, so a change made by someone else does not go unseen.
/// </summary>
public sealed class PasswordService
{
    private readonly IUserRepository _users;
    private readonly IEmailSender _email;
    private readonly string _brand;
    private readonly bool _deliveryConfigured;
    private readonly TimeProvider _time;
    private readonly Action<Exception>? _onSendFailure;

    /// <param name="options">The same brand and delivery switch as the code emails: a notice is only sent when codes are.</param>
    public PasswordService(IUserRepository users, IEmailSender email, AccountEmailOptions options, TimeProvider? time = null, Action<Exception>? onSendFailure = null)
    {
        _users = users;
        _email = email;
        _brand = options.BrandName;
        _deliveryConfigured = options.DeliveryConfigured;
        _time = time ?? TimeProvider.System;
        _onSendFailure = onSendFailure;
    }

    public async Task<PasswordChangeResult> ChangeAsync(string? userId, string? currentPassword, string? newPassword, string? confirmation, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return new PasswordChangeResult(PasswordChangeStatus.NotSignedIn);
        }

        if (string.IsNullOrEmpty(user.PasswordHash))
        {
            return new PasswordChangeResult(PasswordChangeStatus.NoPasswordSet);
        }

        // The current password is checked before the new one is judged, so a wrong guess learns nothing about the rules.
        if (!PasswordHasher.Verify(currentPassword ?? string.Empty, user.PasswordHash))
        {
            return new PasswordChangeResult(PasswordChangeStatus.WrongCurrentPassword);
        }

        return await ApplyAsync(user, newPassword, confirmation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PasswordChangeResult> SetAsync(string? userId, string? newPassword, string? confirmation, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return new PasswordChangeResult(PasswordChangeStatus.NotSignedIn);
        }

        if (!string.IsNullOrEmpty(user.PasswordHash))
        {
            return new PasswordChangeResult(PasswordChangeStatus.AlreadyHasPassword);
        }

        return user.EmailVerifiedAt is null
            ? new PasswordChangeResult(PasswordChangeStatus.EmailNotConfirmed)
            : await ApplyAsync(user, newPassword, confirmation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>For a caller that has just proved the address with an emailed code: sets the password whether or not the account had one, and counts the address as confirmed.</summary>
    public Task<PasswordChangeResult> ResetAsync(User user, string? newPassword, string? confirmation, CancellationToken cancellationToken = default) =>
        ApplyAsync(user, newPassword, confirmation, cancellationToken);

    /// <summary>After a successful password sign-in: re-hashes a password stored with fewer iterations than today's (<see cref="PasswordHasher.NeedsRehash"/>).
    /// The password itself is unchanged, so the security stamp is kept and no notice is sent.</summary>
    public async Task UpgradeHashIfNeededAsync(User user, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(user.PasswordHash) || !PasswordHasher.NeedsRehash(user.PasswordHash))
        {
            return;
        }

        user.PasswordHash = PasswordHasher.Hash(password);
        await _users.SaveAsync(user, cancellationToken).ConfigureAwait(false);
    }

    private async Task<User?> FindAsync(string? userId, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(userId) ? null : await _users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);

    private async Task<PasswordChangeResult> ApplyAsync(User user, string? newPassword, string? confirmation, CancellationToken cancellationToken)
    {
        var problem = PasswordRules.Validate(newPassword, confirmation);
        if (problem is not null)
        {
            return new PasswordChangeResult(PasswordChangeStatus.Invalid, problem);
        }

        user.PasswordHash = PasswordHasher.Hash(newPassword!);
        user.RotateSecurityStamp();
        user.EmailVerifiedAt ??= _time.GetUtcNow();
        await _users.SaveAsync(user, cancellationToken).ConfigureAwait(false);
        await SendNoticeAsync(user, cancellationToken).ConfigureAwait(false);
        return new PasswordChangeResult(PasswordChangeStatus.Changed);
    }

    // A notice that cannot be sent never undoes the change: the password is already saved, and the failure is reported to the caller's hook.
    private async Task SendNoticeAsync(User user, CancellationToken cancellationToken)
    {
        if (!_deliveryConfigured)
        {
            return;
        }

        try
        {
            var when = _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
            await _email.SendAsync(
                user.Email,
                $"Your {_brand} password was changed",
                $"<p>The password for your {WebUtility.HtmlEncode(_brand)} account was changed on {when}.</p><p>If this was you, there is nothing more to do. If it was not, use <strong>Forgot your password?</strong> on the log-in page to choose a new one, and contact us.</p>",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _onSendFailure?.Invoke(ex);
        }
    }
}
