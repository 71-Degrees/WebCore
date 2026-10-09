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
    private static string ApiKey((string Name, Target Target, Options Options) t)
    {
        var key = t.Options.Value("--api-key")
            ?? Environment.GetEnvironmentVariable(t.Target.KeyEnv, EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable(t.Target.KeyEnv);
        return string.IsNullOrWhiteSpace(key)
            ? throw new InvalidOperationException($"No API key: set the {t.Target.KeyEnv} environment variable (the Api__ImportKey value) or pass --api-key.")
            : key;
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string key, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Api-Key", key);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        return request;
    }

    /// <summary>The server's status, or null when it does not know the endpoint yet (a build older than this tool).</summary>
    private static async Task<ServerStatus?> GetStatusAsync((string Name, Target Target, Options Options) t)
    {
        using var response = await Http.SendAsync(Request(HttpMethod.Get, t.Target.Url + Config.Endpoints.Status, ApiKey(t)));
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound || (int)response.StatusCode >= 500)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ServerStatus>(Json);
    }

    internal static async Task<string?> GetVersionAsync(string url)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url + Config.Endpoints.Version));
            return doc.RootElement.GetProperty("version").GetString();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    // ---- status ----------------------------------------------------------------------------------------------------------------------

    private static async Task<int> StatusAsync((string Name, Target Target, Options Options) t)
    {
        var status = await GetStatusAsync(t);
        if (status is null)
        {
            Console.WriteLine($"{t.Name} ({t.Target.Url}): running a build that predates the status endpoint, so live games can't be checked.");
            Console.WriteLine("  version: " + (await GetVersionAsync(t.Target.Url) ?? "(unknown)"));
            return 0;
        }

        PrintStatus(t.Name, t.Target.Url, status);
        return 0;
    }

    private static void PrintStatus(string name, string url, ServerStatus status)
    {
        Console.WriteLine($"{name} ({url}): build {status.Version}{(status.Environment is null ? "" : ", " + status.Environment)}, up since {status.StartedAt.ToLocalTime():g}");
        Console.WriteLine($"  games in progress: {status.InProgress}, lobbies with people waiting: {status.WaitingLobbies}");
        foreach (var game in status.Games)
        {
            Console.WriteLine($"    {game.SessionCode}  {game.Phase,-8} {game.Players} player(s)  \"{game.QuizTitle}\"{(game.IsTest ? "  [live test]" : "")}");
        }
    }

}
