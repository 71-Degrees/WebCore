/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Storage;

namespace WebCore.Tests.Storage;

public sealed class FileDocumentStoreTests : IDisposable
{
    private sealed record Note(string Text);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tn-docstore-" + Guid.NewGuid().ToString("N"));
    private readonly FileDocumentStore _store;

    public FileDocumentStoreTests() => _store = new FileDocumentStore(_folder);

    [Fact]
    public async Task A_saved_document_reads_back_and_a_missing_one_is_null()
    {
        await _store.SetAsync("a.json", new Note("hello"));

        Assert.Equal("hello", (await _store.GetAsync<Note>("a.json"))?.Text);
        Assert.Null(await _store.GetAsync<Note>("missing.json"));
    }

    [Fact]
    public async Task A_corrupt_document_is_treated_as_absent()
    {
        await File.WriteAllTextAsync(Path.Combine(_folder, "bad.json"), "{ not json");

        Assert.Null(await _store.GetAsync<Note>("bad.json"));
    }

    [Fact]
    public async Task Saving_again_replaces_the_document_and_leaves_no_temp_files()
    {
        await _store.SetAsync("a.json", new Note("one"));
        await _store.SetAsync("a.json", new Note("two"));

        Assert.Equal("two", (await _store.GetAsync<Note>("a.json"))?.Text);
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public async Task Delete_reports_whether_there_was_anything_to_delete()
    {
        await _store.SetAsync("a.json", new Note("x"));

        Assert.True(await _store.DeleteAsync("a.json"));
        Assert.False(await _store.DeleteAsync("a.json"));
    }

    [Fact]
    public async Task Listing_gives_each_document_a_version_that_changes_with_its_content()
    {
        await _store.SetAsync("a.json", new Note("short"));
        var before = await VersionsAsync();

        await _store.SetAsync("a.json", new Note("a rather longer note than before"));
        var after = await VersionsAsync();

        Assert.Equal(new[] { "a.json" }, after.Keys);
        Assert.NotEqual(before["a.json"], after["a.json"]);
    }

    private async Task<Dictionary<string, string>> VersionsAsync()
    {
        var versions = new Dictionary<string, string>();
        await foreach (var (name, version) in _store.ListNamesWithVersionAsync())
        {
            versions[name] = version;
        }

        return versions;
    }

    [Fact]
    public async Task Saving_while_others_read_the_same_document_never_fails()
    {
        // Windows refuses to rename a file over one that is open for reading unless the reader allows it; a save in the writer used to fail with "Access to the path is denied" while a reader was mid-read.
        await _store.SetAsync("busy.json", new Note("start"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                Assert.NotNull(await _store.GetAsync<Note>("busy.json"));
                await Task.Delay(5);   // a poll, not a spin: readers that never let go starve any rename on Windows
            }
        }));
        var writer = Task.Run(async () =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                await _store.SetAsync("busy.json", new Note("v" + i));
            }
        });

        await Task.WhenAll(readers.Append(writer));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
