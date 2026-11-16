/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using WebCore.Accounts;

namespace WebCore.Tests.Accounts;

/// <summary>The role list the account tests use: CrateDigger's (Member = 0, Admin = 1).</summary>
internal static class TestRoles
{
    public static readonly AccountRoles MemberAdmin = new(["Member", "Admin"], "Member", "Admin");
}
