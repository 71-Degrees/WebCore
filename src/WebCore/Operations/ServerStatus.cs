/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Caching;

namespace WebCore.Operations;

/// <summary>One live session (a game, a call, a checkout in progress...) as the deploy tool sees it. An app with nothing live reports none.</summary>
public record ActiveGame(string SessionCode, string QuizTitle, string Phase, int Players, bool IsTest);

/// <summary>What is running on the server right now: which build, and whether anyone is mid-session. The shape is the wire format of the app's status endpoint, which the deploy tool reads.</summary>
public record ServerStatus(string Version, string? Environment, DateTimeOffset StartedAt, IReadOnlyList<ActiveGame> Games, IReadOnlyList<CacheSnapshot>? Caches = null, IReadOnlyList<StorageCallCount>? StorageCalls = null)
{
    /// <summary>Sessions where something is under way (or a break is running). Restarting the server ends these for everyone in them. Live tests (an admin alone with a test player) are not counted.</summary>
    public int InProgress => Games.Count(g => !g.IsTest && g.Phase != "Lobby");

    /// <summary>Lobbies with people waiting in them - a restart loses the join code they are all holding.</summary>
    public int WaitingLobbies => Games.Count(g => !g.IsTest && g.Phase == "Lobby" && g.Players > 0);
}

/// <summary>The rule the deploy tool applies before restarting a server that has people on it.</summary>
public static class DeployGuard
{
    /// <summary>Decides whether a deploy may go ahead. Sessions in progress block it unless the caller confirmed the exact number
    /// they are about to interrupt (<paramref name="confirmedInterrupt"/>); waiting lobbies only produce a warning.</summary>
    public static (bool Allowed, string Message) Evaluate(ServerStatus status, int? confirmedInterrupt)
    {
        if (status.InProgress > 0)
        {
            if (confirmedInterrupt == status.InProgress)
            {
                return (true, $"Proceeding on request: interrupting {status.InProgress} game(s) in progress.");
            }

            var detail = string.Join(", ", status.Games.Where(g => !g.IsTest && g.Phase != "Lobby").Select(g => $"{g.SessionCode} \"{g.QuizTitle}\" ({g.Players} player(s), {g.Phase})"));
            return (false, $"{status.InProgress} game(s) in progress: {detail}. Wait for them to finish (--wait), or pass --interrupt {status.InProgress} to deploy anyway.");
        }

        return (true, status.WaitingLobbies > 0
            ? $"No games in progress, but {status.WaitingLobbies} lobby(ies) have people waiting; they would lose their join code."
            : "No live games.");
    }
}
