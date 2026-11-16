/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
namespace WebCore.Audit;

/// <summary>One field a save changed, as a person reads it: its path (<c>Title</c>, <c>Questions[2].Text</c>) and the value before and after (shortened; null when there was none).</summary>
public sealed record AuditChange(string Field, string? Before, string? After);

/// <summary>
/// One change to stored data: when, by whom (with the request's trace id), which item (its kind, id and the name a person knows it by), whose it is when the app keeps
/// data per owner, what was done and which fields changed. A deleted item keeps its name here, so the trail still reads after the item is gone.
/// </summary>
public sealed record AuditEntry
{
    public const string Created = "created";
    public const string Changed = "changed";
    public const string Deleted = "deleted";

    public required DateTimeOffset At { get; init; }

    public required AuditActor Actor { get; init; }

    /// <summary>The app's own word for the kind of item (<c>quiz</c>, <c>collection</c>): lower case letters, digits and dashes.</summary>
    public required string Kind { get; init; }

    public required string EntityId { get; init; }

    public required string EntityName { get; init; }

    /// <summary>Whose data it is (a collector, a shop, an organizer), when the app keeps data per owner; the trail is then also kept per owner and can be forgotten per owner.</summary>
    public string? OwnerId { get; init; }

    public required string Action { get; init; }

    public IReadOnlyList<AuditChange> Changes { get; init; } = [];
}

/// <summary>Which item an entry is about: its kind, its id, the name a person knows it by, and (optionally) whose it is.</summary>
public sealed record AuditSubject(string Kind, string EntityId, string EntityName, string? OwnerId = null);

/// <summary>
/// The trail: one capped list per item (its history card), one per owner (that person's changes, and what "delete my data" removes) and one across everything (the
/// administrator's recent changes). Stored newest last, read newest first.
/// </summary>
public interface IAuditLog
{
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEntry>> ForItemAsync(string kind, string entityId, int count, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEntry>> ForOwnerAsync(string ownerId, int count, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEntry>> RecentAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the owner's own list, the histories of the owner's items, and every entry about the owner or made by them from the recent list. Returns how many stored
    /// copies went (an entry is kept in up to three lists).
    /// </summary>
    Task<int> ForgetOwnerAsync(string ownerId, CancellationToken cancellationToken = default);
}
