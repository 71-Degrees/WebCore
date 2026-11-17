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
/// <summary>
/// The account list for the app's own account class (<typeparamref name="TUser"/>), with the same copies, lookups and invalidation as <see cref="CachedUserRepository"/>.
/// </summary>
public class CachedUserRepository<TUser> : UserRepositoryBase<TUser>, IDisposable
    where TUser : User, new()
{
    private readonly IUserRepository<TUser> _inner;
    private readonly SnapshotCache<IReadOnlyList<TUser>> _all;

    public CachedUserRepository(IUserRepository<TUser> inner, TimeSpan? ttl = null, Func<DateTimeOffset>? now = null)
    {
        _inner = inner;
        _all = new SnapshotCache<IReadOnlyList<TUser>>("users.all", ttl ?? AccountCacheDefaults.ShortLived, now);
    }

    public override Task<TUser?> GetByEmailAsync(string email, CancellationToken cancellationToken = default) => _inner.GetByEmailAsync(email, cancellationToken);

    public override async Task<TUser?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var found = (await _all.GetAsync(_inner.ListAsync, cancellationToken).ConfigureAwait(false)).FirstOrDefault(u => u.Id == id);
        return found is null ? null : Copy(found);
    }

    public override async Task<IReadOnlyList<TUser>> ListAsync(CancellationToken cancellationToken = default) =>
        (await _all.GetAsync(_inner.ListAsync, cancellationToken).ConfigureAwait(false)).Select(u => Copy(u)).ToList();

    public override Task SaveAsync(TUser user, CancellationToken cancellationToken = default) =>
        WriteThenInvalidate(() => _inner.SaveAsync(user, cancellationToken));

    public override Task ChangeEmailAsync(TUser user, string newEmail, CancellationToken cancellationToken = default) =>
        WriteThenInvalidate(() => _inner.ChangeEmailAsync(user, newEmail, cancellationToken));

    public override Task<TUser> CreateAsync(TUser user, CancellationToken cancellationToken = default) =>
        WriteThenInvalidateAsync(() => _inner.CreateAsync(user, cancellationToken));

    public void Invalidate() => _all.Invalidate();

    /// <summary>Releases the disposable fields this type owns.</summary>
    public void Dispose()
    {
        _all.Dispose();
        GC.SuppressFinalize(this);
    }

    private static TUser Copy(TUser user) => (TUser)user.Clone();

    private async Task WriteThenInvalidate(Func<Task> write)
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

    private async Task<TUser> WriteThenInvalidateAsync(Func<Task<TUser>> write)
    {
        try
        {
            return await write().ConfigureAwait(false);
        }
        finally
        {
            Invalidate();
        }
    }
}

/// <summary>
/// The account list (the admin users page reads every account), and lookups by id served from it: the store has no index by id, so going straight through
/// would read every account for each lookup, and the sign-in cookie check looks one up on every request (its security stamp). The shared list is never
/// handed out: a list caller gets copies, a lookup copies only the account it finds. Lookups by email (a direct document read) and all writes go straight
/// through; a write clears the list at once.
/// </summary>
public class CachedUserRepository(IUserRepository inner, TimeSpan? ttl = null, Func<DateTimeOffset>? now = null)
    : CachedUserRepository<User>(new PlainUsers(inner), ttl, now)
{
    /// <summary>Wraps a plain repository (a test double, or a counting proxy) and shows the plain account shape to the typed base.</summary>
    private sealed class PlainUsers(IUserRepository inner) : IUserRepository<User>
    {
        public Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default) => inner.GetByEmailAsync(email, cancellationToken);

        public Task<User?> GetByIdAsync(string id, CancellationToken cancellationToken = default) => inner.GetByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);

        public Task SaveAsync(User user, CancellationToken cancellationToken = default) => inner.SaveAsync(user, cancellationToken);

        public Task ChangeEmailAsync(User user, string newEmail, CancellationToken cancellationToken = default) =>
            inner.ChangeEmailAsync(user, newEmail, cancellationToken);

        public Task<User> CreateAsync(User user, CancellationToken cancellationToken = default) => inner.CreateAsync(user, cancellationToken);
    }
}

/// <summary>
/// Profiles, which the site reads on nearly every request (the sign-in cookie check reads one each time for the account's role and whether it was
/// disabled). A change made through this app clears the copy immediately, so disabling an
/// account takes effect at once on the server that did it; another server notices within the time-to-live. Callers always get
/// their own copy, never the shared one.
/// </summary>
public class CachedUserProfileRepository(IUserProfileRepository<UserProfile> inner, TimeSpan? ttl = null, Func<DateTimeOffset>? now = null)
    : CachedUserProfileRepository<UserProfile>(inner, ttl, now)
{
    /// <summary>Wraps a plain repository (a test double, or a counting proxy) that only speaks <see cref="IUserProfileRepository"/>.</summary>
    public CachedUserProfileRepository(IUserProfileRepository inner, TimeSpan? ttl = null, Func<DateTimeOffset>? now = null)
        : this(new PlainProfiles(inner), ttl, now)
    {
    }

    private sealed class PlainProfiles(IUserProfileRepository inner) : IUserProfileRepository<UserProfile>
    {
        public Task<UserProfile?> GetAsync(string userId, CancellationToken cancellationToken = default) => inner.GetAsync(userId, cancellationToken);

        public Task<UserProfile> EnsureProfileAsync(string userId, string displayName, CancellationToken cancellationToken = default) =>
            inner.EnsureProfileAsync(userId, displayName, cancellationToken);

        public Task<IReadOnlyList<UserProfile>> ListAllAsync(CancellationToken cancellationToken = default) => inner.ListAllAsync(cancellationToken);

        public Task UpdateAsync(string userId, Action<UserProfile> change, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A plain profile repository has no app fields to update.");

        public Task SetRoleAsync(string userId, string role, CancellationToken cancellationToken = default) => inner.SetRoleAsync(userId, role, cancellationToken);

        public Task SetDisabledAsync(string userId, bool disabled, CancellationToken cancellationToken = default) =>
            inner.SetDisabledAsync(userId, disabled, cancellationToken);
    }
}

/// <summary>The profile cache for an app's own profile class (see <see cref="IUserProfileRepository{TProfile}"/>); it answers WebCore's
/// <see cref="IUserProfileRepository"/> too, from the same copies.</summary>
public class CachedUserProfileRepository<TProfile> : ProfileRepositoryBase<TProfile>, IDisposable
    where TProfile : UserProfile
{
    private readonly IUserProfileRepository<TProfile> _inner;
    private readonly KeyedCache<string, TProfile> _byUser;
    private readonly SnapshotCache<IReadOnlyList<TProfile>> _all;

    public CachedUserProfileRepository(IUserProfileRepository<TProfile> inner, TimeSpan? ttl = null, Func<DateTimeOffset>? now = null)
    {
        _inner = inner;
        var lifetime = ttl ?? AccountCacheDefaults.ShortLived;
        _byUser = new KeyedCache<string, TProfile>("profiles.byUser", lifetime, now, copy: p => (TProfile)p.Clone());
        _all = new SnapshotCache<IReadOnlyList<TProfile>>("profiles.all", lifetime, now, profiles => profiles.Select(p => (TProfile)p.Clone()).ToList());
    }

    public override async Task<TProfile?> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        return await _byUser.GetOrLoadAsync(userId, ct => _inner.GetAsync(userId, ct), cancellationToken).ConfigureAwait(false);
    }

    public override Task UpdateAsync(string userId, Action<TProfile> change, CancellationToken cancellationToken = default) =>
        Write(() => _inner.UpdateAsync(userId, change, cancellationToken));

    public override async Task<TProfile> EnsureProfileAsync(string userId, string displayName, CancellationToken cancellationToken = default)
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

    public override Task SetRoleAsync(string userId, string role, CancellationToken cancellationToken = default) =>
        Write(() => _inner.SetRoleAsync(userId, role, cancellationToken));

    public override Task SetDisabledAsync(string userId, bool disabled, CancellationToken cancellationToken = default) =>
        Write(() => _inner.SetDisabledAsync(userId, disabled, cancellationToken));

    public override Task<IReadOnlyList<TProfile>> ListAllAsync(CancellationToken cancellationToken = default) => _all.GetAsync(_inner.ListAllAsync, cancellationToken);

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
