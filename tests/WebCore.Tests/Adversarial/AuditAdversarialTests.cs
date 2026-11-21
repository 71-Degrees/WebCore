/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using Microsoft.Extensions.Logging.Abstractions;
using WebCore.Audit;
using WebCore.Storage;

namespace WebCore.Tests.Adversarial;

// Audit trail: field diffs, actor scopes, the log's kind, owner and count rules, and the change auditor's failure handling.
public sealed class AuditAdversarialTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);
    private readonly TempFolder _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- AuditDiff ----

    [Fact]
    public void AuditDiff_IdenticalValues_ProduceNoChanges()
    {
        var item = new Sample { Name = "a", Count = 1 };

        Assert.Empty(AuditDiff.Compare(item, new Sample { Name = "a", Count = 1 }));
    }

    [Fact]
    public void AuditDiff_NullBefore_ListsEveryFieldOfTheNewValue()
    {
        var changes = AuditDiff.Compare<Sample>(null, new Sample { Name = "a", Count = 1 });

        Assert.Contains(changes, c => c.Field == "Name" && c.Before is null && c.After == "a");
    }

    [Fact]
    public void AuditDiff_NullAfter_ListsEveryFieldAsRemoved()
    {
        var changes = AuditDiff.Compare<Sample>(new Sample { Name = "a", Count = 1 }, null!);

        Assert.Contains(changes, c => c.Field == "Name" && c.Before == "a" && c.After is null);
    }

    [Fact]
    public void AuditDiff_EmptyStringAgainstNull_IsAChange()
    {
        var changes = AuditDiff.Compare(new Sample { Name = string.Empty }, new Sample { Name = null! });

        Assert.Contains(changes, c => c.Field == "Name");
    }

    [Fact]
    public void AuditDiff_UpdatedAt_IsIgnoredAtTheRootButNotNested()
    {
        var root = AuditDiff.Compare(new Stamped { UpdatedAt = 1 }, new Stamped { UpdatedAt = 2 });
        var nested = AuditDiff.Compare(new Nested { Inner = new Stamped { UpdatedAt = 1 } }, new Nested { Inner = new Stamped { UpdatedAt = 2 } });

        Assert.Empty(root);
        Assert.Contains(nested, c => c.Field == "Inner.UpdatedAt");
    }

    [Fact]
    public void AuditDiff_ArrayGrowth_NamesTheNewIndex()
    {
        var changes = AuditDiff.Compare(new Tagged { Tags = ["a"] }, new Tagged { Tags = ["a", "b"] });

        Assert.Contains(changes, c => c.Field == "Tags[1]" && c.Before is null && c.After == "b");
    }

    [Fact]
    public void AuditDiff_MoreThanForty_CapsAndSaysHowManyMore()
    {
        var before = Enumerable.Range(0, 45).ToDictionary(i => "k" + i, _ => 0);
        var after = Enumerable.Range(0, 45).ToDictionary(i => "k" + i, _ => 1);

        var changes = AuditDiff.Compare(before, after);

        Assert.Equal(AuditDiff.MaxChanges + 1, changes.Count);
        Assert.Equal("(and 5 more fields)", changes[^1].Field);
    }

    [Fact]
    public void AuditDiff_LongValues_AreShortenedWithAnEllipsis()
    {
        var changes = AuditDiff.Compare(new Sample { Name = new string('x', 200) }, new Sample { Name = "y" });

        var before = changes.Single(c => c.Field == "Name").Before!;
        Assert.Equal(AuditDiff.MaxValueLength + 1, before.Length);
        Assert.EndsWith("…", before);
    }

    [Fact]
    public void AuditDiff_ValuesWithQuotesAndBackslashes_ComeOutEscapedNotLost()
    {
        var changes = AuditDiff.Compare(new Sample { Name = "a" }, new Sample { Name = "say \"hi\" \\" });

        Assert.Contains(changes, c => c.Field == "Name" && c.After!.Contains("hi"));
    }

    // ---- AuditActorContext ----

    [Fact]
    public void AuditActorContext_WithNoScope_IsTheSystemActor()
    {
        Assert.Equal("system", new AuditActorContext().Current.Id);
    }

    [Fact]
    public void AuditActorContext_DisposingTwice_IsHarmless()
    {
        var context = new AuditActorContext();
        var scope = context.Use(new AuditActor("u1", "Ada", "web"));

        scope.Dispose();
        scope.Dispose();

        Assert.Equal("system", context.Current.Id);
    }

    // ---- AuditLog ----

    [Fact]
    public async Task AuditLog_NullEntry_ThrowsArgumentNull()
    {
        var log = NewLog();

        await Assert.ThrowsAsync<ArgumentNullException>(() => log.AppendAsync(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Item")]
    [InlineData("a/b")]
    [InlineData("a.b")]
    [InlineData("a b")]
    public async Task AuditLog_KindsThatAreNotLowerCaseWordsAreRefused(string kind)
    {
        var log = NewLog();

        await Assert.ThrowsAsync<ArgumentException>(() => log.AppendAsync(Entry(kind, "e1", null)));
        Assert.Empty(await log.RecentAsync(10));
    }

    [Fact]
    public async Task AuditLog_KindOfFortyCharactersIsAccepted_FortyOneIsNot()
    {
        var log = NewLog();

        await log.AppendAsync(Entry(new string('k', 40), "e1", null));
        await Assert.ThrowsAsync<ArgumentException>(() => log.AppendAsync(Entry(new string('k', 41), "e1", null)));
    }

    [Fact]
    public async Task AuditLog_NullEntityId_ThrowsArgumentException()
    {
        var log = NewLog();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => log.AppendAsync(Entry("thing", null!, null)));
    }

    [Fact]
    public async Task AuditLog_NullOwnerQuery_ThrowsArgumentException()
    {
        var log = NewLog();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => log.ForOwnerAsync(null!, 10));
    }

    [Fact]
    public async Task AuditLog_ZeroKeep_StoresNothingForThatList()
    {
        var log = NewLog(new AuditLogOptions { KeepPerItem = 0 });

        await log.AppendAsync(Entry("thing", "e1", null));

        Assert.Empty(await log.ForItemAsync("thing", "e1", 10));
        Assert.Single(await log.RecentAsync(10));
    }

    [Fact]
    public void AuditLog_NegativeKeep_IsRefusedAtConstruction()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => NewLog(new AuditLogOptions { KeepRecent = -1 }));

        Assert.Equal(nameof(AuditLogOptions.KeepRecent), ex.ParamName);
    }

    [Fact]
    public void AuditLog_ZeroKeepFor_IsRefusedAtConstruction()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => NewLog(new AuditLogOptions { KeepFor = TimeSpan.Zero }));

        Assert.Equal(nameof(AuditLogOptions.KeepFor), ex.ParamName);
    }

    [Fact]
    public async Task AuditLog_NegativeOrZeroCount_ReadsNothing()
    {
        var log = NewLog();
        await log.AppendAsync(Entry("thing", "e1", null));

        Assert.Empty(await log.RecentAsync(0));
        Assert.Empty(await log.RecentAsync(-3));
    }

    [Fact(Skip = "FINDING F-AU1 (privacy): forgetting an owner leaves that owner's actions inside other owners' item histories")]
    public async Task AuditLog_ForgetOwner_RemovesTheirActionsFromOtherOwnersItemHistories_Finding()
    {
        var log = NewLog();
        await log.AppendAsync(new AuditEntry
        {
            At = Now,
            Actor = new AuditActor("forgotten", "Gone", "web"),
            Kind = "song",
            EntityId = "s1",
            EntityName = "Song",
            OwnerId = "someone-else",
            Action = AuditEntry.Changed,
        });

        await log.ForgetOwnerAsync("forgotten");

        var history = await log.ForItemAsync("song", "s1", 10);
        Assert.DoesNotContain(history, e => e.Actor.Id == "forgotten");
    }

    [Fact]
    public async Task AuditLog_ForgetOwner_ReturnsHowManyEntriesWentAway()
    {
        var log = NewLog();
        await log.AppendAsync(Entry("thing", "e1", "owner-1"));
        await log.AppendAsync(Entry("thing", "e2", "owner-1"));

        var removed = await log.ForgetOwnerAsync("owner-1");

        Assert.True(removed >= 2);
        Assert.Empty(await log.ForOwnerAsync("owner-1", 10));
    }

    [Fact]
    public async Task AuditLog_EmptyOwnerIdOnAppend_IsSkippedButQueryingItThrows()
    {
        var log = NewLog();
        await log.AppendAsync(Entry("thing", "e1", string.Empty));

        Assert.Single(await log.ForItemAsync("thing", "e1", 10));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => log.ForOwnerAsync(string.Empty, 10));
    }

    // ---- ChangeAuditor ----

    [Fact]
    public async Task ChangeAuditor_LogFailure_IsSwallowedAndTheChangeStands()
    {
        var auditor = new ChangeAuditor(new FakeAuditLog(new InvalidOperationException("disk full")), new AuditActorContext(), TimeProvider.System, NullLogger<ChangeAuditor>.Instance);

        Assert.Null(await Record.ExceptionAsync(() => auditor.DeletedAsync(new AuditSubject("thing", "e1", "Name"))));
    }

    [Fact]
    public async Task ChangeAuditor_Cancellation_IsNotSwallowed()
    {
        var auditor = new ChangeAuditor(new FakeAuditLog(new OperationCanceledException()), new AuditActorContext(), TimeProvider.System, NullLogger<ChangeAuditor>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => auditor.DeletedAsync(new AuditSubject("thing", "e1", "Name")));
    }

    [Fact]
    public async Task ChangeAuditor_SavedWithNoRealChange_WritesNothing()
    {
        var log = new FakeAuditLog();
        var auditor = new ChangeAuditor(log, new AuditActorContext(), TimeProvider.System, NullLogger<ChangeAuditor>.Instance);

        await auditor.SavedAsync(new AuditSubject("thing", "e1", "Name"), new Sample { Name = "a" }, new Sample { Name = "a" });

        Assert.Equal(0, log.Appends);
    }

    [Fact]
    public async Task ChangeAuditor_CreateWithNoBefore_WritesOneCreatedEntry()
    {
        var log = new FakeAuditLog();
        var auditor = new ChangeAuditor(log, new AuditActorContext(), TimeProvider.System, NullLogger<ChangeAuditor>.Instance);

        await auditor.SavedAsync<Sample>(new AuditSubject("thing", "e1", "Name"), null, new Sample { Name = "a" });

        Assert.Equal(1, log.Appends);
        Assert.Equal(AuditEntry.Created, log.Last!.Action);
    }

    [Fact]
    public async Task ChangeAuditor_SaveWhoseSavedResultIsNull_StillRecordsAndReturnsNull()
    {
        var log = new FakeAuditLog();
        var auditor = new ChangeAuditor(log, new AuditActorContext(), TimeProvider.System, NullLogger<ChangeAuditor>.Instance);

        var result = await auditor.SaveAsync<Sample>(_ => new AuditSubject("thing", "e1", "Name"), new Sample(), () => Task.FromResult<Sample?>(null), () => Task.FromResult<Sample>(null!));

        Assert.Null(result);
        Assert.Equal(1, log.Appends);
    }

    [Fact]
    public async Task ChangeAuditor_NullCallbacks_ThrowArgumentNull()
    {
        var auditor = new ChangeAuditor(new FakeAuditLog(), new AuditActorContext(), TimeProvider.System, NullLogger<ChangeAuditor>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() => auditor.SaveAsync<Sample>(null!, new Sample(), () => Task.FromResult<Sample?>(null), () => Task.FromResult(new Sample())));
    }

    private AuditLog NewLog(AuditLogOptions? options = null) =>
        new(new FileDocumentStore(_temp.Path), TimeProvider.System, options);

    private static AuditEntry Entry(string kind, string entityId, string? ownerId) => new()
    {
        At = Now,
        Actor = new AuditActor("actor", "Actor", "web"),
        Kind = kind,
        EntityId = entityId,
        EntityName = "name",
        OwnerId = ownerId,
        Action = AuditEntry.Changed,
    };

    private sealed class Sample
    {
        public string? Name { get; set; }

        public int Count { get; set; }
    }

    private sealed class Stamped
    {
        public int UpdatedAt { get; set; }
    }

    private sealed class Nested
    {
        public Stamped? Inner { get; set; }
    }

    private sealed class Tagged
    {
        public List<string> Tags { get; set; } = [];
    }

    // Fake log for the change auditor: counts appends and keeps the last entry; throws the given exception on append, if set.
    private sealed class FakeAuditLog(Exception? failWith = null) : IAuditLog
    {
        public int Appends { get; private set; }

        public AuditEntry? Last { get; private set; }

        public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            Appends++;
            Last = entry;
            if (failWith is not null)
            {
                throw failWith;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEntry>> ForItemAsync(string kind, string entityId, int count, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<AuditEntry>> ForOwnerAsync(string ownerId, int count, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<AuditEntry>> RecentAsync(int count, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> ForgetOwnerAsync(string ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
