/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using Microsoft.Extensions.Configuration;
using WebCore.Hosting.Storage;
using WebCore.Storage;

namespace WebCore.Hosting.Tests;

public class StorageBackendTests
{
    private static readonly StoreSpec Notes = new("notes", "Storage:NotesFolder", "notes");

    private static StorageBackend Backend(params (string Key, string Value)[] settings) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value)).Build(), Path.Combine(Path.GetTempPath(), "app-root"));

    [Fact]
    public void WithNothingConfiguredDataLivesUnderAppData()
    {
        var backend = Backend();

        Assert.False(backend.UseBlob);
        Assert.False(backend.BlobReachable);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "app-root", "App_Data", "notes"), backend.LocalFolder(Notes));
        Assert.IsType<FileDocumentStore>(backend.Documents(Notes));
        Assert.IsType<FileBinaryStore>(backend.Files(Notes));
    }

    [Fact]
    public void AConfiguredFolderIsUsedAndRelativePathsStartAtTheContentRoot()
    {
        Assert.Equal(Path.Combine(Path.GetTempPath(), "app-root", "data", "n"), Backend(("Storage:NotesFolder", Path.Combine("data", "n"))).LocalFolder(Notes));

        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere");
        Assert.Equal(absolute, Backend(("Storage:NotesFolder", absolute)).LocalFolder(Notes));
    }

    [Fact]
    public void ABlobConnectionAloneReachesBlobStorageButDoesNotSwitchToIt()
    {
        var backend = Backend(("Storage:ConnectionString", "UseDevelopmentStorage=true"));

        Assert.True(backend.BlobReachable);
        Assert.False(backend.UseBlob);
        Assert.IsType<FileDocumentStore>(backend.Documents(Notes));
    }

    [Fact]
    public void BothSwitchesAreNeededToUseBlobStorage()
    {
        Assert.True(Backend(("Storage:ConnectionString", "UseDevelopmentStorage=true"), ("Storage:UseBlobStorage", "true")).UseBlob);
        Assert.False(Backend(("Storage:UseBlobStorage", "true")).UseBlob);
    }
}
