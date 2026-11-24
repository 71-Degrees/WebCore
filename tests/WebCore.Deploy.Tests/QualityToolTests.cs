/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Deploy;

namespace WebCore.Deploy.Tests;

public class QualityToolTests
{
    // One method for both cases: the override is an environment variable, so the two must not run in parallel with each other.
    [Fact]
    public void Home_IsTheDefaultUnlessTheVariableOverridesIt()
    {
        var saved = Environment.GetEnvironmentVariable(QualityTool.HomeVariable);
        try
        {
            Environment.SetEnvironmentVariable(QualityTool.HomeVariable, null);
            Assert.Equal(QualityTool.DefaultHome, QualityTool.Home);

            Environment.SetEnvironmentVariable(QualityTool.HomeVariable, @"D:\elsewhere\quality");
            Assert.Equal(@"D:\elsewhere\quality", QualityTool.Home);
        }
        finally
        {
            Environment.SetEnvironmentVariable(QualityTool.HomeVariable, saved);
        }
    }

    [Fact]
    public void Dll_IsTheBinQualityDllUnderHome() =>
        Assert.Equal(Path.Combine(QualityTool.Home, "bin", "Quality.dll"), QualityTool.Dll);

    [Fact]
    public void Arguments_QuotesTheDllAndAppendsTheCommand() =>
        Assert.Equal($"\"{QualityTool.Dll}\" smells --top 15", QualityTool.Arguments("smells --top 15"));

    [Fact]
    public void MissingMessage_NamesTheDllAndTheInstallCommand()
    {
        Assert.Contains(QualityTool.Dll, QualityTool.MissingMessage);
        Assert.Contains(QualityTool.InstallCommand, QualityTool.MissingMessage);
    }
}
