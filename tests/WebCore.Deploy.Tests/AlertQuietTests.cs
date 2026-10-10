/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Text.Json;

namespace WebCore.Deploy.Tests;

public class AlertQuietTests
{
    private sealed class ManualTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeAzure
    {
        public List<string> Commands { get; } = new();

        public bool SubscriptionKnown { get; init; } = true;

        public (int Code, string Output) Run(string command)
        {
            Commands.Add(command);
            if (command.StartsWith("az account show", StringComparison.Ordinal))
            {
                return SubscriptionKnown ? (0, "sub-123\n") : (1, "Please run 'az login'");
            }

            return (0, "{}");
        }
    }

    private static readonly Target Prod = new("shop", "rg-shop", "https://shop.example", "KEY");

    private static AlertQuiet Quiet(FakeAzure azure, AlertQuietSettings? settings = null) =>
        new(settings ?? new AlertQuietSettings { Rules = new[] { "alert-shop-down" } }, "Shop", azure.Run, new ManualTime());

    [Fact]
    public void ItAppliesOnlyToTheTargetsAndOnlyWhenRulesAreNamed()
    {
        var azure = new FakeAzure();

        Assert.True(Quiet(azure).AppliesTo("prod"));
        Assert.False(Quiet(azure).AppliesTo("stage"));
        Assert.False(Quiet(azure, new AlertQuietSettings()).AppliesTo("prod"));
        Assert.False(new AlertQuiet(null, "Shop", azure.Run, new ManualTime()).AppliesTo("prod"));
    }

    [Fact]
    public void TheBodyMutesTheNamedRulesInTheResourceGroupForTheWindow()
    {
        var from = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

        var json = AlertQuiet.Body("sub-123", "rg-shop", new[] { "alert-shop-down" }, from, from.AddMinutes(25), "why");

        using var doc = JsonDocument.Parse(json);
        var properties = doc.RootElement.GetProperty("properties");
        Assert.Equal("/subscriptions/sub-123/resourceGroups/rg-shop", properties.GetProperty("scopes")[0].GetString());
        Assert.Equal("alert-shop-down", properties.GetProperty("conditions")[0].GetProperty("values")[0].GetString());
        Assert.Equal("AlertRuleName", properties.GetProperty("conditions")[0].GetProperty("field").GetString());
        Assert.Equal("2026-10-10T12:00:00", properties.GetProperty("schedule").GetProperty("effectiveFrom").GetString());
        Assert.Equal("2026-10-10T12:25:00", properties.GetProperty("schedule").GetProperty("effectiveUntil").GetString());
        Assert.Equal("RemoveAllActionGroups", properties.GetProperty("actions")[0].GetProperty("actionType").GetString());
    }

    [Fact]
    public void BeginAndSettleSetTheRuleAndCancelRemovesIt()
    {
        var azure = new FakeAzure();
        var quiet = Quiet(azure);

        Assert.True(quiet.Begin(Prod));
        Assert.True(quiet.Settle(Prod));
        Assert.True(quiet.Cancel(Prod));

        var puts = azure.Commands.Where(c => c.Contains("--method put", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, puts.Count);
        Assert.All(puts, c => Assert.Contains("resourceGroups/rg-shop/providers/Microsoft.AlertsManagement/actionRules/Shop-deploy-quiet", c, StringComparison.Ordinal));
        Assert.Single(azure.Commands, c => c.Contains("--method delete", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutALoginNothingIsChangedAndItSaysSo()
    {
        var azure = new FakeAzure { SubscriptionKnown = false };
        var quiet = Quiet(azure);

        Assert.False(quiet.Begin(Prod));
        Assert.False(quiet.Cancel(Prod));
        Assert.DoesNotContain(azure.Commands, c => c.StartsWith("az rest", StringComparison.Ordinal));
    }
}
