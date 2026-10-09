/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Deploy;

namespace WebCore.Deploy.Tests;

public class ConsoleTests
{
    // Commands that only look: they change nothing, so they may run without a question.
    private static readonly string[] ReadOnlyCommands = { "status", "verify", "perf", "check" };

    [Fact]
    public void EveryActionHasItsOwnId()
    {
        var ids = ConsoleActions.All.Select(a => a.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void EveryActionThatChangesSomethingAsksFirst()
    {
        foreach (var action in ConsoleActions.All)
        {
            var changesSomething = !ReadOnlyCommands.Contains(action.Args[0]) && !IsSettingRead(action);
            Assert.True(changesSomething == (action.Confirm is not null), $"{action.Id}: a changing action must ask first, and a looking one must not");
        }
    }

    [Fact]
    public void EveryActionThatTouchesProductionNamesItInTheQuestion()
    {
        foreach (var action in ConsoleActions.All.Where(a => a.Confirm is not null && a.Args.Contains("prod")))
        {
            Assert.Contains("PRODUCTION", action.Confirm, StringComparison.Ordinal);
        }
        Assert.Contains(ConsoleActions.All, a => a.Id == "prod-deploy" && a.Confirm!.Contains("PRODUCTION", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePageCanOnlyAskForAnActionThatIsOnTheMenu()
    {
        Assert.Null(ConsoleActions.Find("rm -rf"));
        Assert.Null(ConsoleActions.Find(null));
        Assert.NotNull(ConsoleActions.Find("prod-deploy"));
    }

    [Fact]
    public void ThePageCarriesTheTokenAndEncodesTheLabels()
    {
        var actions = new[] { new ConsoleAction("x", "G<b>", "Label <script>alert(1)</script>", new[] { "status", "prod" }) };

        var page = ConsolePage.Render(actions, "TOKEN123");

        Assert.Contains("TOKEN123", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert(1)", page, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Access__CountryGateMode=Enforce Sensitive")]
    [InlineData("Access__CountryGateMode=a&calc")]
    [InlineData("Access__CountryGateMode=\"x\"")]
    [InlineData("Access__CountryGateMode=a;b")]
    public async Task ASettingValueWithShellCharactersIsRefusedBeforeAnythingRuns(string word)
    {
        var code = await SettingCommand.RunAsync(new Options(new[] { "prod", word }));

        Assert.Equal(1, code);
    }

    [Fact]
    public async Task ASettingNameWithShellCharactersIsRefused()
    {
        var code = await SettingCommand.RunAsync(new Options(new[] { "prod", "A&calc=1" }));

        Assert.Equal(1, code);
    }

    [Fact]
    public async Task ASecretLookingSettingIsNeverShown()
    {
        var code = await SettingCommand.RunAsync(new Options(new[] { "prod", "Api__ImportKey" }));

        Assert.Equal(1, code);
    }

    private static bool IsSettingRead(ConsoleAction action) => action.Args[0] == "setting" && !action.Args[^1].Contains('=');
}
