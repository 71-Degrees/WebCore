/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Deploy;

/// <summary>One button on the console page: the deploy command it runs, and the question to answer first when it changes something live.</summary>
/// <param name="Confirm">Null for a read-only action; otherwise the sentence the page asks before running it.</param>
internal sealed record ConsoleAction(string Id, string Group, string Label, string[] Args, string? Confirm = null);

/// <summary>
/// The fixed menu of the deploy console. The page can only ask for an action by its id, so what a browser tab can make the machine run is exactly this list and nothing it supplies itself.
/// </summary>
internal static class ConsoleActions
{
    public static readonly IReadOnlyList<ConsoleAction> All = Build();

    public static ConsoleAction? Find(string? id) => All.FirstOrDefault(a => a.Id == id);

    private static List<ConsoleAction> Build()
    {
        var actions = new List<ConsoleAction>
        {
            new("stage-deploy", "Deploy", "Build and deploy to staging", new[] { "stage", "--wait" }, "Build the latest commit and deploy it to STAGING?"),
            new("prod-deploy", "Deploy", "Promote the staging build to production", new[] { "prod", "--wait" }, "Deploy the build that is on staging to PRODUCTION?"),
            new("prod-rollback", "Deploy", "Roll production back one build", new[] { "rollback", "prod", "--wait" }, "Roll PRODUCTION back to the previous build?"),
            new("stage-rollback", "Deploy", "Roll staging back one build", new[] { "rollback", "stage", "--wait" }, "Roll staging back to the previous build?"),
            new("check", "Checks", "Run the tests and compile (what a push runs)", new[] { "check" }),
        };

        foreach (var target in DeployTool.Config.Targets.Keys)
        {
            actions.Add(new($"{target}-status", "Look", $"Status of {target} (build, sessions in progress)", new[] { "status", target }));
            actions.Add(new($"{target}-verify", "Look", $"Verify {target} (smoke checks and errors since deploy)", new[] { "verify", target }));
            actions.Add(new($"{target}-perf", "Look", $"Time the key pages on {target}", new[] { "perf", target }));
        }

        foreach (var custom in DeployTool.Config.ConsoleActions)
        {
            actions.AddRange(Expand(custom));
        }

        return actions;
    }

    /// <summary>An app's own button; one with <c>targets</c> becomes one button per target, with {target} and {live} (PRODUCTION or staging) filled in.</summary>
    private static IEnumerable<ConsoleAction> Expand(ConsoleActionSettings custom)
    {
        var targets = custom.Targets.Length > 0 ? custom.Targets : new[] { string.Empty };
        foreach (var target in targets)
        {
            string Fill(string text) => text.Replace("{target}", target, StringComparison.Ordinal).Replace("{live}", target == "prod" ? "PRODUCTION" : "staging", StringComparison.Ordinal);
            yield return new ConsoleAction(Fill(custom.Id), custom.Group, Fill(custom.Label), custom.Args.Select(Fill).ToArray(), custom.Confirm is null ? null : Fill(custom.Confirm));
        }
    }
}
