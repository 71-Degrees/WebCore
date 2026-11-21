/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using WebCore.Storage;

namespace WebCore.Tests.Adversarial;

// Document names, the file document and binary stores, and the document repository, with bad names, corrupt files and missing folders.
public sealed class StorageAdversarialTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- Storage name and document edges ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a b")]
    [InlineData("../x")]
    [InlineData("a.json")]
    public void DocumentNames_InvalidIds_AreRefused(string? id)
    {
        Assert.False(DocumentNames.IsValid(id));
        Assert.Throws<ArgumentException>(() => DocumentNames.For(id!, "thing"));
    }

    [Fact]
    public void DocumentNames_LongAndUnderscoredIdsAreAccepted()
    {
        Assert.True(DocumentNames.IsValid(new string('a', 1000)));
        Assert.Equal("a_b-c.json", DocumentNames.For("a_b-c", "thing"));
    }

    [Fact]
    public async Task FileDocumentStore_SlashInAName_IsSilentlyCutToTheLastSegment_Finding()
    {
        // Path.GetFileName drops the folder part, so "a/doc" and "doc" are the same document.
        var store = new FileDocumentStore(_temp.Path);
        await store.SetAsync("a/doc", 7);

        Assert.Equal(7, await store.GetAsync<int>("doc"));
    }

    [Fact]
    public async Task FileDocumentStore_EmptyName_ThrowsRatherThanFindingTheFolder()
    {
        var store = new FileDocumentStore(_temp.Path);

        await Assert.ThrowsAnyAsync<Exception>(() => store.GetAsync<int>(string.Empty));
    }

    [Fact]
    public async Task FileDocumentStore_EmptyOrCorruptFile_ReadsAsAbsent()
    {
        var store = new FileDocumentStore(_temp.Path);
        Directory.CreateDirectory(_temp.Path);
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "empty.json"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "broken.json"), "{ not json");

        Assert.Null(await store.GetAsync<string>("empty.json"));
        Assert.Null(await store.GetAsync<string>("broken.json"));
    }

    [Fact]
    public async Task FileDocumentStore_WrongTypeForTheStoredJson_ReadsAsAbsentNotAnError_Finding()
    {
        // A string saved and read back as an int is quietly "not there", which hides a type mismatch from the caller.
        var store = new FileDocumentStore(_temp.Path);
        await store.SetAsync("doc", "text");

        Assert.Equal(0, await store.GetAsync<int>("doc"));
    }

    [Fact]
    public async Task FileDocumentStore_SavingNull_ReadsBackAsNullAndIsSkippedInListings()
    {
        var store = new FileDocumentStore(_temp.Path);
        await store.SetAsync<string?>("nothing", null);

        Assert.Null(await store.GetAsync<string>("nothing"));
        Assert.Empty(await CollectAsync(store.ListAsync<string>()));
    }

    [Fact]
    public async Task FileDocumentStore_TrySetIfNotExists_KeepsTheFirstValue()
    {
        var store = new FileDocumentStore(_temp.Path);

        Assert.True(await store.TrySetIfNotExistsAsync("once", "first"));
        Assert.False(await store.TrySetIfNotExistsAsync("once", "second"));
        Assert.Equal("first", await store.GetAsync<string>("once"));
    }

    [Fact]
    public async Task FileDocumentStore_DeletingAMissingName_ReportsFalse()
    {
        var store = new FileDocumentStore(_temp.Path);

        Assert.False(await store.DeleteAsync("never-there"));
        Assert.False(await store.DeleteAsync(string.Empty));
    }

    [Fact]
    public async Task FileBinaryStore_EmptyBytes_RoundTrip_AndMissingIsNull()
    {
        var store = new FileBinaryStore(_temp.Path);

        await store.SetBytesAsync("blank", Array.Empty<byte>());

        Assert.Empty((await store.GetBytesAsync("blank"))!);
        Assert.Null(await store.GetBytesAsync("missing"));
    }

    [Fact]
    public async Task FileBinaryStore_NullBytes_ThrowsArgumentNull()
    {
        var store = new FileBinaryStore(_temp.Path);

        await Assert.ThrowsAnyAsync<ArgumentNullException>(() => store.SetBytesAsync("x", null!));
    }

    [Fact]
    public async Task FileBinaryStore_FolderRemovedUnderneath_IsRecreatedOnWrite()
    {
        var store = new FileBinaryStore(_temp.Path);
        Directory.Delete(_temp.Path, recursive: true);

        await store.SetBytesAsync("back", [1, 2, 3]);

        Assert.Equal([1, 2, 3], await store.GetBytesAsync("back"));
    }

    [Fact]
    public async Task FileBinaryStore_ListingSkipsTempFiles()
    {
        var store = new FileBinaryStore(_temp.Path);
        await store.SetBytesAsync("real", [1]);
        await File.WriteAllBytesAsync(Path.Combine(_temp.Path, "leftover.abc.tmp"), [2]);

        var names = await CollectAsync(store.ListNamesWithModifiedAsync());

        Assert.Equal(["real"], names.Select(n => n.Name));
    }

    [Fact]
    public async Task DocumentRepository_BlankIdOnSave_GetsAFreshGuid()
    {
        var store = new FileDocumentStore(_temp.Path);
        var repository = new ThingRepository(store);
        var thing = new Thing { Id = "   " };

        await repository.SaveAsync(thing);

        Assert.True(Guid.TryParse(thing.Id, out _));
    }

    [Fact]
    public async Task DocumentRepository_InvalidIdOnSave_IsRefusedBeforeAnythingIsWritten()
    {
        var store = new FileDocumentStore(_temp.Path);
        var repository = new ThingRepository(store);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveAsync(new Thing { Id = "../evil" }));
        Assert.Empty(await CollectAsync(store.ListAsync<Thing>()));
    }

    [Fact]
    public async Task DocumentRepository_LenientIds_AnswerNullForABadIdWithoutTouchingTheStore()
    {
        var repository = new ThingRepository(new FileDocumentStore(_temp.Path), lenient: true);

        Assert.Null(await repository.GetAsync("../evil"));
    }

    [Fact]
    public async Task DocumentRepository_StrictIds_ThrowForABadId()
    {
        var repository = new ThingRepository(new FileDocumentStore(_temp.Path));

        await Assert.ThrowsAsync<ArgumentException>(() => repository.GetAsync("../evil"));
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source)
        {
            items.Add(item);
        }

        return items;
    }

    private sealed class Thing
    {
        public string Id { get; set; } = string.Empty;
    }

    private sealed class ThingRepository : DocumentRepository<Thing>
    {
        public ThingRepository(IDocumentStore store, bool lenient = false)
            : base(store, "thing", new DocumentShape<Thing>(t => t.Id, (t, id) => t.Id = id, LenientIds: lenient))
        {
        }
    }
}
