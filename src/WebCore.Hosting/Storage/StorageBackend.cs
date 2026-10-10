/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using Microsoft.Extensions.Configuration;
using WebCore.Storage;
using WebCore.Storage.Blob;

namespace WebCore.Hosting.Storage;

/// <summary>One kind of stored data: its blob container, the setting that moves its local folder, and that folder's default name under <c>App_Data</c>. An app lists its own specs in one place.</summary>
public sealed record StoreSpec(string Container, string FolderSetting, string DefaultFolder);

/// <summary>
/// Where the app keeps its data: blob storage in production, a folder under <c>App_Data</c> everywhere else - decided once here, so no repository registration has to know which.
/// Local disk on the App Service is not guaranteed to survive every restart on the Free tier (an Event was lost to exactly this), so when <c>Storage:ConnectionString</c> and <c>Storage:UseBlobStorage</c>
/// are both set, every store is a blob container instead.
/// They are deliberately two switches, not one, for a safe rollout of existing data: the connection string alone lets the one-time migration endpoint reach blob storage and copy local files into it WITHOUT
/// changing what the live app reads from; that only happens once <c>UseBlobStorage</c> is also turned on after the migration has run and been checked. One combined switch would force migrating and cutting
/// over in the same deploy, which is exactly the kind of step that already lost data once.
/// </summary>
public sealed class StorageBackend(IConfiguration configuration, string contentRoot)
{
    /// <summary>The blob storage connection string, or null/blank when none is configured.</summary>
    public string? BlobConnectionString { get; } = configuration["Storage:ConnectionString"];

    /// <summary>A connection string is set, so blob storage can be reached (by the migration endpoint, the key ring) whether or not the app reads from it yet.</summary>
    public bool BlobReachable => !string.IsNullOrWhiteSpace(BlobConnectionString);

    /// <summary>The app reads and writes blob storage rather than local folders.</summary>
    public bool UseBlob { get; } = !string.IsNullOrWhiteSpace(configuration["Storage:ConnectionString"]) && configuration.GetValue<bool>("Storage:UseBlobStorage");

    /// <summary>The local folder for a store: the configured one (relative paths from the content root), else the default under <c>App_Data</c>.</summary>
    public string LocalFolder(StoreSpec spec)
    {
        var configured = configuration[spec.FolderSetting];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Path.Combine(contentRoot, "App_Data", spec.DefaultFolder);
        }

        return Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured);
    }

    /// <summary>JSON documents for this kind of data, in a blob container or a local folder.</summary>
    public IDocumentStore Documents(StoreSpec spec) => UseBlob ? new BlobJsonStore(BlobConnectionString!, spec.Container) : new FileDocumentStore(LocalFolder(spec));

    /// <summary>Raw files (logos, crops, clips) for this kind of data, in a blob container or a local folder.</summary>
    public IBinaryStore Files(StoreSpec spec) => UseBlob ? new BlobJsonStore(BlobConnectionString!, spec.Container) : new FileBinaryStore(LocalFolder(spec));

    /// <summary>A second, related set of documents (an index beside the main records): a second container in blob storage, a subfolder beside the main folder locally.</summary>
    public IDocumentStore Related(StoreSpec spec, string container, string subfolder) =>
        UseBlob ? new BlobJsonStore(BlobConnectionString!, container) : new FileDocumentStore(Path.Combine(LocalFolder(spec), subfolder));
}
