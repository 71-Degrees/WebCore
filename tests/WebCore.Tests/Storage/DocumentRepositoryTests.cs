/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Storage;

namespace WebCore.Tests.Storage;

public sealed class DocumentRepositoryTests : IDisposable
{
    private sealed class Note
    {
        public string Id { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NoteRepository(IDocumentStore store, TimeProvider time)
        : DocumentRepository<Note>(store, "note", new DocumentShape<Note>(n => n.Id, (n, id) => n.Id = id, (n, at) => n.UpdatedAt = at), time);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "webcore-docrepo-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ASavedRecordGetsAnIdAndTheTimeFromTheTimeProvider()
    {
        var at = new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.Zero);
        var repository = new NoteRepository(new FileDocumentStore(_folder), new FixedTime(at));

        var saved = await repository.SaveAsync(new Note { Text = "hello" });
        var loaded = await repository.GetAsync(saved.Id);

        Assert.False(string.IsNullOrEmpty(saved.Id));
        Assert.Equal(at, saved.UpdatedAt);
        Assert.Equal("hello", loaded!.Text);
        Assert.Equal(at, loaded.UpdatedAt);
    }

    [Fact]
    public async Task ADeletedRecordIsGone()
    {
        var repository = new NoteRepository(new FileDocumentStore(_folder), TimeProvider.System);
        var saved = await repository.SaveAsync(new Note { Text = "bye" });

        Assert.True(await repository.DeleteAsync(saved.Id));
        Assert.Null(await repository.GetAsync(saved.Id));
        Assert.False(await repository.DeleteAsync(saved.Id));
    }
}
