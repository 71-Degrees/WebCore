/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Deploy.Tests;

public sealed class MinifierTests : IDisposable
{
    private readonly string _publish = Path.Combine(Path.GetTempPath(), "webcore-minify-" + Guid.NewGuid().ToString("N"));

    public MinifierTests() => Directory.CreateDirectory(Path.Combine(_publish, "wwwroot"));

    public void Dispose()
    {
        Directory.Delete(_publish, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AnAppOnUseStaticFiles_IsMinified()
    {
        Assert.False(Minifier.ServedFromManifest(_publish));
    }

    [Fact]
    public void AnAppOnMapStaticAssets_IsNotMinified_BecauseItsManifestWouldNoLongerMatch()
    {
        File.WriteAllText(Path.Combine(_publish, "Shop.Web.staticwebassets.endpoints.json"), "{}");

        Assert.True(Minifier.ServedFromManifest(_publish));
    }

    [Fact]
    public void AMissingPublishFolder_IsNotTreatedAsManifestServed() =>
        Assert.False(Minifier.ServedFromManifest(Path.Combine(_publish, "nothing-here")));
}
