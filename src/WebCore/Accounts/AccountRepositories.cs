/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
namespace WebCore.Accounts;

public interface IUserRepository
{
    Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);

    Task<User?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Overwrites an existing account (same email); a no-op if it does not exist.</summary>
    Task SaveAsync(User user, CancellationToken cancellationToken = default);

    /// <summary>Moves the account to <paramref name="newEmail"/> and saves it. Throws <see cref="InvalidOperationException"/> if another account already
    /// uses that address.</summary>
    Task ChangeEmailAsync(User user, string newEmail, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="InvalidOperationException"/> if the email is already registered.</summary>
    Task<User> CreateAsync(User user, CancellationToken cancellationToken = default);
}

public interface IUserProfileRepository
{
    Task<UserProfile?> GetAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Creates a profile (with the default role) if none exists for this user id, or refreshes the display name if one does. Called on every
    /// successful sign-in, so a profile always exists for anyone who has ever signed in. Never changes an existing profile's role.</summary>
    Task<UserProfile> EnsureProfileAsync(string userId, string displayName, CancellationToken cancellationToken = default);

    /// <summary>Admin action: sets a user's role (a name from the app's <see cref="AccountRoles"/>). No-ops if the user has no profile yet.</summary>
    Task SetRoleAsync(string userId, string role, CancellationToken cancellationToken = default);

    /// <summary>Admin action: disables or re-enables an account, stamping or clearing <see cref="UserProfile.DisabledAt"/>. No-ops if the user has no
    /// profile yet.</summary>
    Task SetDisabledAsync(string userId, bool disabled, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UserProfile>> ListAllAsync(CancellationToken cancellationToken = default);
}
