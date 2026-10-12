/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Deploy;

/// <summary>The front-end and code-quality steps of <c>check</c> (what the pre-push hook runs), kept out of DeployTool.cs so that file stops growing.</summary>
internal static class QualityChecks
{
    /// <summary>The browser scripts' type check and eslint. Skipped, not failed, when Node or TypeScript is not installed: the editor still checks them as you type, and the interop contract
    /// test in the unit tests still runs.</summary>
    public static bool Scripts(string root, StageLog log)
    {
        if (string.IsNullOrWhiteSpace(DeployTool.Config.ScriptTypeCheck) && string.IsNullOrWhiteSpace(DeployTool.Config.ScriptLint))
        {
            return true;   // this app has no browser scripts to check
        }

        var hasTsc = DeployTool.Run("cmd", "/c where node").Code == 0 && DeployTool.Run("cmd", "/c npx --no-install tsc --version", root).Code == 0;
        if (!hasTsc)
        {
            log.WriteLine("  (script type check skipped: Node/TypeScript not installed)");
            return true;
        }

        if (DeployTool.Config.ScriptTypeCheck is { Length: > 0 } typeCheck && !Step("Type-checking the scripts...", typeCheck, "The scripts do not type-check.", root, log))
        {
            return false;
        }

        // The same scripts, linted (eslint.config.mjs at the repo root): equality, unused or shadowed names, implicit globals. Needs `npm install` once at the repo root.
        var hasEslint = DeployTool.Run("cmd", "/c npx --no-install eslint --version", root).Code == 0;
        return !hasEslint || DeployTool.Config.ScriptLint is not { Length: > 0 } lint || Step("Linting the scripts...", lint, "The scripts do not lint clean.", root, log);
    }

    /// <summary>Code smells (tools/CodeQuality: Roslyn over the C# and Razor). Fails only on what is NEW or WORSE than quality/baseline.json, so old code never blocks a push but new code cannot
    /// add to the pile. If the check itself cannot run it does not block.</summary>
    public static bool Smells(string root, StageLog log)
    {
        log.WriteLine("Checking code smells against the baseline...");
        if (!File.Exists(Path.Combine(root, "tools", "CodeQuality", "src", "Quality", "Quality.csproj")))
        {
            log.WriteLine("  (the CodeQuality submodule is not checked out - run `git submodule update --init`; not blocking the push)");
            return true;
        }

        var (code, output) = DeployTool.Run("dotnet", "run --project tools/CodeQuality/src/Quality -- smells --top 15 --no-open", root);
        if (code == 0)
        {
            return true;
        }

        if (code != 1)
        {
            log.WriteLine("  (the smell check could not run; not blocking the push)");
            return true;
        }

        var lines = output.Split('\n').Where(l => l.TrimStart().StartsWith("NEW") || l.TrimStart().StartsWith("WORSE") || l.StartsWith("Against"));
        log.Error(string.Join(Environment.NewLine, lines));
        log.Error("New or worse code smells (see docs/quality.md). Fix them; if one is deliberate, run `dotnet run --project tools/CodeQuality/src/Quality -- smells --update-baseline`, commit quality/baseline.json and say why.");
        return false;
    }

    private static bool Step(string announce, string command, string failure, string root, StageLog log)
    {
        log.WriteLine(announce);
        var (code, output) = DeployTool.Run("cmd", "/c " + command, root);
        if (code == 0)
        {
            return true;
        }

        log.Error(output.Trim());
        log.Error(failure);
        return false;
    }
}
