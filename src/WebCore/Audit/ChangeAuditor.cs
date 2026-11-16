/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace WebCore.Audit;

/// <summary>What an app tells the audit trail: how much to keep, and which top-level fields of its items no person needs to see in a change (bulky or bookkeeping).</summary>
public sealed record AuditTrailOptions
{
    public AuditLogOptions Log { get; init; } = new();

    public IReadOnlySet<string> IgnoredFields { get; init; } = AuditDiff.DefaultIgnored;
}

/// <summary>
/// Turns a save, a delete or a switched flag into a trail entry (the one place that knows how): who (<see cref="IAuditActorContext"/>), what changed
/// (<see cref="AuditDiff"/>, leaving out the fields the app named). A save that changed nothing writes nothing. Writing the trail never fails the change it describes:
/// the change has already happened, so a trail fault is logged as an error with the whole entry in the log line, and the caller carries on.
/// </summary>
public sealed class ChangeAuditor(IAuditLog log, IAuditActorContext actors, TimeProvider time, ILogger<ChangeAuditor> logger, AuditTrailOptions? options = null)
{
    private readonly IReadOnlySet<string> _ignored = options?.IgnoredFields ?? AuditDiff.DefaultIgnored;

    /// <summary>Records a save: <paramref name="before"/> is null for a new item.</summary>
    public Task SavedAsync<T>(AuditSubject subject, T? before, T after, CancellationToken cancellationToken = default)
        where T : class
    {
        var changes = AuditDiff.Compare(before, after, _ignored);
        if (before is not null && changes.Count == 0)
        {
            return Task.CompletedTask;
        }

        return AppendAsync(subject, before is null ? AuditEntry.Created : AuditEntry.Changed, before is null ? [] : changes, cancellationToken);
    }

    public Task DeletedAsync(AuditSubject subject, CancellationToken cancellationToken = default) =>
        AppendAsync(subject, AuditEntry.Deleted, [], cancellationToken);

    /// <summary>A flag or setting switched by its own call (a record put in review, a participation switch), as the one field it changed.</summary>
    public Task SwitchedAsync(AuditSubject subject, string field, bool now, CancellationToken cancellationToken = default) =>
        AppendAsync(subject, AuditEntry.Changed, [new AuditChange(field, Word(!now), Word(now))], cancellationToken);

    /// <summary>
    /// Runs a save and records it: the item as it was is read first (none for a new one), the save runs, and the two versions are compared. The shape a repository
    /// wrapper uses when its save returns the stored item.
    /// </summary>
    public async Task<T> SaveAsync<T>(Func<T, AuditSubject> subjectOf, T item, Func<Task<T?>> readBefore, Func<Task<T>> save, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(subjectOf);
        ArgumentNullException.ThrowIfNull(readBefore);
        ArgumentNullException.ThrowIfNull(save);
        var before = await readBefore().ConfigureAwait(false);
        var saved = await save().ConfigureAwait(false);
        await SavedAsync(subjectOf(saved), before, saved, cancellationToken).ConfigureAwait(false);
        return saved;
    }

    private static string Word(bool value) => value.ToString(CultureInfo.InvariantCulture).ToLowerInvariant();

    private async Task AppendAsync(AuditSubject subject, string action, IReadOnlyList<AuditChange> changes, CancellationToken cancellationToken)
    {
        var entry = new AuditEntry
        {
            At = time.GetUtcNow(),
            Actor = actors.Current,
            Kind = subject.Kind,
            EntityId = subject.EntityId,
            EntityName = subject.EntityName,
            OwnerId = subject.OwnerId,
            Action = action,
            Changes = changes,
        };

        try
        {
            await log.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The audit entry could not be written; the change itself stands. {Kind} {EntityId} \"{Name}\" of {OwnerId} {Action} by {ActorId} (trace {TraceId})",
                entry.Kind, entry.EntityId, entry.EntityName, entry.OwnerId, entry.Action, entry.Actor.Id, entry.Actor.TraceId);
        }
    }
}
