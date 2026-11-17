/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using WebCore.Accounts;
using WebCore.Storage;

namespace WebCore.Tests.Accounts;

public sealed class TypedProfileTests : IDisposable
{
    /// <summary>An app's own profile, shaped like Better Trivia's: a field of its own and one that defaults to on.</summary>
    private sealed class TeamProfile : UserProfile
    {
        public string? TeamName { get; set; }

        public bool ShowOnLeaderboard { get; set; } = true;
    }

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WebCoreTypedProfiles_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private UserProfileRepository<TeamProfile> Repository() => new(new FileDocumentStore(_folder), TestRoles.MemberAdmin);

    [Fact]
    public async Task AFieldMissingFromAnOldDocument_TakesTheClassDefault()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path.Combine(_folder, DocumentNames.For("u1", "user")), """{ "UserId": "u1", "DisplayName": "Sam", "Role": 0 }""");

        var profile = await Repository().GetAsync("u1");

        Assert.True(profile!.ShowOnLeaderboard);
        Assert.Equal("Member", profile.Role);
        Assert.Null(profile.UnknownFields);
    }

    [Fact]
    public async Task TheAppsFields_SurviveWebCoresOwnWrites()
    {
        var repository = Repository();
        await repository.EnsureProfileAsync("u1", "Sam");
        await repository.UpdateAsync("u1", p => p.TeamName = "Quiz Khalifa");

        IUserProfileRepository plain = repository;
        await plain.SetRoleAsync("u1", "Admin");
        await plain.SetDisabledAsync("u1", true);
        await plain.EnsureProfileAsync("u1", "Sam S");

        var profile = await repository.GetAsync("u1");
        Assert.Equal("Quiz Khalifa", profile!.TeamName);
        Assert.Equal("Admin", profile.Role);
        Assert.True(profile.IsDisabled);
        Assert.Equal("Sam S", profile.DisplayName);
    }

    [Fact]
    public async Task TheCache_AnswersBothShapes_FromTheSameCopies_AndAnUpdateClearsIt()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        using var cache = new CachedUserProfileRepository<TeamProfile>(Repository(), TimeSpan.FromMinutes(5), () => now);
        await cache.EnsureProfileAsync("u1", "Sam");
        Assert.Null((await cache.GetAsync("u1"))!.TeamName);

        await cache.UpdateAsync("u1", p => p.TeamName = "Quiz Khalifa");

        Assert.Equal("Quiz Khalifa", (await cache.GetAsync("u1"))!.TeamName);
        var seenByWebCore = await ((IUserProfileRepository)cache).GetAsync("u1");
        Assert.Equal("Quiz Khalifa", Assert.IsType<TeamProfile>(seenByWebCore).TeamName);
        Assert.Single(await ((IUserProfileRepository)cache).ListAllAsync());
    }

    [Fact]
    public async Task ACallersChange_NeverReachesTheCachedCopy()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        using var cache = new CachedUserProfileRepository<TeamProfile>(Repository(), TimeSpan.FromMinutes(5), () => now);
        await cache.EnsureProfileAsync("u1", "Sam");

        (await cache.GetAsync("u1"))!.TeamName = "changed by a caller";

        Assert.Null((await cache.GetAsync("u1"))!.TeamName);
    }
}
