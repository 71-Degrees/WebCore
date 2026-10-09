/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;
using WebCore.Hosting.Assets;

namespace WebCore.Hosting.Tests;

public class StaticAssetVersionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "TriviaNightAssets_" + Guid.NewGuid().ToString("N"));

    public StaticAssetVersionTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private StaticAssetVersion Versioner() => new(new PhysicalFileProvider(_folder));

    [Fact]
    public void AFile_GetsAContentHashQueryString()
    {
        File.WriteAllText(Path.Combine(_folder, "TriviaNight.Web.styles.css"), "body { color: red; }");

        var url = Versioner().Url("TriviaNight.Web.styles.css");

        Assert.Matches(new Regex(@"^TriviaNight\.Web\.styles\.css\?v=[0-9a-f]{8}$"), url);
    }

    [Fact]
    public void TheVersion_ChangesWhenTheContentChanges_AndNotOtherwise()
    {
        var path = Path.Combine(_folder, "app.css");
        File.WriteAllText(path, "a");
        var first = Versioner().Url("app.css");
        var same = Versioner().Url("app.css");
        File.WriteAllText(path, "b");
        var changed = Versioner().Url("app.css");

        Assert.Equal(first, same);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void AMissingFile_IsLeftWithoutAVersion()
    {
        Assert.Equal("nope.css", Versioner().Url("nope.css"));
    }

    [Fact]
    public void AFileInASubfolder_IsVersionedToo()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "js"));
        File.WriteAllText(Path.Combine(_folder, "js", "avatar.js"), "x");

        Assert.Matches(new Regex(@"^js/avatar\.js\?v=[0-9a-f]{8}$"), Versioner().Url("js/avatar.js"));
    }
}
