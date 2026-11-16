/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

namespace WebCore.LiveSettings;

/// <summary>
/// The tunables an app keeps in a document it can edit while running. Readers call <see cref="GetAsync{T}"/> every time they need the value (never once at startup), so a change is seen within the
/// cache time on every server and at once on the server that saved it. A read never throws: a missing, unreadable or invalid document or value gives the default.
/// </summary>
public interface ILiveSettings
{
    Task<T> GetAsync<T>(LiveSettingDefinition<T> definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// The value now, without waiting, for code that cannot await (a property read on every request). It serves the last document this server read or saved (the default until the first read finishes,
    /// so warm it once at startup with <see cref="ListAsync"/>), and refreshes it in the background once the cache time is up. A change elsewhere is seen within about two cache times.
    /// </summary>
    T Peek<T>(LiveSettingDefinition<T> definition);

    /// <summary>Every registered tunable with the value in force, for the editor page.</summary>
    Task<IReadOnlyList<LiveSettingView>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates and stores an override. A refused value changes nothing and says why.</summary>
    Task<LiveSettingResult> SetAsync(string key, string raw, string changedBy, CancellationToken cancellationToken = default);

    /// <summary>Removes the override so the default applies again.</summary>
    Task<LiveSettingResult> ResetAsync(string key, string changedBy, CancellationToken cancellationToken = default);
}
