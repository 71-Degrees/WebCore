/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Operations;

namespace WebCore.Tests.Operations;

public class DeployGuardTests
{
    private static ServerStatus Status(params ActiveGame[] games) => new("v1", null, DateTimeOffset.UtcNow, games);

    private static ActiveGame Playing(string code, int players = 3) => new(code, "Quiz", "Playing", players, IsTest: false);

    [Fact]
    public void SessionsInProgressBlockADeployUnlessTheExactNumberIsConfirmed()
    {
        var status = Status(Playing("A1"), Playing("B2"));

        Assert.False(DeployGuard.Evaluate(status, null).Allowed);
        Assert.False(DeployGuard.Evaluate(status, 1).Allowed);
        Assert.True(DeployGuard.Evaluate(status, 2).Allowed);
    }

    [Fact]
    public void WaitingLobbiesOnlyWarnAndTestSessionsAreIgnored()
    {
        var status = Status(new ActiveGame("L1", "Quiz", "Lobby", 4, IsTest: false), new ActiveGame("T1", "Quiz", "Playing", 1, IsTest: true));

        var (allowed, message) = DeployGuard.Evaluate(status, null);

        Assert.True(allowed);
        Assert.Contains("lobby", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, status.InProgress);
        Assert.Equal(1, status.WaitingLobbies);
    }

    [Fact]
    public void NothingLiveIsSafe()
    {
        var (allowed, message) = DeployGuard.Evaluate(Status(), null);

        Assert.True(allowed);
        Assert.Contains("No live", message, StringComparison.Ordinal);
    }
}
