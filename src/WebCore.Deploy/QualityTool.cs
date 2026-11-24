/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Deploy;

/// <summary>Where the machine-wide CodeQuality lives and how the pre-push check calls it. One place knows the path: the copy is installed once per machine by
/// `quality install` (CodeQuality's own command), so a repo no longer carries its own tools/CodeQuality submodule (plan standalone-quality, slice S3, decision D3).
/// <see cref="HomeVariable"/> overrides the folder for a machine that keeps it elsewhere.</summary>
internal static class QualityTool
{
    public const string DefaultHome = @"C:\71Degrees\published\quality";
    public const string HomeVariable = "CODEQUALITY_HOME";
    public const string InstallCommand = @"dotnet run --project C:\71Degrees\repos\CodeQuality\src\Quality -- install";

    public static string Home => Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home ? home : DefaultHome;

    public static string Dll => Path.Combine(Home, "bin", "Quality.dll");

    public static bool Installed => File.Exists(Dll);

    /// <summary>The arguments that follow `dotnet` to run CodeQuality with the given command.</summary>
    public static string Arguments(string command) => $"\"{Dll}\" {command}";

    public static string MissingMessage => $"CodeQuality is not installed on this machine ({Dll}): {InstallCommand}";
}
