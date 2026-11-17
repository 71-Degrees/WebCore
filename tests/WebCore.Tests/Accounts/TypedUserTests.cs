/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using WebCore.Accounts;
using WebCore.Storage;

namespace WebCore.Tests.Accounts;

public sealed class TypedUserTests : IDisposable
{
    /// <summary>An app's own account class, shaped like Better Trivia's: fields of its own that a plain account does not have.</summary>
    private sealed class AppUser : User
    {
        public string? SignupIp { get; set; }

        public int Counter { get; set; }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Start = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WebCoreTypedUsers_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private UserRepository<AppUser> Typed() => new(new FileDocumentStore(_folder));

    [Fact]
    public async Task ATypedRepository_SavesAndReadsBackItsOwnFields()
    {
        await Typed().CreateAsync(new AppUser { Email = "a@example.com", SignupIp = "10.0.0.1", Counter = 3 });

        var loaded = await Typed().GetByEmailAsync("a@example.com");

        Assert.Equal("10.0.0.1", loaded!.SignupIp);
        Assert.Equal(3, loaded.Counter);
    }

    [Fact]
    public async Task CreatingAPlainUser_ThroughThePlainInterface_StoresTheAppsClass()
    {
        var plainUser = new User { Email = "b@example.com", DisplayName = "Bea" };
        IUserRepository plain = Typed();

        var created = await plain.CreateAsync(plainUser);

        Assert.IsType<AppUser>(created);
        Assert.Equal(plainUser.Id, created.Id);
        var loaded = await Typed().GetByEmailAsync("b@example.com");
        Assert.Equal("Bea", loaded!.DisplayName);
        Assert.Null(loaded.SignupIp);
        Assert.Equal(0, loaded.Counter);
    }

    [Fact]
    public async Task ExtrasWrittenByTheTypedRepository_SurviveAPlainSaveAfterAPlainRead()
    {
        await Typed().CreateAsync(new AppUser { Email = "c@example.com", SignupIp = "1.2.3.4" });
        IUserRepository plain = Typed();

        var read = await plain.GetByEmailAsync("c@example.com");
        read!.DisplayName = "Cy";
        await plain.SaveAsync(read);

        var after = await Typed().GetByEmailAsync("c@example.com");
        Assert.Equal("Cy", after!.DisplayName);
        Assert.Equal("1.2.3.4", after.SignupIp);
    }

    [Fact]
    public async Task TheCachedTypedRepository_ListsCopies_AndAWriteClearsTheList()
    {
        var inner = Typed();
        await inner.CreateAsync(new AppUser { Email = "d@example.com", DisplayName = "Original" });
        using var cache = new CachedUserRepository<AppUser>(inner, TimeSpan.FromMinutes(5), () => Start);

        (await cache.ListAsync())[0].DisplayName = "Tampered";
        Assert.Equal("Original", (await cache.ListAsync())[0].DisplayName);

        await cache.CreateAsync(new AppUser { Email = "e@example.com" });
        Assert.Equal(2, (await cache.ListAsync()).Count);
    }

    [Fact]
    public async Task EnsureProfile_StampsCreatedAtOnlyWhenItMakesTheProfile()
    {
        var store = new FileDocumentStore(_folder);
        var first = new UserProfileRepository(store, TestRoles.MemberAdmin, new FixedClock(Start));
        var created = await first.EnsureProfileAsync("u1", "Sam");
        Assert.Equal(Start, created.CreatedAt);

        var later = new UserProfileRepository(store, TestRoles.MemberAdmin, new FixedClock(Start.AddDays(1)));
        var renamed = await later.EnsureProfileAsync("u1", "Sam S");

        Assert.Equal("Sam S", renamed.DisplayName);
        Assert.Equal(Start, renamed.CreatedAt);
    }

    [Fact]
    public async Task AProfileWithoutTheField_ReadsCreatedAtAsNull()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path.Combine(_folder, DocumentNames.For("u1", "user")), """{ "UserId": "u1", "DisplayName": "Sam", "Role": 0 }""");

        var profile = await new UserProfileRepository(new FileDocumentStore(_folder), TestRoles.MemberAdmin).GetAsync("u1");

        Assert.Null(profile!.CreatedAt);
    }
}
