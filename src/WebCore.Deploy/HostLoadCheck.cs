/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Globalization;
using System.Text.Json;

namespace WebCore.Deploy;

/// <summary>What the App Service plan's host was doing over the last few minutes: average CPU and memory, in percent.</summary>
internal sealed record HostLoad(double CpuPercent, double MemoryPercent)
{
    /// <summary>The one line the deploy prints before it uploads.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"CPU {CpuPercent:F0}%, memory {MemoryPercent:F0}%");
}

/// <summary>
/// Reads the plan's CPU and memory before an upload and, when the host is saturated, waits a little for it to calm down. A saturated one-core host starves the deploy
/// service (Kudu) that shares it, which is how a deploy can sit for many minutes or fail with a 503 while the site itself still answers (2026-11-20). The check only
/// informs and delays: it never fails a deploy, and a metrics call that fails is a one-line note, not an error.
/// </summary>
internal static class HostLoadCheck
{
    /// <summary>CPU at or above this percent, or memory at or above <see cref="BusyMemoryPercent"/>, counts as a host too busy to deploy to.</summary>
    internal const double BusyCpuPercent = 85;

    internal const double BusyMemoryPercent = 90;

    private const int MinutesRead = 5;
    private static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaitUpTo = TimeSpan.FromMinutes(3);

    internal static bool IsBusy(HostLoad load) => load.CpuPercent >= BusyCpuPercent || load.MemoryPercent >= BusyMemoryPercent;

    /// <summary>
    /// The average of each metric over the minutes in <c>az monitor metrics list ... --interval PT1M</c> output for CpuPercentage and MemoryPercentage. Null when the output
    /// is not that shape or either metric has no data (a brand-new plan, or a metrics outage).
    /// </summary>
    internal static HostLoad? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var cpu = AverageOf(document.RootElement, "CpuPercentage");
            var memory = AverageOf(document.RootElement, "MemoryPercentage");
            return cpu is { } c && memory is { } m ? new HostLoad(c, m) : null;
        }
        catch (JsonException)
        {
            // Not metrics JSON (a login prompt or an error text): the caller reports that the load could not be read.
            return null;
        }
    }

    /// <summary>Prints the host's load and, while it is busy, rechecks for up to three minutes. Never throws and never stops the deploy.</summary>
    internal static async Task ReportAndSettleAsync(Target target)
    {
        var plan = ReadPlanId(target);
        if (plan is null)
        {
            Console.WriteLine("Host load: could not find the App Service plan, so the load was not checked.");
            return;
        }

        var load = ReadLoad(plan);
        if (load is null)
        {
            Console.WriteLine("Host load: could not read the plan's CPU and memory, so the load was not checked.");
            return;
        }

        Console.WriteLine($"Host load before the upload: {load}.");
        var deadline = DateTimeOffset.UtcNow + WaitUpTo;
        while (IsBusy(load) && DateTimeOffset.UtcNow < deadline)
        {
            Console.WriteLine("The host is saturated, which can make the upload slow or fail (the deploy service shares the host). Waiting for it to calm down...");
            await Task.Delay(RecheckEvery).ConfigureAwait(false);
            load = ReadLoad(plan) ?? load;
            Console.WriteLine($"Host load: {load}.");
        }

        if (IsBusy(load))
        {
            Console.WriteLine("The host is still saturated; uploading anyway. If the upload hangs or answers 503, the load is the likely cause, not the tool.");
        }
    }

    private static string? ReadPlanId(Target target)
    {
        var (code, output) = DeployTool.Run("cmd", $"/c az webapp show --name {target.App} --resource-group {target.Group} --query serverFarmId -o tsv");
        var id = output.Trim();
        return code == 0 && id.StartsWith('/') ? id : null;
    }

    private static HostLoad? ReadLoad(string planId)
    {
        var (code, output) = DeployTool.Run("cmd", $"/c az monitor metrics list --resource {planId} --metric CpuPercentage MemoryPercentage --interval PT1M --offset {MinutesRead}m --aggregation Average -o json");
        return code == 0 ? Parse(output) : null;
    }

    private static double? AverageOf(JsonElement root, string metric)
    {
        if (!root.TryGetProperty("value", out var metrics))
        {
            return null;
        }

        foreach (var entry in metrics.EnumerateArray())
        {
            if (entry.GetProperty("name").GetProperty("value").GetString() != metric)
            {
                continue;
            }

            var averages = entry.GetProperty("timeseries").EnumerateArray()
                .SelectMany(series => series.GetProperty("data").EnumerateArray())
                .Where(point => point.TryGetProperty("average", out var value) && value.ValueKind == JsonValueKind.Number)
                .Select(point => point.GetProperty("average").GetDouble())
                .ToList();
            return averages.Count == 0 ? null : averages.Average();
        }

        return null;
    }
}
