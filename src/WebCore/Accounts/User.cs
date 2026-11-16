/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebCore.Accounts;

/// <summary>
/// One person's account: the single identity behind signing in. It can sign in with a password, with a code emailed to <see cref="Email"/>, or both (an
/// account created by code alone has no password). Roles and the disabled flag live on <see cref="UserProfile"/>. Fields an app adds of its own are kept
/// in <see cref="UnknownFields"/>, so WebCore saving the account never erases them.
/// </summary>
public class User
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Empty for an account created by emailed code alone; such an account can never sign in by password.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Changes whenever the password is set, changed or reset. The sign-in cookie carries the value it was issued with, so a change ends every
    /// other session. Null until the first change (older cookies carry none and stay valid).</summary>
    public string? SecurityStamp { get; set; }

    /// <summary>When the email was proven by an emailed code; null until then.</summary>
    public DateTimeOffset? EmailVerifiedAt { get; set; }

    /// <summary>When the account was made; set when the object is first constructed (a stored account reads its own value back).</summary>
    public DateTimeOffset CreatedAt { get; set; } = TimeProvider.System.GetUtcNow();

    /// <summary>Fields this code does not know (an app's own, or a newer version's): read and written back unchanged.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }

    /// <summary>The one way a stamp is made, so every place that ends the existing sessions does it alike.</summary>
    public void RotateSecurityStamp() => SecurityStamp = Guid.NewGuid().ToString("N");

    /// <summary>An independent copy, so a cached account is never mutated through a caller's reference.</summary>
    public User Clone()
    {
        var copy = (User)MemberwiseClone();
        copy.UnknownFields = UnknownFields is null ? null : new Dictionary<string, JsonElement>(UnknownFields);
        return copy;
    }
}
