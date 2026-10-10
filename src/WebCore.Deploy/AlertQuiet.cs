/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Text.Json;

namespace WebCore.Deploy;

/// <summary>
/// Keeps the "site is down" alert from emailing the owner every time a deploy or a setting change restarts the site on purpose. It uses an Azure Monitor <b>alert processing rule</b> with a schedule:
/// for a few minutes the named alert rules fire without notifying anyone. The window ends by itself, so a tool that dies halfway can never leave monitoring switched off; the rule is shortened to just
/// past the point where the failed availability samples have left the alert's window once the new build is steady, and removed at once if the deploy fails (a site that is really down should alert).
/// Off unless <c>alertQuiet</c> is in deploy/deploy.json.
/// </summary>
internal sealed class AlertQuiet(AlertQuietSettings? settings, string app, Func<string, (int Code, string Output)> az, TimeProvider time)
{
    private const string ApiVersion = "2021-08-08";

    /// <summary>True when this target's restarts are quieted: the setting exists, names rules, and lists this target.</summary>
    public bool AppliesTo(string targetName) => settings is { Rules.Length: > 0 } configured && configured.Targets.Contains(targetName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Starts the quiet window (the configured maximum, shortened later by <see cref="Settle"/>). False when it could not be set, which only means the owner may get the alert email.</summary>
    public bool Begin(Target target) => Put(target, settings!.QuietMinutes, "quiet for the deploy");

    /// <summary>The new build is steady: end the quiet window just after the failed availability samples will have left the alert's evaluation window.</summary>
    public bool Settle(Target target) => Put(target, settings!.SettleMinutes, "settling after the deploy");

    /// <summary>The deploy failed: remove the rule now so a site that is really down alerts normally.</summary>
    public bool Cancel(Target target)
    {
        var subscription = Subscription();
        return subscription is not null && az($"az rest --method delete --url \"{RuleUrl(subscription, target)}\"").Code == 0;
    }

    /// <summary>The request body: this resource group's alerts named in <paramref name="rules"/> are muted from <paramref name="from"/> until <paramref name="until"/> (UTC).</summary>
    public static string Body(string subscription, string group, IEnumerable<string> rules, DateTimeOffset from, DateTimeOffset until, string description) =>
        JsonSerializer.Serialize(new
        {
            location = "Global",
            properties = new
            {
                scopes = new[] { $"/subscriptions/{subscription}/resourceGroups/{group}" },
                conditions = new[] { new { field = "AlertRuleName", @operator = "Equals", values = rules.ToArray() } },
                schedule = new { effectiveFrom = Stamp(from), effectiveUntil = Stamp(until), timeZone = "UTC" },
                actions = new[] { new { actionType = "RemoveAllActionGroups" } },
                enabled = true,
                description,
            },
        });

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    private bool Put(Target target, int minutes, string why)
    {
        var subscription = Subscription();
        if (subscription is null)
        {
            return false;
        }

        var now = time.GetUtcNow();
        var body = Body(subscription, target.Group, settings!.Rules, now, now.AddMinutes(minutes), $"{app} deploy tool: {why}; ends by itself");
        var file = Path.Combine(Path.GetTempPath(), $"{app}-alert-quiet.json");
        File.WriteAllText(file, body);
        try
        {
            return az($"az rest --method put --url \"{RuleUrl(subscription, target)}\" --body @\"{file}\"").Code == 0;
        }
        finally
        {
            File.Delete(file);
        }
    }

    private string? Subscription()
    {
        var (code, output) = az("az account show --query id -o tsv");
        return code == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
    }

    private string RuleUrl(string subscription, Target target) =>
        $"https://management.azure.com/subscriptions/{subscription}/resourceGroups/{target.Group}/providers/Microsoft.AlertsManagement/actionRules/{settings!.RuleName ?? app + "-deploy-quiet"}?api-version={ApiVersion}";
}
