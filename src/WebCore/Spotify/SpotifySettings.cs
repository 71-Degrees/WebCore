/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Spotify;

/// <summary>
/// The app's Spotify credentials for the client-credentials flow (catalog reads only, no user). They come from configuration (<c>Spotify:ClientId</c>,
/// <c>Spotify:ClientSecret</c>), never from code. Without both the client answers <see cref="WebCore.Metadata.MetadataStatus.Failed"/> and calls nothing.
/// </summary>
public sealed class SpotifySettings
{
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
