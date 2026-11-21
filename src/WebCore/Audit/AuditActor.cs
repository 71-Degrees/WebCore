/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
namespace WebCore.Audit;

/// <summary>
/// Who is making the change being saved: the signed-in person, a stand-in for a non-person caller (<c>api</c> for a key-gated API), or <see cref="System"/> for work
/// nobody started (a hosted service, a rebuild, a tool). <see cref="TraceId"/> and <see cref="Path"/> tie a trail entry to the request's log lines.
/// </summary>
public sealed record AuditActor(string Id, string Name, string Source, string? TraceId = null, string? Path = null)
{
    public static readonly AuditActor System = new("system", "system", "system");

    /// <summary>
    /// The id an anonymised actor carries once that person has been forgotten. Their entries about other owners' items stay, so those histories keep their shape,
    /// but the id, trace and path go: the name as a string is enough for an audit.
    /// </summary>
    public const string ForgottenId = "forgotten";

    public static AuditActor Forgotten(AuditActor actor) => actor with { Id = ForgottenId, TraceId = null, Path = null };
}

/// <summary>The actor of the work in progress, carried with it (one value per request or circuit activity), so a repository that has no idea who is calling can still say who changed what.</summary>
public interface IAuditActorContext
{
    AuditActor Current { get; }

    /// <summary>Makes <paramref name="actor"/> the current actor until the returned scope is disposed.</summary>
    IDisposable Use(AuditActor actor);
}

public sealed class AuditActorContext : IAuditActorContext
{
    private readonly AsyncLocal<AuditActor?> _current = new();

    public AuditActor Current => _current.Value ?? AuditActor.System;

    public IDisposable Use(AuditActor actor)
    {
        var previous = _current.Value;
        _current.Value = actor;
        return new Scope(() => _current.Value = previous);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
