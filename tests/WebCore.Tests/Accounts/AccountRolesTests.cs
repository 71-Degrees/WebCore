/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using WebCore.Accounts;
using WebCore.Storage;

namespace WebCore.Tests.Accounts;

public sealed class AccountRolesTests : IDisposable
{
    private enum TriviaRole
    {
        Player,
        Organizer,
        Admin,
    }

    // A profile exactly as CrateDigger stored it before roles were strings (copied from a local store, 2026-11-16): Admin was the number 1.
    private const string LegacyAdminProfile = """
        {
          "UserId": "b0835b1ef4b4408c8852fb5ad941e256",
          "DisplayName": "PJJ",
          "Role": 1,
          "IsDisabled": false,
          "DisabledAt": null
        }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WebCoreAccountRoles_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void FromEnum_ListsTheNamesInEnumOrder()
    {
        var roles = AccountRoles.FromEnum(TriviaRole.Player, TriviaRole.Admin);

        Assert.Equal(["Player", "Organizer", "Admin"], roles.Names);
        Assert.Equal("Player", roles.Default);
        Assert.True(roles.IsAdmin("Admin"));
        Assert.False(roles.IsAdmin("Organizer"));
    }

    [Theory]
    [InlineData("0", "Player")]
    [InlineData("1", "Organizer")]
    [InlineData("2", "Admin")]
    [InlineData("9", "Player")]
    [InlineData("", "Player")]
    [InlineData(null, "Player")]
    [InlineData("Organizer", "Organizer")]
    [InlineData("Host", "Host")]
    public void Normalize_ReadsOldNumbersByIndex_AndKeepsNames(string? stored, string expected) =>
        Assert.Equal(expected, AccountRoles.FromEnum(TriviaRole.Player, TriviaRole.Admin).Normalize(stored));

    [Fact]
    public void ARoleListMustNameItsDefaultAndAdmin() =>
        Assert.Throws<ArgumentException>(() => new AccountRoles(["Member"], "Member", "Admin"));

    [Fact]
    public async Task ALegacyNumericProfile_ReadsAsItsRoleName_AndIsSavedAsTheName()
    {
        var store = new FileDocumentStore(_folder);
        var userId = "b0835b1ef4b4408c8852fb5ad941e256";
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path.Combine(_folder, DocumentNames.For(userId, "user")), LegacyAdminProfile);
        var repository = new UserProfileRepository(store, TestRoles.MemberAdmin);

        var profile = await repository.GetAsync(userId);
        Assert.Equal("Admin", profile!.Role);
        Assert.Equal("Admin", Assert.Single(await repository.ListAllAsync()).Role);

        await repository.SetDisabledAsync(userId, true);
        var saved = await File.ReadAllTextAsync(Path.Combine(_folder, DocumentNames.For(userId, "user")));
        Assert.Contains("\"Role\": \"Admin\"", saved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FieldsAnAppAddsToAProfile_SurviveASaveByWebCore()
    {
        var store = new FileDocumentStore(_folder);
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(
            Path.Combine(_folder, DocumentNames.For("u1", "user")),
            """{ "UserId": "u1", "DisplayName": "Sam", "Role": "Member", "TeamName": "Quiz Khalifa", "AvatarId": 7 }""");
        var repository = new UserProfileRepository(store, TestRoles.MemberAdmin);

        await repository.SetRoleAsync("u1", "Admin");

        var saved = await File.ReadAllTextAsync(Path.Combine(_folder, DocumentNames.For("u1", "user")));
        Assert.Contains("Quiz Khalifa", saved, StringComparison.Ordinal);
        Assert.Contains("\"AvatarId\": 7", saved, StringComparison.Ordinal);
        Assert.Equal("Admin", (await repository.GetAsync("u1"))!.Role);
    }

    [Fact]
    public void AClonedProfile_HasItsOwnUnknownFields()
    {
        var profile = new UserProfile { UnknownFields = [] };
        var copy = profile.Clone();
        copy.UnknownFields!["x"] = default;

        Assert.Empty(profile.UnknownFields);
    }

    [Fact]
    public void ClaimNames_CarryTheAppsPrefix_AndTheStampIsIssuedOnlyWhenSet()
    {
        var claims = new AccountClaims("cratedigger:");
        Assert.Equal("cratedigger:email_confirmed", claims.EmailConfirmed);
        Assert.Equal("cratedigger:masquerade_admin", claims.MasqueradeOriginalAdminId);

        var user = new User { Email = "a@example.com", DisplayName = "A" };
        Assert.Null(claims.CreatePrincipal(user, "Member", "test").FindFirst(claims.SecurityStamp));

        user.RotateSecurityStamp();
        Assert.Equal(user.SecurityStamp, claims.CreatePrincipal(user, "Member", "test").FindFirst(claims.SecurityStamp)!.Value);
    }

    [Theory]
    [InlineData("short", "short", PasswordProblem.TooShort)]
    [InlineData("long-enough", "different!", PasswordProblem.Mismatch)]
    [InlineData("long-enough", "long-enough", PasswordProblem.None)]
    public void PasswordRules_NameTheFirstProblem(string password, string confirmation, PasswordProblem expected) =>
        Assert.Equal(expected, PasswordRules.Check(password, confirmation));

    [Fact]
    public void PasswordRules_RefuseAPasswordOverTheCeiling()
    {
        var tooLong = new string('x', PasswordRules.MaximumLength + 1);
        Assert.Equal(PasswordProblem.TooLong, PasswordRules.Check(tooLong, tooLong));
        Assert.Equal("long", PasswordRules.Code(PasswordProblem.TooLong));
    }
}
