/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Tests.Storage;

/// <summary>
/// A <see cref="FactAttribute"/> for the tests that need a real blob account. Without the connection string the test is reported as skipped,
/// not passed, so a machine without it configured cannot mistake an unrun test for a green one.
/// </summary>
internal sealed class BlobFactAttribute : FactAttribute
{
    public BlobFactAttribute()
    {
        if (!BlobTestConnection.RequireOrSkip())
        {
            Skip = "Set WEBCORE_TEST_BLOB_CONNECTION_STRING to run the blob store tests.";
        }
    }
}
