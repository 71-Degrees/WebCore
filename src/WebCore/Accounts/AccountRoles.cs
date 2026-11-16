/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using System.Globalization;

namespace WebCore.Accounts;

/// <summary>
/// The roles an app knows, as the names it stores and issues as role claims. Roles are strings in WebCore (the owner's decision, 2026-11-15) so two apps
/// with different role lists can share one accounts module; each app maps them to its own enum at its edge.
/// <see cref="Names"/> is in the app's enum order, because profiles saved before 2026-11-16 hold the role as that enum's number: index n reads as
/// <c>Names[n]</c>, so no stored document has to be rewritten.
/// </summary>
public sealed class AccountRoles
{
    public AccountRoles(IReadOnlyList<string> names, string defaultRole, string adminRole)
    {
        if (names.Count == 0 || !names.Contains(defaultRole, StringComparer.Ordinal) || !names.Contains(adminRole, StringComparer.Ordinal))
        {
            throw new ArgumentException("The role list must name both the default role and the admin role.", nameof(names));
        }

        Names = names;
        Default = defaultRole;
        Admin = adminRole;
    }

    /// <summary>Every role, in the order of the app's enum (the order its old numeric documents used).</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>The role a new account gets.</summary>
    public string Default { get; }

    /// <summary>The role that manages accounts (the admin policy requires it).</summary>
    public string Admin { get; }

    /// <summary>The roles of an app whose role enum's values run 0, 1, 2... in declaration order, as every app's did when they were stored as numbers.</summary>
    public static AccountRoles FromEnum<TEnum>(TEnum defaultRole, TEnum adminRole) where TEnum : struct, Enum =>
        new(Enum.GetNames<TEnum>(), defaultRole.ToString(), adminRole.ToString());

    public bool IsKnown(string? role) => role is not null && Names.Contains(role, StringComparer.Ordinal);

    public bool IsAdmin(string? role) => string.Equals(role, Admin, StringComparison.Ordinal);

    /// <summary>
    /// The stored value as a role name: a number from a document saved before roles were strings becomes the name at that index; a name is kept as it is,
    /// even one this app does not know (it may come from a newer version of the app, and must survive a save). Empty or an out-of-range number reads as
    /// the default role, the same answer the old enum gave for a missing value.
    /// </summary>
    public string Normalize(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return Default;
        }

        if (!int.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            return stored;
        }

        return index < Names.Count ? Names[index] : Default;
    }
}
