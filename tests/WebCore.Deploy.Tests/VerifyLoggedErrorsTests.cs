/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Deploy.Tests;

/// <summary>
/// The "errors logged since deploy" check counts only what the running process logged: a zip deploy copies files over the old process, and a request in that
/// window logs a BadImageFormatException that is noise from the copy, not a fault of the new build.
/// </summary>
public class VerifyLoggedErrorsTests
{
    [Fact]
    public void TheStartTime_IsReadFromTheVersionEndpoint()
    {
        var started = VerifyCommands.TryStartedAt("""{"version":"1.0.0+b94f08cb","startedAt":"2026-11-28T15:39:03.5121337+00:00"}""");

        Assert.Equal(new DateTimeOffset(2026, 11, 28, 15, 39, 3, TimeSpan.Zero).AddTicks(5121337), started);
    }

    [Theory]
    [InlineData("""{"version":"1.0.0"}""")]
    [InlineData("""{"startedAt":"not a time"}""")]
    [InlineData("[1,2]")]
    [InlineData("<html>stopped</html>")]
    public void NoUsableStartTime_GivesNull(string body) => Assert.Null(VerifyCommands.TryStartedAt(body));

    [Fact]
    public void WithAStartTime_TheQueryCountsNothingBeforeIt()
    {
        var query = VerifyCommands.LoggedErrorsQuery("cratedigger", 30, new DateTimeOffset(2026, 11, 28, 15, 39, 3, 512, TimeSpan.Zero));

        Assert.Contains("TimeGenerated > ago(30m) and TimeGenerated >= datetime(2026-11-28T15:39:03.512Z) and AppRoleName == 'cratedigger'", query, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAStartTime_TheQueryKeepsTheMinutesWindowAlone()
    {
        var query = VerifyCommands.LoggedErrorsQuery("cratedigger", 30, null);

        Assert.Contains("TimeGenerated > ago(30m) and AppRoleName == 'cratedigger'", query, StringComparison.Ordinal);
    }
}
