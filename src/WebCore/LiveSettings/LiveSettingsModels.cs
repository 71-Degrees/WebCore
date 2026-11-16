/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.LiveSettings;

/// <summary>One stored override: the value as invariant text, and who set it and when.</summary>
public sealed record LiveSettingEntry(string Value, string ChangedBy, DateTimeOffset ChangedAt);

/// <summary>The document kept per environment. It only overrides: a key that is absent means "the definition's default".</summary>
public sealed class LiveSettingsDocument
{
    public Dictionary<string, LiveSettingEntry> Values { get; set; } = new(StringComparer.Ordinal);

    internal LiveSettingsDocument Copy() => new() { Values = new Dictionary<string, LiveSettingEntry>(Values, StringComparer.Ordinal) };
}

/// <summary>What the editor shows for one tunable: the definition, the value in force, whether it is an override and who set it.</summary>
public sealed record LiveSettingView(LiveSettingDefinition Definition, string EffectiveText, LiveSettingEntry? Override);

/// <summary>The outcome of a save or reset; <see cref="Reason"/> says why a value was refused.</summary>
public sealed record LiveSettingResult(bool Saved, string? Reason)
{
    public static LiveSettingResult Ok { get; } = new(true, null);

    public static LiveSettingResult Refused(string reason) => new(false, reason);
}

/// <summary>One change, as the app's audit log wants it. A null <see cref="NewText"/> means the override was removed (back to the default).</summary>
public sealed record LiveSettingChange(string Key, string OldText, string? NewText, string ChangedBy, DateTimeOffset At);

/// <summary>Where the app records changes (its own audit log); the generic code never knows what that is.</summary>
public interface ILiveSettingsAudit
{
    Task RecordAsync(LiveSettingChange change, CancellationToken cancellationToken = default);
}

/// <summary>Which environment's document to use, how long a read is kept, and where warnings go. Warnings are one line each and never one per call.</summary>
public sealed record LiveSettingsOptions(string Environment, TimeSpan CacheTimeToLive, Action<string>? Warn = null, ILiveSettingsAudit? Audit = null);
