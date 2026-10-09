/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Deploy.Tests;

public class ConsoleActionsTests
{
    [Fact]
    public void AnAppOwnButtonWithTargetsBecomesOneButtonPerTarget()
    {
        var stage = ConsoleActions.Find("stage-flag-on");
        var prod = ConsoleActions.Find("prod-flag-on");

        Assert.NotNull(stage);
        Assert.NotNull(prod);
        Assert.Equal(new[] { "setting", "stage", "Feature__Flag=On" }, stage.Args);
        Assert.Equal("Turn the flag on for staging?", stage.Confirm);
        Assert.Equal("Turn the flag on for PRODUCTION?", prod.Confirm);
        Assert.Equal("Turn the flag on for prod", prod.Label);
    }

    [Fact]
    public void AButtonWithoutTargetsIsKeptAsWritten()
    {
        var read = ConsoleActions.Find("flag-read");

        Assert.NotNull(read);
        Assert.Null(read.Confirm);
        Assert.Equal(new[] { "setting", "prod", "Feature__Flag" }, read.Args);
    }

    [Fact]
    public void TheStandardLookingButtonsExistForEveryTarget()
    {
        foreach (var target in new[] { "stage", "prod" })
        {
            Assert.NotNull(ConsoleActions.Find($"{target}-status"));
            Assert.NotNull(ConsoleActions.Find($"{target}-verify"));
            Assert.NotNull(ConsoleActions.Find($"{target}-perf"));
        }
    }
}
