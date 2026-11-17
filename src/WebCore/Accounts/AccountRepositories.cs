/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using System.Text.Json;

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

/// <summary>
/// The same accounts as the app's own account class (<typeparamref name="TUser"/>, deriving from <see cref="User"/>), for an app that keeps fields of its own
/// on the account (a sign-up address, a counter). WebCore reads and writes accounts through <see cref="IUserRepository"/>; the same repository answers both,
/// so the app's fields survive every WebCore save.
/// </summary>
public interface IUserRepository<TUser>
    where TUser : User
{
    /// <summary>As <see cref="IUserRepository.CreateAsync"/>, for the app's class.</summary>
    Task<TUser> CreateAsync(TUser user, CancellationToken cancellationToken = default);

    /// <summary>As <see cref="IUserRepository.SaveAsync"/>, for the app's class.</summary>
    Task SaveAsync(TUser user, CancellationToken cancellationToken = default);

    /// <summary>As <see cref="IUserRepository.ChangeEmailAsync"/>, for the app's class.</summary>
    Task ChangeEmailAsync(TUser user, string newEmail, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TUser>> ListAsync(CancellationToken cancellationToken = default);

    Task<TUser?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<TUser?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);
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

/// <summary>
/// The same profiles as the app's own profile class (<typeparamref name="TProfile"/>, deriving from <see cref="UserProfile"/>), for an app that keeps
/// fields of its own on the profile (a team name, an avatar, a setting that defaults to on). WebCore itself reads and writes them through
/// <see cref="IUserProfileRepository"/>; the same repository answers both, so the app's fields survive every WebCore save.
/// </summary>
public interface IUserProfileRepository<TProfile>
    where TProfile : UserProfile
{
    Task<TProfile?> GetAsync(string userId, CancellationToken cancellationToken = default);

    Task<TProfile> EnsureProfileAsync(string userId, string displayName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TProfile>> ListAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Admin action, as <see cref="IUserProfileRepository.SetRoleAsync"/>.</summary>
    Task SetRoleAsync(string userId, string role, CancellationToken cancellationToken = default);

    /// <summary>Admin action, as <see cref="IUserProfileRepository.SetDisabledAsync"/>.</summary>
    Task SetDisabledAsync(string userId, bool disabled, CancellationToken cancellationToken = default);

    /// <summary>Loads the profile, applies <paramref name="change"/> (the app's own fields) and saves it, under that user's lock. A user with no profile is left
    /// alone. The change must not touch the role or the disabled flag: those have their own admin actions.</summary>
    Task UpdateAsync(string userId, Action<TProfile> change, CancellationToken cancellationToken = default);
}

/// <summary>A profile repository for the app's class that answers WebCore's plain <see cref="IUserProfileRepository"/> too: the one place that knows how
/// the typed answers become plain ones.</summary>
public abstract class ProfileRepositoryBase<TProfile> : IUserProfileRepository<TProfile>, IUserProfileRepository
    where TProfile : UserProfile
{
    public abstract Task<TProfile?> GetAsync(string userId, CancellationToken cancellationToken = default);

    public abstract Task<TProfile> EnsureProfileAsync(string userId, string displayName, CancellationToken cancellationToken = default);

    public abstract Task<IReadOnlyList<TProfile>> ListAllAsync(CancellationToken cancellationToken = default);

    public abstract Task SetRoleAsync(string userId, string role, CancellationToken cancellationToken = default);

    public abstract Task SetDisabledAsync(string userId, bool disabled, CancellationToken cancellationToken = default);

    public abstract Task UpdateAsync(string userId, Action<TProfile> change, CancellationToken cancellationToken = default);

    async Task<UserProfile?> IUserProfileRepository.GetAsync(string userId, CancellationToken cancellationToken) =>
        await GetAsync(userId, cancellationToken).ConfigureAwait(false);

    async Task<UserProfile> IUserProfileRepository.EnsureProfileAsync(string userId, string displayName, CancellationToken cancellationToken) =>
        await EnsureProfileAsync(userId, displayName, cancellationToken).ConfigureAwait(false);

    async Task<IReadOnlyList<UserProfile>> IUserProfileRepository.ListAllAsync(CancellationToken cancellationToken) =>
        await ListAllAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>An account repository for the app's class that answers WebCore's plain <see cref="IUserRepository"/> too: the one place that knows how the
/// typed answers become plain ones, and how a plain account handed in becomes the app's class.</summary>
public abstract class UserRepositoryBase<TUser> : IUserRepository<TUser>, IUserRepository
    where TUser : User, new()
{
    public abstract Task<TUser?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);

    public abstract Task<TUser?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    public abstract Task<IReadOnlyList<TUser>> ListAsync(CancellationToken cancellationToken = default);

    public abstract Task SaveAsync(TUser user, CancellationToken cancellationToken = default);

    public abstract Task ChangeEmailAsync(TUser user, string newEmail, CancellationToken cancellationToken = default);

    public abstract Task<TUser> CreateAsync(TUser user, CancellationToken cancellationToken = default);

    async Task<User?> IUserRepository.GetByEmailAsync(string email, CancellationToken cancellationToken) =>
        await GetByEmailAsync(email, cancellationToken).ConfigureAwait(false);

    async Task<User?> IUserRepository.GetByIdAsync(string id, CancellationToken cancellationToken) =>
        await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);

    async Task<IReadOnlyList<User>> IUserRepository.ListAsync(CancellationToken cancellationToken) =>
        await ListAsync(cancellationToken).ConfigureAwait(false);

    async Task IUserRepository.SaveAsync(User user, CancellationToken cancellationToken) =>
        await SaveAsync(Typed(user), cancellationToken).ConfigureAwait(false);

    // A plain account handed in is copied first, so the new address lands on the copy and not on the caller's object.
    async Task IUserRepository.ChangeEmailAsync(User user, string newEmail, CancellationToken cancellationToken) =>
        await ChangeEmailAsync(Typed(user), newEmail, cancellationToken).ConfigureAwait(false);

    async Task<User> IUserRepository.CreateAsync(User user, CancellationToken cancellationToken) =>
        await CreateAsync(Typed(user), cancellationToken).ConfigureAwait(false);

    /// <summary>The one place a plain <see cref="User"/> becomes the app's class. A stored account already is one (every read returns the app's class), so
    /// the copy happens only for a plain account handed in by WebCore's own services. Every field WebCore knows is copied; the app's own fields start at
    /// their defaults.</summary>
    protected static TUser Typed(User user)
    {
        if (user is TUser typed)
        {
            return typed;
        }

        return new TUser
        {
            Id = user.Id,
            Email = user.Email,
            DisplayName = user.DisplayName,
            PasswordHash = user.PasswordHash,
            SecurityStamp = user.SecurityStamp,
            EmailVerifiedAt = user.EmailVerifiedAt,
            CreatedAt = user.CreatedAt,
            UnknownFields = user.UnknownFields is null ? null : new Dictionary<string, JsonElement>(user.UnknownFields),
        };
    }
}
