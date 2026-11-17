/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using System.Collections.Concurrent;
using WebCore.Storage;
using Microsoft.Extensions.Logging;

namespace WebCore.Accounts;

/// <summary>Profiles of the plain <see cref="UserProfile"/> shape, for an app with no profile fields of its own.</summary>
public sealed class UserProfileRepository(IDocumentStore store, AccountRoles roles, TimeProvider? timeProvider = null, ILogger? logger = null)
    : UserProfileRepository<UserProfile>(store, roles, timeProvider, logger);

/// <summary>
/// One profile document per user, named by user id, read as the app's profile class. The id is validated (<see cref="DocumentNames"/>) because an id
/// arriving in a claim is not fully within the app's control. Every read turns the stored role into a name (<see cref="AccountRoles.Normalize"/>), so a
/// profile saved when roles were numbers is seen as the right role and saved as its name the next time it changes.
/// Every change is a read-modify-write, and two concurrent changes to the SAME user (several tabs signed into one account) would otherwise both read the
/// old document and both write: a lost update at best and, on Windows, an exception out of the file move. One lock per user id serializes that cycle
/// without blocking anyone else; in-process locking is enough because each app runs as a single instance.
/// </summary>
public class UserProfileRepository<TProfile>(IDocumentStore store, AccountRoles roles, TimeProvider? timeProvider = null, ILogger? logger = null)
    : ProfileRepositoryBase<TProfile>
    where TProfile : UserProfile, new()
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _userLocks = new();

    public override async Task<TProfile?> GetAsync(string userId, CancellationToken cancellationToken = default) =>
        Normalized(await store.GetAsync<TProfile>(NameFor(userId), cancellationToken).ConfigureAwait(false));

    public override async Task<TProfile> EnsureProfileAsync(string userId, string displayName, CancellationToken cancellationToken = default)
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

            if (profile is null)
            {
                profile = new TProfile { UserId = userId, Role = roles.Default, CreatedAt = Now(), DisplayName = displayName };
                if (await store.ExistsAsync(NameFor(userId), cancellationToken).ConfigureAwait(false))
                {
                    // The store reads a blob it cannot parse as "absent". Writing a fresh default profile over it would silently demote an admin and destroy
                    // the evidence, so this request gets a default that is not saved and the blob is left for someone to repair.
                    logger?.LogError("The profile document for user {UserId} exists but could not be read; serving a default profile for this request without saving it", userId);
                    return profile;
                }
            }

            profile.DisplayName = displayName;
            await store.SetAsync(NameFor(userId), profile, cancellationToken).ConfigureAwait(false);
            return profile;
        }
        finally
        {
            gate.Release();
        }
    }

    public override Task SetRoleAsync(string userId, string role, CancellationToken cancellationToken = default) =>
        UpdateAsync(userId, profile => profile.Role = role, cancellationToken);

    public override Task SetDisabledAsync(string userId, bool disabled, CancellationToken cancellationToken = default) =>
        UpdateAsync(
            userId,
            profile =>
            {
                profile.IsDisabled = disabled;
                profile.DisabledAt = disabled ? Now() : null;
            },
            cancellationToken);

    public override async Task<IReadOnlyList<TProfile>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        var profiles = new List<TProfile>();
        await foreach (var (_, profile) in store.ListAsync<TProfile>(cancellationToken).ConfigureAwait(false))
        {
            profiles.Add(Normalized(profile)!);
        }

        return profiles.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public override async Task UpdateAsync(string userId, Action<TProfile> change, CancellationToken cancellationToken = default)
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

    private TProfile? Normalized(TProfile? profile)
    {
        if (profile is not null)
        {
            profile.Role = roles.Normalize(profile.Role);
        }

        return profile;
    }

    private SemaphoreSlim GateFor(string userId) => _userLocks.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));

    private static string NameFor(string userId) => DocumentNames.For(userId, "user");

    private DateTimeOffset Now() => (timeProvider ?? TimeProvider.System).GetUtcNow();
}
