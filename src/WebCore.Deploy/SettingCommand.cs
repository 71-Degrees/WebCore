/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Text.RegularExpressions;

namespace WebCore.Deploy;

/// <summary>
/// <c>setting &lt;stage|prod&gt; Name[=Value]</c>: reads or changes one Azure App Setting. Changing it restarts the site, so it waits for any game in progress exactly as a deploy does, then
/// waits for the site to answer again and runs <c>verify</c>. Names and values are held to a plain character set because the Azure CLI is reached through a shell; anything fancier belongs in the portal.
/// </summary>
internal static partial class SettingCommand
{
    private static readonly string[] SecretWords = { "key", "secret", "password", "token", "connection", "credential" };

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_:.-]{0,127}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex ValuePattern();

    public static async Task<int> RunAsync(Options options)
    {
        var t = options.Target("setting");
        var word = options.Words.FirstOrDefault(w => w is not ("stage" or "prod"))
            ?? throw new InvalidOperationException("setting needs a Name or Name=Value.");
        var (name, value) = Split(word);
        if (!NamePattern().IsMatch(name))
        {
            return DeployTool.Fail($"'{name}' is not a plain setting name (letters, digits, _ : . -).");
        }

        if (string.IsNullOrWhiteSpace(t.Target.App))
        {
            return DeployTool.Fail($"The '{t.Name}' target has no Azure app name in targets.json.");
        }

        return value is null ? Read(t, name) : await ChangeAsync(t, name, value, options);
    }

    private static (string Name, string? Value) Split(string word)
    {
        var at = word.IndexOf('=');
        return at < 0 ? (word, null) : (word[..at], word[(at + 1)..]);
    }

    private static int Read((string Name, Target Target, Options Options) t, string name)
    {
        // Reading prints the value, so refuse the names that hold secrets rather than echo one into a page or a log.
        if (SecretWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            return DeployTool.Fail($"'{name}' looks like a secret, so its value is not shown.");
        }

        var current = ReadValue(t.Target, name);
        Console.WriteLine($"{t.Name}: {name} = {(current is null ? "(not set)" : current)}");
        return 0;
    }

    private static string? ReadValue(Target target, string name)
    {
        var (code, output) = DeployTool.Run("cmd", $"/c az webapp config appsettings list --name {target.App} --resource-group {target.Group} --query \"[?name=='{name}'].value | [0]\" -o tsv");
        return code == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
    }

    private static async Task<int> ChangeAsync((string Name, Target Target, Options Options) t, string name, string value, Options options)
    {
        if (!ValuePattern().IsMatch(value))
        {
            return DeployTool.Fail("The value may only use letters, digits and . _ - (anything else: use the Azure portal).");
        }

        var before = SecretWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)) ? "(hidden)" : ReadValue(t.Target, name) ?? "(not set)";
        Console.WriteLine($"{t.Name}: {name} is {before}; changing it to {value}. The site restarts.");

        // A setting change restarts the app just like a deploy, so it never lands on a game in progress.
        if (!await DeployTool.WaitUntilSafeAsync(t, options))
        {
            return 3;
        }

        if (options.Flag("--whatif"))
        {
            Console.WriteLine("--whatif: nothing changed.");
            return 0;
        }

        // The restart is on purpose, so the "site is down" alert is quieted around it.
        return await DeployTool.QuietAroundAsync(t.Name, t.Target, options, () => ApplyAsync(t, name, value, options));
    }

    private static async Task<int> ApplyAsync((string Name, Target Target, Options Options) t, string name, string value, Options options)
    {
        // -o none: the CLI's default output lists every setting value, secrets included.
        var (code, output) = DeployTool.Run("cmd", $"/c az webapp config appsettings set --name {t.Target.App} --resource-group {t.Target.Group} --settings {name}={value} -o none");
        if (code != 0)
        {
            return DeployTool.Fail("az could not change the setting: " + output.Trim());
        }

        var after = ReadValue(t.Target, name);
        Console.WriteLine($"Set. Azure now has {name} = {after ?? "(not set)"}.");
        if (!string.Equals(after, value, StringComparison.Ordinal))
        {
            return DeployTool.Fail("Azure does not report the value that was just written - check the portal.");
        }

        await WaitForSiteAsync(t.Target);
        return await VerifyCommands.VerifyAsync(t.Name, t.Target, options);
    }

    private static async Task WaitForSiteAsync(Target target)
    {
        Console.WriteLine("Waiting for the site to restart and answer...");
        await Task.Delay(TimeSpan.FromSeconds(15)); // the old instance can still answer for a moment after the change
        var giveUp = DateTimeOffset.UtcNow.AddMinutes(5);
        while (DateTimeOffset.UtcNow < giveUp)
        {
            if (await DeployTool.GetVersionAsync(target.Url) is not null)
            {
                Console.WriteLine("The site answers.");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        Console.Error.WriteLine($"The site did not answer within 5 minutes - check {target.Url}/version.");
    }
}
