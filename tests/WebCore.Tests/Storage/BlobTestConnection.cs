/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Tests.Storage;

/// <summary>
/// There is no Azurite/emulator dependency, so the blob-backed tests run against a real storage account - read from an environment variable rather than checked-in config so the connection string
/// (and the account it points at) is never committed. Tests that need it carry <c>[BlobFact]</c>, which marks them skipped (not passed) when it is absent, so a machine without it configured just skips
/// these rather than failing the whole suite. <c>WEBCORE_TEST_BLOB_CONNECTION_STRING</c>, or the older <c>TRIVIANIGHT_TEST_BLOB_CONNECTION_STRING</c> some machines already have.
/// </summary>
internal static class BlobTestConnection
{
    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable("WEBCORE_TEST_BLOB_CONNECTION_STRING") ?? Environment.GetEnvironmentVariable("TRIVIANIGHT_TEST_BLOB_CONNECTION_STRING");

    public static bool RequireOrSkip() => !string.IsNullOrWhiteSpace(ConnectionString);
}
