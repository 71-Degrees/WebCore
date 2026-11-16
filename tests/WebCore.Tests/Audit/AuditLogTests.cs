/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Audit;
using WebCore.Storage;
using WebCore.Timing;

namespace WebCore.Tests.Audit;

public sealed class AuditLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "webcore-audit-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private AuditLog NewLog(AuditLogOptions? options = null) => new(new FileDocumentStore(_folder), _time, options);

    private AuditEntry Entry(string entityId, string? owner, string action = AuditEntry.Changed, string actor = "someone") => new()
    {
        At = _time.GetUtcNow(),
        Actor = new AuditActor(actor, actor, "web"),
        Kind = "record",
        EntityId = entityId,
        EntityName = "Record " + entityId,
        OwnerId = owner,
        Action = action,
    };

    [Fact]
    public async Task AnEntry_IsReadBackPerItem_PerOwner_AndInRecent_NewestFirst()
    {
        using var log = NewLog();
        await log.AppendAsync(Entry("a1", "o1", AuditEntry.Created));
        await log.AppendAsync(Entry("a1", "o1", AuditEntry.Changed));

        var item = await log.ForItemAsync("record", "a1", 10);
        var owner = await log.ForOwnerAsync("o1", 10);
        var recent = await log.RecentAsync(10);

        Assert.Equal([AuditEntry.Changed, AuditEntry.Created], item.Select(e => e.Action));
        Assert.Equal(2, owner.Count);
        Assert.Equal(2, recent.Count);
    }

    [Fact]
    public async Task AnEntryWithNoOwner_IsKeptPerItemAndInRecentOnly()
    {
        using var log = NewLog();

        await log.AppendAsync(Entry("q1", owner: null));

        Assert.Single(await log.ForItemAsync("record", "q1", 10));
        Assert.Single(await log.RecentAsync(10));
    }

    [Fact]
    public async Task EachListIsCapped_AndOldEntriesAreDropped()
    {
        using var log = NewLog(new AuditLogOptions { KeepPerItem = 2, KeepFor = TimeSpan.FromDays(1) });
        await log.AppendAsync(Entry("a1", "o1", "first"));
        _time.Advance(TimeSpan.FromDays(2));
        await log.AppendAsync(Entry("a1", "o1", "second"));
        await log.AppendAsync(Entry("a1", "o1", "third"));
        await log.AppendAsync(Entry("a1", "o1", "fourth"));

        Assert.Equal(["fourth", "third"], (await log.ForItemAsync("record", "a1", 10)).Select(e => e.Action));
        Assert.DoesNotContain(await log.RecentAsync(10), e => e.Action == "first");
    }

    [Fact]
    public async Task ForgettingAnOwner_RemovesTheirEntriesEverywhere_AndLeavesOthers()
    {
        using var log = NewLog();
        await log.AppendAsync(Entry("a1", "o1"));
        await log.AppendAsync(Entry("b1", "o2"));
        await log.AppendAsync(Entry("b1", "o2", actor: "o1"));

        var removed = await log.ForgetOwnerAsync("o1");

        Assert.Equal(4, removed);   // stored copies: a1 in the owner's list, in a1's history and in recent; the change o1 made to b1, in recent
        Assert.Empty(await log.ForOwnerAsync("o1", 10));
        Assert.Empty(await log.ForItemAsync("record", "a1", 10));
        Assert.All(await log.RecentAsync(10), e => Assert.True(e.OwnerId != "o1" && e.Actor.Id != "o1"));
        Assert.Equal(2, (await log.ForOwnerAsync("o2", 10)).Count);   // o2's own trail keeps every change to o2's records, including the one o1 made
    }

    [Theory]
    [InlineData("Quiz")]
    [InlineData("../x")]
    [InlineData("")]
    public async Task AKindThatCouldNameAnotherDocument_IsRefused(string kind)
    {
        using var log = NewLog();

        await Assert.ThrowsAsync<ArgumentException>(() => log.AppendAsync(Entry("a1", "o1") with { Kind = kind }));
    }
}

public sealed class AuditDiffTests
{
    private sealed record Item(string Title, string? Price, IReadOnlyList<int> Fingerprints, DateTimeOffset UpdatedAt);

    [Fact]
    public void AChangedField_IsNamedWithBeforeAndAfter_AndBookkeepingIsLeftOut()
    {
        var before = new Item("At Folsom Prison", "$20", [1], DateTimeOffset.MinValue);
        var after = before with { Price = "$24", UpdatedAt = DateTimeOffset.MaxValue };

        Assert.Equal([new AuditChange("Price", "$20", "$24")], AuditDiff.Compare(before, after));
    }

    [Fact]
    public void FieldsTheAppNames_AreLeftOut()
    {
        var before = new Item("I Walk the Line", null, [1, 2], DateTimeOffset.MinValue);
        var after = before with { Fingerprints = [3, 4] };

        Assert.Empty(AuditDiff.Compare(before, after, new HashSet<string>(StringComparer.Ordinal) { "Fingerprints" }));
        Assert.NotEmpty(AuditDiff.Compare(before, after));
    }
}
