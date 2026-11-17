/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Collections.Concurrent;
using WebCore.Caching;
using WebCore.Storage;

namespace WebCore.LiveSettings;

/// <summary>
/// <see cref="ILiveSettings"/> over one document per environment. Reads come from a short cache of the whole document (at most one store read per time-to-live per server, cleared at once by a save made
/// here, a copy to every caller). Saves are serialized and re-read the stored document first, so two overlapping saves on this server both land; across servers the last writer of a document wins, which
/// for a handful of admin-edited values is acceptable. A failed read is never cached and never thrown from a read.
/// </summary>
public sealed class LiveSettingsService : ILiveSettings, IDisposable
{
    private static readonly TimeSpan WarnEvery = TimeSpan.FromMinutes(10);

    private readonly IKeyedDocumentStore _store;
    private readonly LiveSettingsOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, LiveSettingDefinition> _definitions;
    private readonly string _documentName;
    private readonly SnapshotCache<LiveSettingsDocument> _cache;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _warned = new(StringComparer.Ordinal);

    // What Peek serves without waiting: the last document read or saved here (defaults until the first read completes), refreshed in the background once the time-to-live is up.
    private LiveSettingsDocument _lastKnown = new();
    private long _nextRefreshTicks;
    private int _refreshing;

    /// <summary>The background refresh <see cref="Peek{T}"/> last started, held rather than discarded so it is observed (it never faults: the read inside
    /// swallows store failures), and a test can await it.</summary>
    internal Task BackgroundRefresh { get; private set; } = Task.CompletedTask;

    public LiveSettingsService(IKeyedDocumentStore store, IEnumerable<LiveSettingDefinition> definitions, LiveSettingsOptions options, TimeProvider? timeProvider = null)
    {
        _store = store;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _definitions = definitions.ToDictionary(d => d.Key, StringComparer.Ordinal);
        var environment = options.Environment.Trim().ToLowerInvariant();
        _documentName = DocumentNames.For(environment, "environment");
        _cache = new SnapshotCache<LiveSettingsDocument>("live-settings-" + environment, options.CacheTimeToLive, copy: d => d.Copy(), timeProvider: _time);
    }

    public async Task<T> GetAsync<T>(LiveSettingDefinition<T> definition, CancellationToken cancellationToken = default)
    {
        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
        return ValueFrom(definition, document);
    }

    public T Peek<T>(LiveSettingDefinition<T> definition)
    {
        var now = _time.GetUtcNow();
        if (now.UtcTicks >= Volatile.Read(ref _nextRefreshTicks) && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
        {
            Volatile.Write(ref _nextRefreshTicks, (now + _options.CacheTimeToLive).UtcTicks);
            BackgroundRefresh = RefreshInBackgroundAsync();
        }

        return ValueFrom(definition, Volatile.Read(ref _lastKnown));
    }

    // Never throws (the read it runs swallows store failures), so a forgotten task cannot fault unobserved. The next attempt waits a full time-to-live even after a failure.
    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            await ReadDocumentAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private T ValueFrom<T>(LiveSettingDefinition<T> definition, LiveSettingsDocument document)
    {
        if (!document.Values.TryGetValue(definition.Key, out var entry))
        {
            return definition.Default;
        }

        if (definition.TryRead(entry.Value, out var value))
        {
            return value;
        }

        Warn("value:" + definition.Key, $"Live setting '{definition.Key}' holds a value that is not valid ({definition.Validate(entry.Value)}); using the default.");
        return definition.Default;
    }

    public async Task<IReadOnlyList<LiveSettingView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
        return _definitions.Values
            .OrderBy(d => d.Group, StringComparer.Ordinal)
            .ThenBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => ViewOf(d, document))
            .ToList();
    }

    public async Task<LiveSettingResult> SetAsync(string key, string raw, string changedBy, CancellationToken cancellationToken = default)
    {
        if (!_definitions.TryGetValue(key, out var definition))
        {
            return LiveSettingResult.Refused($"'{key}' is not a live setting.");
        }

        var reason = definition.Validate(raw);
        if (reason is not null)
        {
            return LiveSettingResult.Refused(reason);
        }

        var text = Normalized(definition, raw);
        var now = _time.GetUtcNow();
        return await ChangeAsync(definition, changedBy, document => document.Values[key] = new LiveSettingEntry(text, changedBy, now), text, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LiveSettingResult> ResetAsync(string key, string changedBy, CancellationToken cancellationToken = default)
    {
        if (!_definitions.TryGetValue(key, out var definition))
        {
            return LiveSettingResult.Refused($"'{key}' is not a live setting.");
        }

        return await ChangeAsync(definition, changedBy, document => document.Values.Remove(key), null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Releases the lock and cache this service owns.</summary>
    public void Dispose()
    {
        _writeLock.Dispose();
        _cache.Dispose();
    }

    private async Task<LiveSettingResult> ChangeAsync(LiveSettingDefinition definition, string changedBy, Action<LiveSettingsDocument> apply, string? newText, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Straight from the store, not the cache: a save must build on what is stored now, and a store that cannot be read must refuse rather than overwrite.
            var stored = await _store.GetAsync<LiveSettingsDocument>(_documentName, cancellationToken).ConfigureAwait(false);
            var document = stored?.Copy() ?? new LiveSettingsDocument();
            var oldText = ViewOf(definition, document).EffectiveText;
            apply(document);
            await _store.SetAsync(_documentName, document, cancellationToken).ConfigureAwait(false);
            _cache.Invalidate();
            Volatile.Write(ref _lastKnown, document);
            await RecordAsync(new LiveSettingChange(definition.Key, oldText, newText, changedBy, _time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            return LiveSettingResult.Ok;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // The save has already happened; a failing audit log is reported, not allowed to turn a stored change into an error the admin would retry.
    private async Task RecordAsync(LiveSettingChange change, CancellationToken cancellationToken)
    {
        if (_options.Audit is null)
        {
            return;
        }

        try
        {
            await _options.Audit.RecordAsync(change, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn("audit", $"Live setting '{change.Key}' was saved but its audit entry failed: {ex.Message}");
        }
    }

    // A read never throws: the app must not go down for a bad or unreachable settings document. A failed read is not cached (the load throws, so the cache keeps nothing) and warns once per interval.
    private async Task<LiveSettingsDocument> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        try
        {
            var document = await _cache.GetAsync(async ct => await _store.GetAsync<LiveSettingsDocument>(_documentName, ct).ConfigureAwait(false) ?? new LiveSettingsDocument(), cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _lastKnown, document.Copy());
            return document;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn("read", $"The live settings document could not be read, so defaults apply: {ex.Message}");
            return new LiveSettingsDocument();
        }
    }

    private static LiveSettingView ViewOf(LiveSettingDefinition definition, LiveSettingsDocument document)
    {
        if (document.Values.TryGetValue(definition.Key, out var entry) && definition.Validate(entry.Value) is null)
        {
            return new LiveSettingView(definition, entry.Value, entry);
        }

        return new LiveSettingView(definition, definition.DefaultText, null);
    }

    private static string Normalized(LiveSettingDefinition definition, string raw) => definition.Kind == LiveSettingKind.Text ? raw.Trim() : raw.Trim().ToLowerInvariant();

    private void Warn(string topic, string message)
    {
        var now = _time.GetUtcNow();
        if (_options.Warn is null || (_warned.TryGetValue(topic, out var last) && now - last < WarnEvery))
        {
            return;
        }

        _warned[topic] = now;
        _options.Warn(message);
    }
}
