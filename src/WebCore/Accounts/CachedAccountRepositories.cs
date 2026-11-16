/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Caching;

namespace WebCore.Accounts;

/// <summary>
/// Short-lived in-memory copies of data that is read far more often than it changes, built from the shared caching blocks (<see cref="SnapshotCache{T}"/>,
/// <see cref="KeyedCache{TKey, TValue}"/>). Every write made through this app clears the copy at once; the time-to-live only bounds how stale a read can be when
/// the change was made somewhere else (two app instances can share storage but not memory). The defaults are deliberately short. See docs/performance.md.
/// </summary>
internal static class AccountCacheDefaults
{
    public static readonly TimeSpan ShortLived = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The account list (the admin users page reads every account), and lookups by id served from it: the store has no index by id, so going straight through
/// would read every account for each lookup, and the sign-in cookie check looks one up on every request (its security stamp). The shared list is never
/// handed out: a list caller gets copies, a lookup copies only the account it finds. Lookups by email (a direct document read) and all writes go straight
/// through; a write clears the list at once.
/// </summary>
public class CachedUserRepository : IUserRepository, IDisposable
{
    private readonly IUserRepository _inner;
    private readonly SnapshotCache<IReadOnlyList<User>> _all;

    public CachedUserRepository(IUserRepository inner, TimeSpan? ttl = null, Func<DateTimeOffset>? now = null)
    {
        _inner = inner;
        _all = new SnapshotCache<IReadOnlyList<User>>("users.all", ttl ?? AccountCacheDefaults.ShortLived, now);
    }

    public Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default) => _inner.GetByEmailAsync(email, cancellationToken);

    public async Task<User?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(id) ? null : (await _all.GetAsync(_inner.ListAsync, cancellationToken).ConfigureAwait(false)).FirstOrDefault(u => u.Id == id)?.Clone();

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken = default) =>
        (await _all.GetAsync(_inner.ListAsync, cancellationToken).ConfigureAwait(false)).Select(u => u.Clone()).ToList();

    public async Task SaveAsync(User user, CancellationToken cancellationToken = default)
    {
        try
        {
            await _inner.SaveAsync(user, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Invalidate();
        }
    }

    public async Task ChangeEmailAsync(User user, string newEmail, CancellationToken cancellationToken = default)
    {
        try
        {
            await _inner.ChangeEmailAsync(user, newEmail, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Invalidate();
        }
    }

    public async Task<User> CreateAsync(User user, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _inner.CreateAsync(user, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Invalidate();
        }
    }

    public void Invalidate() => _all.Invalidate();

    /// <summary>Releases the disposable fields this type owns.</summary>
    public void Dispose()
    {
        _all.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Profiles, which the site reads on nearly every request (the sign-in cookie check reads one each time for the account's role and whether it was
/// disabled). A change made through this app clears the copy immediately, so disabling an
/// account takes effect at once on the server that did it; another server notices within the time-to-live. Callers always get
/// their own copy, never the shared one.
/// </summary>
public class CachedUserProfileRepository : IUserProfileRepository, IDisposable
{
    private readonly IUserProfileRepository _inner;
    private readonly KeyedCache<string, UserProfile> _byUser;
    private readonly SnapshotCache<IReadOnlyList<UserProfile>> _all;

    public CachedUserProfileRepository(IUserProfileRepository inner, TimeSpan? ttl = null, Func<DateTimeOffset>? now = null)
    {
        _inner = inner;
        var lifetime = ttl ?? AccountCacheDefaults.ShortLived;
        _byUser = new KeyedCache<string, UserProfile>("profiles.byUser", lifetime, now, copy: p => p.Clone());
        _all = new SnapshotCache<IReadOnlyList<UserProfile>>("profiles.all", lifetime, now, profiles => profiles.Select(p => p.Clone()).ToList());
    }

    public async Task<UserProfile?> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        return await _byUser.GetOrLoadAsync(userId, ct => _inner.GetAsync(userId, ct), cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserProfile> EnsureProfileAsync(string userId, string displayName, CancellationToken cancellationToken = default)
    {
        // A copy that already has this display name needs no write: the common case (every page load).
        if (_byUser.TryGet(userId, out var cached) && cached.DisplayName == displayName)
        {
            return cached;
        }

        var profile = await _inner.EnsureProfileAsync(userId, displayName, cancellationToken).ConfigureAwait(false);
        Invalidate();
        _byUser.Set(userId, profile, _byUser.Generation);
        return profile;
    }

    public Task SetRoleAsync(string userId, string role, CancellationToken cancellationToken = default) =>
        Write(() => _inner.SetRoleAsync(userId, role, cancellationToken));

    public Task SetDisabledAsync(string userId, bool disabled, CancellationToken cancellationToken = default) =>
        Write(() => _inner.SetDisabledAsync(userId, disabled, cancellationToken));

    public Task<IReadOnlyList<UserProfile>> ListAllAsync(CancellationToken cancellationToken = default) => _all.GetAsync(_inner.ListAllAsync, cancellationToken);

    public void Invalidate()
    {
        _byUser.Invalidate();
        _all.Invalidate();
    }

    private async Task Write(Func<Task> write)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
        finally
        {
            Invalidate();
        }
    }

    /// <summary>Releases the disposable fields this type owns.</summary>
    public void Dispose()
    {
        _all.Dispose();
        GC.SuppressFinalize(this);
    }
}
