/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Deploy.Tests;

public sealed class DeployConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "webcore-config-" + Guid.NewGuid().ToString("N"));

    public DeployConfigTests() => Directory.CreateDirectory(Path.Combine(_root, "deploy"));

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Write(string json) => File.WriteAllText(DeployConfig.PathFor(_root), json);

    [Fact]
    public void TheSettingsAreReadWithComments()
    {
        Write("""{ "app": "Shop", "webProject": "src/Shop.Web", /* note */ "targets": { "prod": { "app": "a", "group": "g", "url": "https://x", "keyEnv": "K" } } }""");

        var config = DeployConfig.Load(_root);

        Assert.Equal("Shop", config.App);
        Assert.Equal("win-x86", config.Runtime);
        Assert.Equal("/version", config.Endpoints.Version);
        Assert.Equal("https://x", config.Targets["prod"].Url);
        Assert.Equal(new[] { "src/Shop.Web" }, config.CompileProjects);
    }

    [Theory]
    [InlineData("""{ "webProject": "src/Web" }""")]
    [InlineData("""{ "app": "Shop" }""")]
    public void TheAppAndWebProjectAreRequired(string json)
    {
        Write(json);

        Assert.Throws<InvalidOperationException>(() => DeployConfig.Load(_root));
    }

    [Fact]
    public void LiveSessionsAreAssumedUnlessTheAppSaysItHasNone()
    {
        Write("""{ "app": "A", "webProject": "w" }""");
        Assert.True(DeployConfig.Load(_root).LiveSessions);

        Write("""{ "app": "A", "webProject": "w", "liveSessions": false }""");
        Assert.False(DeployConfig.Load(_root).LiveSessions);
    }

    [Fact]
    public void WithNoFileTheMessageSaysWhereToPutOne()
    {
        var error = Assert.Throws<InvalidOperationException>(() => DeployConfig.Load(_root));

        Assert.Contains("deploy.json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TestProjectsDefaultToEveryTestsFolderThatHoldsAProject()
    {
        Directory.CreateDirectory(Path.Combine(_root, "tests", "B.Tests"));
        Directory.CreateDirectory(Path.Combine(_root, "tests", "A.Tests"));
        Directory.CreateDirectory(Path.Combine(_root, "tests", "Docs"));
        File.WriteAllText(Path.Combine(_root, "tests", "B.Tests", "B.Tests.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "tests", "A.Tests", "A.Tests.csproj"), "<Project />");

        var projects = new DeployConfig { Tests = null }.TestProjects(_root);

        Assert.Equal(new[] { "tests/A.Tests", "tests/B.Tests" }, projects);
        Assert.Equal(new[] { "tests/Only" }, new DeployConfig { Tests = new[] { "tests/Only" } }.TestProjects(_root));
    }
}
