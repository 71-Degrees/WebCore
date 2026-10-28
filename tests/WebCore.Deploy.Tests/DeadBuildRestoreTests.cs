/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Deploy.Tests;

public class DeadBuildRestoreTests
{
    [Theory]
    [InlineData("stage", false, true, true)]
    [InlineData("stage", false, false, false)]
    [InlineData("stage", true, true, false)]
    [InlineData("prod", false, true, false)]
    public void AStagingBuildThatNeverAnswers_GoesBackOnlyWhenThatIsSafe(string target, bool isRollback, bool onDisk, bool expected) =>
        Assert.Equal(expected, DeployTool.ShouldRestoreAfterDeadBuild(target, isRollback, onDisk));
}
