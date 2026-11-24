/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Deploy;

/// <summary>The front-end and code-quality steps of <c>check</c> (what the pre-push hook runs), kept out of DeployTool.cs so that file stops growing.</summary>
internal static class QualityChecks
{
    /// <summary>Set to 1 to run the repo's own scriptTypeCheck/scriptLint again (see <see cref="RepoScripts"/>).</summary>
    private const string RepoScriptsVariable = "WEBCORE_REPO_SCRIPTS";

    /// <summary>The browser scripts' type check and lint, run by the machine-wide CodeQuality (<see cref="QualityTool"/>) over every repo's src/*/wwwroot/js with one shared
    /// rule set (2026-11-24). The repo's own scriptTypeCheck/scriptLint settings are switched off. A missing copy fails the push (decision D3): a machine without it would
    /// push unchecked scripts.</summary>
    public static bool Scripts(string root, StageLog log)
    {
        if (Environment.GetEnvironmentVariable(RepoScriptsVariable) == "1")
        {
            return RepoScripts(root, log);
        }

        if (!QualityTool.Installed)
        {
            log.Error(QualityTool.MissingMessage);
            return false;
        }

        log.WriteLine("Checking the browser scripts (type check and lint, CodeQuality's machine-wide tools)...");
        var (code, output) = DeployTool.Run("dotnet", QualityTool.Arguments("scripts --no-open"), root);
        if (code == 0)
        {
            return true;   // CodeQuality itself reports "no browser scripts" for a repo without any, which passes
        }

        var lines = output.Split('\n').Where(l => l.StartsWith("typecheck") || l.StartsWith("lint") || l.StartsWith("    "));
        log.Error(string.Join(Environment.NewLine, lines));
        log.Error($"The browser scripts do not pass the type check or lint (run: dotnet {QualityTool.Arguments("scripts")}).");
        return false;
    }

    /// <summary>Code smells (the machine-wide CodeQuality, see <see cref="QualityTool"/>: Roslyn over the C# and Razor). Fails only on what is NEW or WORSE than
    /// quality/baseline.json, so old code never blocks a push but new code cannot add to the pile. A missing copy fails the push (decision D3): a machine without it
    /// would push unchecked code. If the check itself cannot run it does not block.</summary>
    public static bool Smells(string root, StageLog log)
    {
        log.WriteLine("Checking code smells against the baseline...");
        if (!QualityTool.Installed)
        {
            log.Error(QualityTool.MissingMessage);
            return false;
        }

        var (code, output) = DeployTool.Run("dotnet", QualityTool.Arguments("smells --top 15 --no-open"), root);
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
        log.Error($"New or worse code smells (see docs/quality.md). Fix them; if one is deliberate, run `dotnet {QualityTool.Arguments("smells --update-baseline")}`, commit quality/baseline.json and say why.");
        return false;
    }

    /// <summary>The browser scripts' type check and eslint. Skipped, not failed, when Node or TypeScript is not installed: the editor still checks them as you type, and the interop contract
    /// test in the unit tests still runs. Switched off 2026-11-24 (CodeQuality runs the scripts machine-wide); runs again when WEBCORE_REPO_SCRIPTS=1.</summary>
    private static bool RepoScripts(string root, StageLog log)
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
