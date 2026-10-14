/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Deploy.Tests;

/// <summary>Reporting a verify result onward is a courtesy: every observer is told, and one that fails never stops the others or the deploy.</summary>
public class VerifyObserverTests
{
    private sealed class Recording : IVerifyObserver
    {
        public List<(string Target, int Outcomes, bool AfterDeploy)> Told { get; } = [];

        public Task ObserveAsync(VerifyContext context, IReadOnlyList<VerifyOutcome> outcomes, bool afterDeploy)
        {
            Told.Add((context.TargetName, outcomes.Count, afterDeploy));
            return Task.CompletedTask;
        }
    }

    private sealed class Failing : IVerifyObserver
    {
        public Task ObserveAsync(VerifyContext context, IReadOnlyList<VerifyOutcome> outcomes, bool afterDeploy) =>
            throw new HttpRequestException("the dashboard is down");
    }

    [Fact]
    public async Task EveryObserverIsTold_AndAFailingOneDoesNotStopTheRest()
    {
        using var http = new HttpClient();
        var context = new VerifyContext(http, "https://site.example", "prod");
        var after = new Recording();
        VerifyOutcome[] outcomes = [new(VerifyResult.Pass, "a", "ok"), new(VerifyResult.Warn, "b", "look")];

        await VerifyCommands.NotifyObserversAsync([new Failing(), after], context, outcomes, afterDeploy: true);

        Assert.Equal(("prod", 2, true), Assert.Single(after.Told));
    }
}
