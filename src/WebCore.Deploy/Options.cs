/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Deploy;

/// <summary>Tiny argument reader: positional words, --flag, and --name value.</summary>
internal sealed class Options
{
    private readonly string[] _args;

    /// <summary>What the app adds to the standard tool (its own verify checks), carried to every command.</summary>
    public DeployExtensions Extensions { get; }

    public Options(string[] args, DeployExtensions? extensions = null)
    {
        _args = args;
        Extensions = extensions ?? new DeployExtensions();
    }

    /// <summary>Every argument that is not a --flag (positional words, and a flag's value when it has one), in order.</summary>
    public IEnumerable<string> Words => _args.Where(a => !a.StartsWith("--", StringComparison.Ordinal));

    public bool Flag(string name) => _args.Contains(name, StringComparer.OrdinalIgnoreCase);

    public string? Value(string name)
    {
        var index = Array.FindIndex(_args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < _args.Length && !_args[index + 1].StartsWith("--", StringComparison.Ordinal) ? _args[index + 1] : null;
    }

    public int? IntValue(string name) => int.TryParse(Value(name), out var n) ? n : null;

    /// <summary>Minutes to keep waiting for games to finish: --wait 45, or 120 when --wait has no number.</summary>
    public int WaitMinutes() => Flag("--wait") ? IntValue("--wait") ?? 120 : 0;

    /// <summary>The first positional word (stage or prod), resolved to its target.</summary>
    public (string Name, Target Target, Options Options) Target(string command)
    {
        var name = _args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && (a is "stage" or "prod"))
            ?? throw new InvalidOperationException($"{command} needs a target: stage or prod.");
        return DeployTool.Resolve(this, name);
    }
}
