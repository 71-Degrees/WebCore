/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Accounts;


namespace WebCore.Tests.Accounts;

public class CachedRepositoriesTests
{
    private DateTimeOffset _now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    // ---- fakes that count how often storage is actually hit -------------------------------------------------------------------------

    private sealed class FakeUsers : IUserRepository
    {
        public int ListCalls;
        public List<User> Stored { get; } = new();

        public Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default) => Task.FromResult(Stored.FirstOrDefault(u => u.Email == email));

        public Task<User?> GetByIdAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(Stored.FirstOrDefault(u => u.Id == id));

        public Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<User>>(Stored.Select(u => u.Clone()).ToList());
        }

        public Task SaveAsync(User user, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ChangeEmailAsync(User user, string newEmail, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<User> CreateAsync(User user, CancellationToken cancellationToken = default)
        {
            Stored.Add(user);
            return Task.FromResult(user);
        }
    }

    private sealed class FakeProfiles : IUserProfileRepository
    {
        public int GetCalls;
        public int EnsureCalls;
        public Dictionary<string, UserProfile> Stored { get; } = new();

        public Task<UserProfile?> GetAsync(string userId, CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(Stored.TryGetValue(userId, out var p) ? p.Clone() : null);
        }

        public Task<UserProfile> EnsureProfileAsync(string userId, string displayName, CancellationToken cancellationToken = default)
        {
            EnsureCalls++;
            if (!Stored.TryGetValue(userId, out var p))
            {
                p = Stored[userId] = new UserProfile { UserId = userId, DisplayName = displayName };
            }

            p.DisplayName = displayName;
            return Task.FromResult(p.Clone());
        }

        public Task SetRoleAsync(string userId, string role, CancellationToken cancellationToken = default)
        {
            Stored[userId].Role = role;
            return Task.CompletedTask;
        }

        public Task SetDisabledAsync(string userId, bool disabled, CancellationToken cancellationToken = default)
        {
            Stored[userId].IsDisabled = disabled;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<UserProfile>> ListAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UserProfile>>(Stored.Values.Select(p => p.Clone()).ToList());
    }

    // ---- users ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Users_ListIsCached_AndACreatedAccountShowsUpImmediately()
    {
        var inner = new FakeUsers();
        var cache = new CachedUserRepository(inner, Ttl, () => _now);

        await cache.ListAsync();
        await cache.ListAsync();
        Assert.Equal(1, inner.ListCalls);

        await cache.CreateAsync(new User { Email = "a@example.com" });
        Assert.Single(await cache.ListAsync());
        Assert.Equal(2, inner.ListCalls);
    }

    [Fact]
    public async Task Users_CallersGetTheirOwnCopies()
    {
        var inner = new FakeUsers();
        inner.Stored.Add(new User { Email = "a@example.com", DisplayName = "Original" });
        var cache = new CachedUserRepository(inner, Ttl, () => _now);

        (await cache.ListAsync())[0].DisplayName = "Tampered";
        (await cache.ListAsync())[0].DisplayName = "Tampered again";

        Assert.Equal("Original", (await cache.ListAsync())[0].DisplayName);
    }

    // ---- profiles -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Profiles_RepeatedReadsHitStorageOnce()
    {
        var inner = new FakeProfiles();
        inner.Stored["u1"] = new UserProfile { UserId = "u1", DisplayName = "Pat", Role = "Admin" };
        var cache = new CachedUserProfileRepository(inner, Ttl, () => _now);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal("Admin", (await cache.GetAsync("u1"))!.Role);
        }

        Assert.Equal(1, inner.GetCalls);
    }

    [Fact]
    public async Task Profiles_DisablingAnAccount_TakesEffectAtOnce()
    {
        var inner = new FakeProfiles();
        inner.Stored["u1"] = new UserProfile { UserId = "u1", DisplayName = "Pat" };
        var cache = new CachedUserProfileRepository(inner, Ttl, () => _now);
        Assert.False((await cache.GetAsync("u1"))!.IsDisabled);

        await cache.SetDisabledAsync("u1", true);

        Assert.True((await cache.GetAsync("u1"))!.IsDisabled);
    }

    [Fact]
    public async Task Profiles_ARoleChange_TakesEffectAtOnce()
    {
        var inner = new FakeProfiles();
        inner.Stored["u1"] = new UserProfile { UserId = "u1", DisplayName = "Pat" };
        var cache = new CachedUserProfileRepository(inner, Ttl, () => _now);
        Assert.False((await cache.GetAsync("u1"))!.Role == "Admin");

        await cache.SetRoleAsync("u1", "Admin");

        Assert.True((await cache.GetAsync("u1"))!.Role == "Admin");
    }

    [Fact]
    public async Task Profiles_AChangeMadeElsewhere_IsSeenAfterTheTimeToLive()
    {
        var inner = new FakeProfiles();
        inner.Stored["u1"] = new UserProfile { UserId = "u1", DisplayName = "Pat" };
        var cache = new CachedUserProfileRepository(inner, Ttl, () => _now);
        Assert.False((await cache.GetAsync("u1"))!.IsDisabled);

        inner.Stored["u1"].IsDisabled = true; // another server did this
        Assert.False((await cache.GetAsync("u1"))!.IsDisabled);

        _now = _now.AddSeconds(31);
        Assert.True((await cache.GetAsync("u1"))!.IsDisabled);
    }

    [Fact]
    public async Task Profiles_EnsureOnEveryPageLoad_OnlyWritesWhenTheNameChanges()
    {
        var inner = new FakeProfiles();
        var cache = new CachedUserProfileRepository(inner, Ttl, () => _now);

        await cache.EnsureProfileAsync("u1", "Pat");
        await cache.EnsureProfileAsync("u1", "Pat");
        await cache.EnsureProfileAsync("u1", "Pat");
        Assert.Equal(1, inner.EnsureCalls);

        await cache.EnsureProfileAsync("u1", "Patrick");
        Assert.Equal(2, inner.EnsureCalls);
    }

    [Fact]
    public async Task Profiles_CallersGetTheirOwnCopies()
    {
        var inner = new FakeProfiles();
        inner.Stored["u1"] = new UserProfile { UserId = "u1", DisplayName = "Pat", Role = "Member" };
        var cache = new CachedUserProfileRepository(inner, Ttl, () => _now);

        (await cache.GetAsync("u1"))!.Role = "Admin";

        Assert.Equal("Member", (await cache.GetAsync("u1"))!.Role);
    }
}
