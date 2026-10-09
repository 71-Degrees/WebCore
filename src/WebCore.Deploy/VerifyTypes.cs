/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.Deploy;

/// <summary>How a verify check came out: it passed, it needs a look, or something is broken.</summary>
public enum VerifyResult
{
    Pass,
    Warn,
    Fail,
}

/// <summary>One line of the verify report.</summary>
public sealed record VerifyOutcome(VerifyResult Result, string Name, string Detail);

/// <summary>What a page answered: its status, body, the names of its response headers (lower case) and any redirect address.</summary>
public sealed record Reply(int Status, string Body, IReadOnlySet<string> Headers, string? Location);

/// <summary>What a check is given: a way to load pages of the site being verified (without following redirects), its address, and which target it is.</summary>
public sealed class VerifyContext(HttpClient http, string baseUrl, string targetName)
{
    public string BaseUrl { get; } = baseUrl;

    /// <summary>"stage" or "prod": production is open to search engines and staging is not, among other differences.</summary>
    public string TargetName { get; } = targetName;

    /// <summary>Loads a page of the site, never throwing: a failure to connect is a reply with status 0.</summary>
    public async Task<Reply> GetAsync(string path)
    {
        try
        {
            using var response = await http.GetAsync(BaseUrl + path).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var headers = response.Headers.Select(h => h.Key.ToLowerInvariant()).Concat(response.Content.Headers.Select(h => h.Key.ToLowerInvariant())).ToHashSet();
            return new Reply((int)response.StatusCode, body, headers, response.Headers.Location?.ToString());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new Reply(0, string.Empty, new HashSet<string>(), null);
        }
    }
}

/// <summary>An app's own verify check (its sign-up page shows prices, its catalog has enough items...). Read-only: it loads pages and judges them.</summary>
public interface IVerifyCheck
{
    Task<IReadOnlyList<VerifyOutcome>> RunAsync(VerifyContext context);
}

/// <summary>What an app adds to the standard deploy tool from its own launcher project.</summary>
public sealed class DeployExtensions
{
    /// <summary>Checks run after the configured page checks, as part of every verify.</summary>
    public IReadOnlyList<IVerifyCheck> VerifyChecks { get; init; } = Array.Empty<IVerifyCheck>();
}
