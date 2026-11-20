/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Deploy.Tests;

public class HostLoadCheckTests
{
    private const string Metrics = """
        {"value":[
          {"name":{"value":"CpuPercentage"},"timeseries":[{"data":[{"average":90.0},{"average":100.0},{"timeStamp":"x"}]}]},
          {"name":{"value":"MemoryPercentage"},"timeseries":[{"data":[{"average":80.0},{"average":84.0}]}]}
        ]}
        """;

    [Fact]
    public void TheAverageOfEachMetric_IsTakenOverTheMinutesThatHaveData()
    {
        var load = HostLoadCheck.Parse(Metrics);

        Assert.NotNull(load);
        Assert.Equal(95.0, load.CpuPercent);
        Assert.Equal(82.0, load.MemoryPercent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("please log in")]
    [InlineData("{\"value\":[]}")]
    [InlineData("{\"value\":[{\"name\":{\"value\":\"CpuPercentage\"},\"timeseries\":[]}]}")]
    public void Output_ThatIsNotUsableMetrics_IsNoReading(string output) =>
        Assert.Null(HostLoadCheck.Parse(output));

    [Theory]
    [InlineData(84, 89, false)]
    [InlineData(85, 10, true)]
    [InlineData(10, 90, true)]
    [InlineData(99, 99, true)]
    public void AHostIsBusy_AtTheCpuOrMemoryThreshold(double cpu, double memory, bool expected) =>
        Assert.Equal(expected, HostLoadCheck.IsBusy(new HostLoad(cpu, memory)));

    [Fact]
    public void TheLoadLine_NamesBothFigures() =>
        Assert.Equal("CPU 95%, memory 82%", new HostLoad(95.0, 82.0).ToString());
}
