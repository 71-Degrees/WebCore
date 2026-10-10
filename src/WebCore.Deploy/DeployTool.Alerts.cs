/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Deploy;

internal static partial class DeployTool
{
    private static AlertQuiet NewAlertQuiet() => new(Config.AlertQuiet, Config.App, command => Run("cmd", "/c " + command), TimeProvider.System);

    /// <summary>
    /// Runs something that restarts the site on purpose with the "site is down" alert quieted. The quiet window is cut short once the action succeeded and the site is steady, and removed at once when
    /// it failed or threw. Does nothing extra for a target that has no <c>alertQuiet</c> setting.
    /// </summary>
    internal static async Task<int> QuietAroundAsync(string name, Target target, Options options, Func<Task<int>> action)
    {
        var quiet = NewAlertQuiet();
        if (options.Flag("--whatif") || !quiet.AppliesTo(name))
        {
            return await action();
        }

        Console.WriteLine(quiet.Begin(target)
            ? $"Alerts: the {name} down alert is quiet while the site restarts (it ends by itself)."
            : "Alerts: could not quiet the down alert; you may get an alert email for this restart.");
        var code = 1;
        try
        {
            code = await action();
            return code;
        }
        finally
        {
            Console.WriteLine((code == 0 ? quiet.Settle(target) : quiet.Cancel(target))
                ? (code == 0 ? "Alerts: quiet window set to end just after the restart's failed checks leave the alert window." : "Alerts: quiet window removed, so a real outage would alert.")
                : "Alerts: could not update the quiet window; it will end by itself.");
        }
    }

    /// <summary>alerts quiet|resume &lt;target&gt; [--minutes N]: starts or removes the quiet window by hand (for example before a long maintenance job).</summary>
    private static Task<int> AlertsAsync(Options options)
    {
        var (name, target, _) = options.Target("alerts");
        var quiet = NewAlertQuiet();
        if (!quiet.AppliesTo(name))
        {
            return Task.FromResult(Fail($"deploy.json has no alertQuiet setting for '{name}'."));
        }

        var verb = options.Words.FirstOrDefault(w => w is "quiet" or "resume") ?? throw new InvalidOperationException("alerts needs 'quiet' or 'resume' and a target.");
        var done = verb == "quiet" ? quiet.Begin(target) : quiet.Cancel(target);
        Console.WriteLine(done ? $"Alerts for {name}: {(verb == "quiet" ? "quiet (it ends by itself)" : "back to normal")}." : "Could not change the alert rule: is 'az login' current?");
        return Task.FromResult(done ? 0 : 1);
    }
}
