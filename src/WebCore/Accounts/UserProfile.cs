/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebCore.Accounts;

/// <summary>
/// The role and standing of an account, separate from its credentials (<see cref="User"/>). One is created or refreshed on every successful sign-in, so an
/// admin can see and grant roles to anyone who has ever signed in. The role is a name from the app's <see cref="AccountRoles"/>; profiles saved before
/// roles were strings hold a number, which <see cref="RoleNameJsonConverter"/> reads and the repository turns into the name.
/// </summary>
public class UserProfile
{
    public string UserId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    [JsonConverter(typeof(RoleNameJsonConverter))]
    public string Role { get; set; } = string.Empty;

    /// <summary>Admin action: blocks sign-in and signs out any existing session once set. Null <see cref="DisabledAt"/> means never disabled or
    /// currently re-enabled.</summary>
    public bool IsDisabled { get; set; }

    public DateTimeOffset? DisabledAt { get; set; }

    /// <summary>Fields this code does not know (an app's own profile fields, or a newer version's): read and written back unchanged.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }

    /// <summary>An independent copy, so a cached profile is never mutated through a caller's reference.</summary>
    public UserProfile Clone()
    {
        var copy = (UserProfile)MemberwiseClone();
        copy.UnknownFields = UnknownFields is null ? null : new Dictionary<string, JsonElement>(UnknownFields);
        return copy;
    }
}

/// <summary>Reads a role stored either as a name or, from before 2026-11-16, as the app enum's number (kept as its digits for
/// <see cref="AccountRoles.Normalize"/>); always writes the name.</summary>
public sealed class RoleNameJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number => reader.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonTokenType.String => reader.GetString() ?? string.Empty,
        JsonTokenType.Null => string.Empty,
        _ => throw new JsonException("A role must be a name or a number."),
    };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
