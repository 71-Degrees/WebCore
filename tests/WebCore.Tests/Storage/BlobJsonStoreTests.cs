/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Storage.Blob;

namespace WebCore.Tests.Storage;

public class BlobJsonStoreTests
{
    private sealed record Widget(string Id, string Name);

    private static BlobJsonStore CreateStore() =>
        new(BlobTestConnection.ConnectionString!, "test-blob-json-store");

    [BlobFact]
    public async Task SetAsync_ThenGetAsync_RoundTrips()
    {
        var store = CreateStore();
        var blobName = $"{Guid.NewGuid():N}.json";
        try
        {
            await store.SetAsync(blobName, new Widget("w1", "Gadget"));

            var found = await store.GetAsync<Widget>(blobName);

            Assert.Equal(new Widget("w1", "Gadget"), found);
        }
        finally
        {
            await store.DeleteAsync(blobName);
        }
    }

    [BlobFact]
    public async Task GetAsync_UnknownBlob_ReturnsNull()
    {
        var store = CreateStore();
        var found = await store.GetAsync<Widget>($"no-such-{Guid.NewGuid():N}.json");

        Assert.Null(found);
    }

    [BlobFact]
    public async Task DeleteAsync_RemovesTheBlob()
    {
        var store = CreateStore();
        var blobName = $"{Guid.NewGuid():N}.json";
        await store.SetAsync(blobName, new Widget("w1", "Gadget"));

        var deleted = await store.DeleteAsync(blobName);
        var foundAfter = await store.GetAsync<Widget>(blobName);

        Assert.True(deleted);
        Assert.Null(foundAfter);
    }

    [BlobFact]
    public async Task TrySetIfNotExistsAsync_BlobAlreadyExists_ReturnsFalseAndDoesNotOverwrite()
    {
        var store = CreateStore();
        var blobName = $"{Guid.NewGuid():N}.json";
        try
        {
            var firstWrite = await store.TrySetIfNotExistsAsync(blobName, new Widget("w1", "Original"));
            var secondWrite = await store.TrySetIfNotExistsAsync(blobName, new Widget("w1", "Overwrite"));
            var found = await store.GetAsync<Widget>(blobName);

            Assert.True(firstWrite);
            Assert.False(secondWrite);
            Assert.Equal("Original", found!.Name);
        }
        finally
        {
            await store.DeleteAsync(blobName);
        }
    }

    [BlobFact]
    public async Task ListAsync_ReturnsEveryStoredRecord()
    {
        var store = CreateStore();
        var names = new[] { $"{Guid.NewGuid():N}.json", $"{Guid.NewGuid():N}.json" };
        try
        {
            await store.SetAsync(names[0], new Widget("a", "A"));
            await store.SetAsync(names[1], new Widget("b", "B"));

            var ids = new List<string>();
            await foreach (var (name, widget) in store.ListAsync<Widget>())
            {
                if (names.Contains(name))
                {
                    ids.Add(widget.Id);
                }
            }

            Assert.Contains("a", ids);
            Assert.Contains("b", ids);
        }
        finally
        {
            await store.DeleteAsync(names[0]);
            await store.DeleteAsync(names[1]);
        }
    }

    [BlobFact]
    public async Task GetBytesAsync_ThenSetBytesAsync_RoundTrips()
    {
        var store = CreateStore();
        var blobName = $"{Guid.NewGuid():N}.bin";
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        try
        {
            await store.SetBytesAsync(blobName, bytes);
            var found = await store.GetBytesAsync(blobName);

            Assert.Equal(bytes, found);
        }
        finally
        {
            await store.DeleteAsync(blobName);
        }
    }
}
