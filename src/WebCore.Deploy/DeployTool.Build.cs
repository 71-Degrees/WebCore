/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using WebCore.Operations;

namespace WebCore.Deploy;

internal static partial class DeployTool
{
    // ---- build -----------------------------------------------------------------------------------------------------------------------

    private static string StateDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Config.App + "Deploy");
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static DeployState LoadState()
    {
        var path = Path.Combine(StateDir(), "state.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<DeployState>(File.ReadAllText(path), Json) ?? new() : new();
    }

    private static void SaveState(DeployState state) => File.WriteAllText(Path.Combine(StateDir(), "state.json"), JsonSerializer.Serialize(state, Json));

    internal static string RepoRoot()
    {
        // Tests (and anyone running the tool from elsewhere) can point it at a repository directly.
        if (Environment.GetEnvironmentVariable("WEBCORE_DEPLOY_ROOT") is { Length: > 0 } fixedRoot && Directory.Exists(fixedRoot))
        {
            return fixedRoot;
        }

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                // A worktree has a .git file rather than a .git folder.
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                {
                    return dir.FullName;
                }
            }
        }

        throw new InvalidOperationException("Run this from inside the repository being deployed.");
    }

    internal static (int Code, string Output) Run(string file, string arguments, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start " + file);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.Result + error.Result);
    }

    /// <summary>Publishes the site as it is at HEAD and zips it. Returns the zip path and the build's version (the commit), or null on failure.</summary>
    private static (string Zip, string Version)? Build(Options options)
    {
        var root = RepoRoot();
        if (!MayBuild(root, options))
        {
            return null;
        }

        var sha = Run("git", "rev-parse --short=8 HEAD", root).Output.Trim();
        var version = "1.0.0+" + sha;
        var zip = Path.Combine(StateDir(), $"build-{sha}.zip");
        if (File.Exists(zip) && !options.Flag("--rebuild"))
        {
            Console.WriteLine($"Build {version} already exists: {zip}");
            return (zip, version);
        }

        return PublishAndZip(root, version, zip, options) ? (zip, version) : null;
    }

    /// <summary>A build must be of a commit whose tests pass, so the tests run first (--skip-tests overrides) and uncommitted changes under src/ stop it (--allow-dirty overrides).</summary>
    private static bool MayBuild(string root, Options options)
    {
        if (!options.Flag("--skip-tests") && !RunChecks(root, includeWebBuild: false, forBuild: true))
        {
            Console.Error.WriteLine("Not building: the tests must pass first (--skip-tests to override, though you shouldn't).");
            return false;
        }

        var (_, dirty) = Run("git", "status --porcelain -- src", root);
        if (!string.IsNullOrWhiteSpace(dirty) && !options.Flag("--allow-dirty"))
        {
            Console.Error.WriteLine("Uncommitted changes under src/ - commit them first so the build matches a commit (or pass --allow-dirty):\n" + dirty.TrimEnd());
            return false;
        }

        return true;
    }

    private static bool PublishAndZip(string root, string version, string zip, Options options)
    {
        var publishDir = Path.Combine(StateDir(), "publish-tmp");
        if (Directory.Exists(publishDir))
        {
            Directory.Delete(publishDir, recursive: true);
        }

        Console.WriteLine($"Publishing {version} ({Config.Runtime}, framework-dependent)...");
        var (code, output) = Run("dotnet", $"publish \"{Config.WebProject}\" -c Release -r {Config.Runtime} --self-contained false -o \"{publishDir}\"", root);
        if (code != 0)
        {
            Console.Error.WriteLine("dotnet publish failed:\n" + output);
            return false;
        }

        // Smaller css/js for the wire; the source files stay readable. --no-minify skips it.
        if (!options.Flag("--no-minify"))
        {
            var (minifiedFiles, beforeBytes, afterBytes, skippedFiles) = Minifier.MinifyWebRoot(Path.Combine(publishDir, "wwwroot"));
            Console.WriteLine($"Minified {minifiedFiles} files: {beforeBytes / 1024.0:N1} KB -> {afterBytes / 1024.0:N1} KB{(skippedFiles.Count > 0 ? "; left as is: " + string.Join(", ", skippedFiles) : string.Empty)}");
        }

        // The release notes ride along in the build so /admin/releases can show them.
        File.WriteAllText(Path.Combine(publishDir, "release-notes.json"), ReleaseNotesCommand.GenerateJson(root));

        // Native debug symbols (a vendor's .pdb can be ~100 MB) are useless in production and would blow the zip up; the app's own symbols are small and stay.
        foreach (var symbols in new DirectoryInfo(publishDir).GetFiles("*.pdb").Where(f => f.Length > 20L * 1024 * 1024))
        {
            symbols.Delete();
        }

        File.Delete(zip);
        ZipFile.CreateFromDirectory(publishDir, zip);
        Directory.Delete(publishDir, recursive: true);
        Console.WriteLine($"Built {zip} ({new FileInfo(zip).Length / 1024.0 / 1024.0:N1} MB)");

        foreach (var old in new DirectoryInfo(StateDir()).GetFiles("build-*.zip").OrderByDescending(f => f.LastWriteTimeUtc).Skip(8))
        {
            old.Delete();
        }

        return true;
    }
}
