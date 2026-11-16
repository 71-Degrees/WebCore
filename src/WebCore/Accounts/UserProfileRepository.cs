/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using System.Collections.Concurrent;
using WebCore.Storage;

namespace WebCore.Accounts;

/// <summary>
/// One profile document per user, named by user id. The id is validated (<see cref="DocumentNames"/>) because an id arriving in a claim is not fully within
/// the app's control. Every read turns the stored role into a name (<see cref="AccountRoles.Normalize"/>), so a profile saved when roles were numbers is
/// seen as the right role and saved as its name the next time it changes.
/// Every change is a read-modify-write, and two concurrent changes to the SAME user (several tabs signed into one account) would otherwise both read the
/// old document and both write: a lost update at best and, on Windows, an exception out of the file move. One lock per user id serializes that cycle
/// without blocking anyone else; in-process locking is enough because each app runs as a single instance.
/// </summary>
public sealed class UserProfileRepository(IDocumentStore store, AccountRoles roles, TimeProvider? timeProvider = null) : IUserProfileRepository
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _userLocks = new();

    public async Task<UserProfile?> GetAsync(string userId, CancellationToken cancellationToken = default) =>
        Normalized(await store.GetAsync<UserProfile>(NameFor(userId), cancellationToken).ConfigureAwait(false));

    public async Task<UserProfile> EnsureProfileAsync(string userId, string displayName, CancellationToken cancellationToken = default)
    {
        var gate = GateFor(userId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = await GetAsync(userId, cancellationToken).ConfigureAwait(false);
            if (profile?.DisplayName == displayName)
            {
                return profile;
            }

            profile ??= new UserProfile { UserId = userId, Role = roles.Default };
            profile.DisplayName = displayName;
            await store.SetAsync(NameFor(userId), profile, cancellationToken).ConfigureAwait(false);
            return profile;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task SetRoleAsync(string userId, string role, CancellationToken cancellationToken = default) =>
        UpdateAsync(userId, profile => profile.Role = role, cancellationToken);

    public Task SetDisabledAsync(string userId, bool disabled, CancellationToken cancellationToken = default) =>
        UpdateAsync(
            userId,
            profile =>
            {
                profile.IsDisabled = disabled;
                profile.DisabledAt = disabled ? (timeProvider ?? TimeProvider.System).GetUtcNow() : null;
            },
            cancellationToken);

    public async Task<IReadOnlyList<UserProfile>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        var profiles = new List<UserProfile>();
        await foreach (var (_, profile) in store.ListAsync<UserProfile>(cancellationToken).ConfigureAwait(false))
        {
            profiles.Add(Normalized(profile)!);
        }

        return profiles.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Loads the user's profile, applies <paramref name="change"/>, and saves it, all under that user's lock. A user with no profile is left alone.</summary>
    private async Task UpdateAsync(string userId, Action<UserProfile> change, CancellationToken cancellationToken)
    {
        var gate = GateFor(userId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = await GetAsync(userId, cancellationToken).ConfigureAwait(false);
            if (profile is null)
            {
                return;
            }

            change(profile);
            await store.SetAsync(NameFor(userId), profile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private UserProfile? Normalized(UserProfile? profile)
    {
        if (profile is not null)
        {
            profile.Role = roles.Normalize(profile.Role);
        }

        return profile;
    }

    private SemaphoreSlim GateFor(string userId) => _userLocks.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));

    private static string NameFor(string userId) => DocumentNames.For(userId, "user");
}
