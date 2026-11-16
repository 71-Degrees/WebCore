/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Globalization;

namespace WebCore.LiveSettings;

/// <summary>What kind of value a tunable holds; the editor page picks its input from it.</summary>
public enum LiveSettingKind
{
    Text,
    WholeNumber,
    Amount,
    Toggle,
}

/// <summary>
/// One tunable an app lists on purpose: its key, kind, default (today's behaviour), valid range and a sentence for the editor. Values travel as invariant text in the settings document, and
/// <see cref="Validate"/> is the one rule for whether a piece of text is acceptable, used by the page before saving and by the reader before trusting what is stored.
/// </summary>
public abstract class LiveSettingDefinition
{
    private protected LiveSettingDefinition(string key, LiveSettingKind kind, string description, string group)
    {
        Key = key;
        Kind = kind;
        Description = description;
        Group = group;
    }

    public string Key { get; }

    public LiveSettingKind Kind { get; }

    public string Description { get; }

    public string Group { get; }

    /// <summary>The default as the text the document would hold.</summary>
    public abstract string DefaultText { get; }

    /// <summary>The valid range or choices in words, for the editor ("30 to 600"); empty when anything goes.</summary>
    public abstract string RangeText { get; }

    /// <summary>Null when <paramref name="raw"/> is an acceptable value, otherwise the reason it is not.</summary>
    public abstract string? Validate(string raw);
}

/// <summary>A tunable of one type. Build one with the methods of <see cref="LiveSettingFactory"/>.</summary>
public sealed class LiveSettingDefinition<T> : LiveSettingDefinition
{
    private readonly Func<string, (bool Ok, T Value, string? Reason)> _parse;
    private readonly Func<T, string> _format;

    internal LiveSettingDefinition(LiveSettingKind kind, string key, T defaultValue, (string Description, string Group, string Range) text, (Func<string, (bool Ok, T Value, string? Reason)> Parse, Func<T, string> Format) codec)
        : base(key, kind, text.Description, text.Group)
    {
        Default = defaultValue;
        RangeText = text.Range;
        _parse = codec.Parse;
        _format = codec.Format;
    }

    public T Default { get; }

    public override string DefaultText => _format(Default);

    public override string RangeText { get; }

    public override string? Validate(string raw) => _parse(raw).Reason;

    /// <summary>The value in <paramref name="raw"/>, or false (and the default) when it is not valid.</summary>
    public bool TryRead(string raw, out T value)
    {
        var (ok, parsed, _) = _parse(raw);
        value = ok ? parsed : Default;
        return ok;
    }
}

/// <summary>Builds the definitions an app registers. Every default is checked against its own rule when built, so a typo in a default fails at start, not at the first save.</summary>
public static class LiveSettingFactory
{
    public static LiveSettingDefinition<string> Text(string key, string defaultValue, string description, string group, int maxLength = 200, IReadOnlyList<string>? allowed = null)
    {
        var range = allowed is { Count: > 0 } ? "one of: " + string.Join(", ", allowed) : "up to " + maxLength.ToString(CultureInfo.InvariantCulture) + " characters";
        (bool, string, string?) Parse(string raw) => ParseText(raw.Trim(), maxLength, allowed);
        return Checked(new LiveSettingDefinition<string>(LiveSettingKind.Text, key, defaultValue, (description, group, range), (Parse, v => v)));
    }

    public static LiveSettingDefinition<int> WholeNumber(string key, int defaultValue, int minimum, int maximum, string description, string group)
    {
        (bool, int, string?) Parse(string raw) =>
            !int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? (false, 0, "Not a whole number.")
            : n < minimum || n > maximum ? (false, 0, $"Must be between {minimum} and {maximum}.")
            : (true, n, null);
        return Checked(new LiveSettingDefinition<int>(LiveSettingKind.WholeNumber, key, defaultValue, (description, group, $"{minimum} to {maximum}"), (Parse, v => v.ToString(CultureInfo.InvariantCulture))));
    }

    public static LiveSettingDefinition<decimal> Amount(string key, decimal defaultValue, decimal minimum, decimal maximum, string description, string group)
    {
        var low = minimum.ToString(CultureInfo.InvariantCulture);
        var high = maximum.ToString(CultureInfo.InvariantCulture);
        (bool, decimal, string?) Parse(string raw) =>
            !decimal.TryParse(raw.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? (false, 0m, "Not a number.")
            : n < minimum || n > maximum ? (false, 0m, $"Must be between {low} and {high}.")
            : (true, n, null);
        return Checked(new LiveSettingDefinition<decimal>(LiveSettingKind.Amount, key, defaultValue, (description, group, low + " to " + high), (Parse, v => v.ToString(CultureInfo.InvariantCulture))));
    }

    public static LiveSettingDefinition<bool> Toggle(string key, bool defaultValue, string description, string group)
    {
        (bool, bool, string?) Parse(string raw) =>
            bool.TryParse(raw.Trim(), out var b) ? (true, b, null) : (false, false, "Must be true or false.");
        return Checked(new LiveSettingDefinition<bool>(LiveSettingKind.Toggle, key, defaultValue, (description, group, "on or off"), (Parse, v => v ? "true" : "false")));
    }

    private static (bool, string, string?) ParseText(string value, int maxLength, IReadOnlyList<string>? allowed)
    {
        if (value.Length > maxLength)
        {
            return (false, string.Empty, $"At most {maxLength} characters.");
        }

        if (allowed is { Count: > 0 } && !allowed.Contains(value, StringComparer.Ordinal))
        {
            return (false, string.Empty, "Must be one of: " + string.Join(", ", allowed) + ".");
        }

        return (true, value, null);
    }

    private static LiveSettingDefinition<T> Checked<T>(LiveSettingDefinition<T> definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Key) || definition.Key.Length > 100)
        {
            throw new ArgumentException("A live setting needs a key of 1 to 100 characters.", nameof(definition));
        }

        // An empty text default is legitimate (it means "use the shared one"), so only a default the rule itself refuses is a mistake.
        var emptyTextDefault = definition.Kind == LiveSettingKind.Text && definition.DefaultText.Length == 0;
        if (!emptyTextDefault && definition.Validate(definition.DefaultText) is { } reason)
        {
            throw new ArgumentException($"The default of live setting '{definition.Key}' breaks its own rule: {reason}", nameof(definition));
        }

        return definition;
    }
}
