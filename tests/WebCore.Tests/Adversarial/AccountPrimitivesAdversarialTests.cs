/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using WebCore.Accounts;

namespace WebCore.Tests.Adversarial;

// Hammering the pure account helpers with nulls, empties, boundaries, oversized and control-character input,
// and mistyped JSON. A test whose name ends in _Finding documents a behaviour that is probably wrong; see
// docs/testing/adversarial-review-2026-11-21.md for the write-up.
public sealed class AccountPrimitivesAdversarialTests
{
    // ---- PasswordRules ----

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("1234567", "1234567")]
    [InlineData("1234567", null)]
    public void PasswordRules_ShortOrBlank_IsTooShortBeforeMismatchIsConsidered(string? password, string? confirmation)
    {
        Assert.Equal(PasswordProblem.TooShort, PasswordRules.Check(password, confirmation));
    }

    [Fact]
    public void PasswordRules_EightCharactersMatching_IsAccepted()
    {
        Assert.Equal(PasswordProblem.None, PasswordRules.Check("12345678", "12345678"));
    }

    [Fact]
    public void PasswordRules_EightCharactersWithNullConfirmation_IsMismatch()
    {
        Assert.Equal(PasswordProblem.Mismatch, PasswordRules.Check("12345678", null));
    }

    [Fact]
    public void PasswordRules_MaximumLength_IsAcceptedAndOneMoreIsTooLong()
    {
        var at = new string('x', PasswordRules.MaximumLength);
        var over = new string('x', PasswordRules.MaximumLength + 1);

        Assert.Equal(PasswordProblem.None, PasswordRules.Check(at, at));
        Assert.Equal(PasswordProblem.TooLong, PasswordRules.Check(over, over));
    }

    [Fact]
    public void PasswordRules_OverLongAndMismatched_ReportsTooLongFirst()
    {
        var over = new string('x', PasswordRules.MaximumLength + 1);

        Assert.Equal(PasswordProblem.TooLong, PasswordRules.Check(over, "different"));
    }

    [Fact]
    public void PasswordRules_LengthIsCountedInUtf16Units_SoFourEmojiPassTheMinimum()
    {
        // Each emoji is a surrogate pair: four "characters" a person would count are eight units, which is the minimum.
        var fourEmoji = Emoji(4);

        Assert.Equal(8, fourEmoji.Length);
        Assert.Equal(PasswordProblem.None, PasswordRules.Check(fourEmoji, fourEmoji));
    }

    [Fact]
    public void PasswordRules_WhitespaceOnlyOfLegalLength_IsAccepted()
    {
        // Recorded, not endorsed: nothing rejects an all-space password of the minimum length.
        Assert.Equal(PasswordProblem.None, PasswordRules.Check("        ", "        "));
    }

    [Fact]
    public void PasswordRules_ValidateAndDescribeAndCode_AgreeForEveryProblem()
    {
        foreach (var problem in Enum.GetValues<PasswordProblem>())
        {
            var message = PasswordRules.Describe(problem);
            var code = PasswordRules.Code(problem);

            Assert.Equal(problem == PasswordProblem.None, message is null);
            Assert.Equal(problem == PasswordProblem.None, code.Length == 0);
        }
    }

    [Fact]
    public void PasswordRules_UndefinedProblemValue_DescribesAsNoProblem()
    {
        var undefined = (PasswordProblem)999;

        Assert.Null(PasswordRules.Describe(undefined));
        Assert.Equal(string.Empty, PasswordRules.Code(undefined));
    }

    // ---- PasswordHasher ----
    // PBKDF2 at 600k iterations costs real time, so these keep the number of hashes small.

    [Fact]
    public void PasswordHasher_EmptyPasswordRoundTrips()
    {
        var hash = PasswordHasher.Hash(string.Empty);

        Assert.True(PasswordHasher.Verify(string.Empty, hash));
        Assert.False(PasswordHasher.Verify(" ", hash));
    }

    [Fact]
    public void PasswordHasher_PasswordWithNulAndUnicode_RoundTrips()
    {
        const string tricky = "a\0bé\U0001F600 \t\r\n";
        var hash = PasswordHasher.Hash(tricky);

        Assert.True(PasswordHasher.Verify(tricky, hash));
        Assert.False(PasswordHasher.Verify("a", hash));
    }

    [Fact]
    public void PasswordHasher_HashOfNull_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => PasswordHasher.Hash(null!));
    }

    [Fact]
    public void PasswordHasher_VerifyWithNullPassword_ThrowsArgumentNull()
    {
        var hash = PasswordHasher.Hash("correct horse");

        Assert.Throws<ArgumentNullException>(() => PasswordHasher.Verify(null!, hash));
    }

    [Fact]
    public void PasswordHasher_VerifyWithNullHash_ThrowsRatherThanReturningFalse_Finding()
    {
        // A null stored hash (a user row with no password column) should answer false like every other malformed hash.
        Assert.Throws<NullReferenceException>(() => PasswordHasher.Verify("anything", null!));
    }

    [Theory(Skip = "FINDING F-A1: zero/negative iteration count throws ArgumentOutOfRange instead of returning false")]
    [InlineData("-1.AAAAAAAAAAAAAAAAAAAAAA==.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("0.AAAAAAAAAAAAAAAAAAAAAA==.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void PasswordHasher_NonPositiveIterationCount_IsRefusedNotThrown_Finding(string hash)
    {
        // A tampered or corrupted row with zero or negative iterations makes PBKDF2 throw an ArgumentOutOfRangeException
        // instead of answering false, so one bad row becomes an unhandled error on sign-in.
        Assert.False(PasswordHasher.Verify("anything", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("600000")]
    [InlineData("600000.onlyone")]
    [InlineData("abc.AAAA.AAAA")]
    [InlineData("600000.AAAA.@@@@")]
    [InlineData("600000.@@@@.AAAA")]
    public void PasswordHasher_MalformedHashes_AreFalse(string hash)
    {
        Assert.False(PasswordHasher.Verify("anything", hash));
    }

    [Fact(Skip = "FINDING F-A2 (security): a hash with an empty key verifies ANY password as true")]
    public void PasswordHasher_EmptyKeyPart_IsFalse_Finding()
    {
        // "600000.<salt>." has an empty key: PBKDF2 returns zero bytes and FixedTimeEquals(empty, empty) is true.
        // Needs write access to the user store to plant such a row, but then every account with it signs in with any password.
        Assert.False(PasswordHasher.Verify("anything", "600000.AAAAAAAAAAAAAAAAAAAAAA==."));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("not a hash", true)]
    [InlineData("599999.a.b", true)]
    [InlineData("600000.a.b", false)]
    [InlineData("600001.a.b", false)]
    [InlineData("-5.a.b", true)]
    [InlineData("600000", false)]
    public void PasswordHasher_NeedsRehash_TracksTheIterationCountOnly(string hash, bool expected)
    {
        Assert.Equal(expected, PasswordHasher.NeedsRehash(hash));
    }

    [Fact]
    public void PasswordHasher_NeedsRehashOfNull_ThrowsRatherThanAnswering_Finding()
    {
        Assert.Throws<NullReferenceException>(() => PasswordHasher.NeedsRehash(null!));
    }

    [Fact]
    public void PasswordHasher_SpendVerifyTime_NeverThrowsForAnyPassword()
    {
        Assert.Null(Record.Exception(() => PasswordHasher.SpendVerifyTime(string.Empty)));
        Assert.Null(Record.Exception(() => PasswordHasher.SpendVerifyTime(new string('x', PasswordRules.MaximumLength))));
    }

    // ---- LocalReturnUrl ----

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("/", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("\\\\evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("\\evil", "/")]
    [InlineData("/\t/evil.example", "/")]
    [InlineData("/ok\r\nSet-Cookie: x", "/")]
    [InlineData("　//evil.example", "/")]
    [InlineData("  //evil.example  ", "/")]
    [InlineData("https://evil.example", "/https://evil.example")]
    [InlineData("javascript:alert(1)", "/javascript:alert(1)")]
    [InlineData("evil.example/path", "/evil.example/path")]
    [InlineData("/%2F%2Fevil.example", "/%2F%2Fevil.example")]
    [InlineData("  /ok  ", "/ok")]
    public void LocalReturnUrl_HostileAndOddInput_NeverLeavesTheSite(string? input, string expected)
    {
        Assert.Equal(expected, LocalReturnUrl.Sanitize(input));
    }

    [Fact]
    public void LocalReturnUrl_RandomishInputs_AlwaysStartWithOneSlashAndNeverAControlCharacter()
    {
        // A deterministic pseudo-fuzz pass over the characters that matter to a redirect parser.
        const string alphabet = "/\\ \t\r\n:.%aZ0?#@";
        var random = new Random(20261121);
        var builder = new StringBuilder();

        for (var i = 0; i < 5000; i++)
        {
            builder.Clear();
            var length = random.Next(0, 12);
            for (var j = 0; j < length; j++)
            {
                builder.Append(alphabet[random.Next(alphabet.Length)]);
            }

            var result = LocalReturnUrl.Sanitize(builder.ToString());

            Assert.StartsWith("/", result);
            Assert.False(result.Length > 1 && result[1] is '/' or '\\', $"protocol-relative escape from input '{builder}' gave '{result}'");
            Assert.DoesNotContain(result, c => char.IsControl(c));
        }
    }

    [Fact]
    public void LocalReturnUrl_HugeInput_IsReturnedWithoutALengthCap_Finding()
    {
        // No length limit: a 64 KB return URL passes straight through into a redirect header. Recorded, not endorsed.
        var huge = "/" + new string('a', 65_536);

        Assert.Equal(huge, LocalReturnUrl.Sanitize(huge));
    }

    // ---- DisplayNames ----

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("  Ada   Lovelace  ", "Ada Lovelace")]
    [InlineData("a\t\tb\r\nc", "a b c")]
    [InlineData(" x ", "x")]
    public void DisplayNames_Tidy_CollapsesAndTrimsWhitespace(string? input, string expected)
    {
        Assert.Equal(expected, DisplayNames.Tidy(input));
    }

    [Fact]
    public void DisplayNames_LengthBoundary_IsForty()
    {
        Assert.True(DisplayNames.IsValid(new string('a', DisplayNames.MaxLength)));
        Assert.False(DisplayNames.IsValid(new string('a', DisplayNames.MaxLength + 1)));
    }

    [Fact]
    public void DisplayNames_LengthIsUtf16Units_SoTwentyEmojiFitAndTwentyOneDoNot()
    {
        Assert.True(DisplayNames.IsValid(Emoji(20)));
        Assert.False(DisplayNames.IsValid(Emoji(21)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\t\n")]
    public void DisplayNames_BlankOrNull_IsInvalid(string? name)
    {
        Assert.False(DisplayNames.IsValid(name));
    }

    [Fact]
    public void DisplayNames_ZeroWidthSpaceOnly_IsAcceptedAsAName_Finding()
    {
        // U+200B is not whitespace to .NET, so a name made only of it is "valid" and shows as a blank label.
        Assert.True(DisplayNames.IsValid("​"));
    }

    [Fact]
    public void DisplayNames_MarkupIsAcceptedAsText_RelyingOnEncodingAtTheEdge()
    {
        Assert.Equal("<b>x</b>", DisplayNames.Tidy("<b>x</b>"));
    }

    [Fact]
    public void DisplayNames_Normalize_LowerCasesAfterTidying()
    {
        Assert.Equal("ada lovelace", DisplayNames.Normalize("  ADA   Lovelace "));
        Assert.Equal(string.Empty, DisplayNames.Normalize(null));
    }

    // ---- AccountRoles ----

    [Fact]
    public void AccountRoles_EmptyNameList_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new AccountRoles([], "Player", "Admin"));
    }

    [Fact]
    public void AccountRoles_DefaultOrAdminMissingFromTheList_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new AccountRoles(["Player", "Admin"], "Guest", "Admin"));
        Assert.Throws<ArgumentException>(() => new AccountRoles(["Player", "Admin"], "Player", "Root"));
    }

    [Fact]
    public void AccountRoles_NullNameList_ThrowsNullReference_Finding()
    {
        // The guard reads Count before checking for null, so a null list is a NullReferenceException, not an ArgumentNullException.
        Assert.Throws<NullReferenceException>(() => new AccountRoles(null!, "Player", "Admin"));
    }

    [Fact]
    public void AccountRoles_RoleNamesAreCaseSensitive()
    {
        Assert.Throws<ArgumentException>(() => new AccountRoles(["Player", "Admin"], "player", "Admin"));
        var roles = new AccountRoles(["Player", "Admin"], "Player", "Admin");
        Assert.False(roles.IsAdmin("admin"));
        Assert.False(roles.IsKnown("ADMIN"));
    }

    [Fact]
    public void AccountRoles_DuplicateNames_AreAcceptedAsIs()
    {
        var roles = new AccountRoles(["Player", "Player", "Admin"], "Player", "Admin");

        Assert.Equal(3, roles.Names.Count);
    }

    [Fact]
    public void AccountRoles_DefaultEqualToAdmin_MakesEveryNewAccountAnAdmin_Finding()
    {
        // Nothing stops the default and the admin role being the same name; then every account created is an admin.
        var roles = AccountRoles.FromEnum(TestRole.Player, TestRole.Player);

        Assert.True(roles.IsAdmin(roles.Default));
    }

    [Fact]
    public void AccountRoles_FromEnumWithAnUndefinedValue_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => AccountRoles.FromEnum(TestRole.Player, (TestRole)99));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AccountRoles_NormalizeBlankStored_IsTheDefault(string? stored)
    {
        Assert.Equal("Player", Roles().Normalize(stored));
    }

    [Theory]
    [InlineData("0", "Player")]
    [InlineData("1", "Organizer")]
    [InlineData("2", "Admin")]
    [InlineData("3", "Player")]
    [InlineData("99", "Player")]
    [InlineData("2147483648", "2147483648")]
    [InlineData("Admin", "Admin")]
    [InlineData("admin", "admin")]
    public void AccountRoles_NormalizeStored_NumbersBecomeNamesAndOutOfRangeFallsBack(string stored, string expected)
    {
        Assert.Equal(expected, Roles().Normalize(stored));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData(" 1")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData("1e0")]
    public void AccountRoles_NormalizeNumericLookingButNotPlain_PassesThroughUnchanged_Finding(string stored)
    {
        // Only plain digits are read as an index. "-1" or " 1" comes back raw, which is not a role; callers must check IsKnown.
        var normalized = Roles().Normalize(stored);

        Assert.Equal(stored, normalized);
        Assert.False(Roles().IsKnown(normalized));
    }

    [Fact]
    public void AccountRoles_IsKnownAndIsAdmin_HandleNullAndEmpty()
    {
        var roles = Roles();

        Assert.False(roles.IsKnown(null));
        Assert.False(roles.IsKnown(string.Empty));
        Assert.False(roles.IsAdmin(null));
        Assert.False(roles.IsAdmin(string.Empty));
        Assert.True(roles.IsAdmin("Admin"));
    }

    // ---- AccountClaims ----

    [Fact]
    public void AccountClaims_NullPrefix_QuietlyBecomesTheBareNames_Finding()
    {
        var claims = new AccountClaims(null!);

        Assert.Equal("email_confirmed", claims.EmailConfirmed);
    }

    [Fact]
    public void AccountClaims_CreatePrincipal_WithNullEmail_ThrowsArgumentNull()
    {
        var user = new User { Email = null! };

        Assert.Throws<ArgumentNullException>(() => Claims().CreatePrincipal(user, "Player", "test"));
    }

    [Fact]
    public void AccountClaims_CreatePrincipal_WithNullRole_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => Claims().CreatePrincipal(new User(), null!, "test"));
    }

    [Fact]
    public void AccountClaims_CreatePrincipal_WithEmptyIdAndName_KeepsEmptyClaims()
    {
        var principal = Claims().CreatePrincipal(new User { Id = string.Empty, DisplayName = string.Empty }, "Player", "test");

        Assert.Equal(string.Empty, AccountClaims.UserIdOf(principal));
        Assert.Equal(string.Empty, principal.Identity!.Name);
    }

    [Fact]
    public void AccountClaims_CreatePrincipal_WithNullAuthenticationType_IsNotAuthenticated_Finding()
    {
        // A caller that forgets the scheme name gets a principal that reports as unauthenticated, which sign-in then rejects.
        var principal = Claims().CreatePrincipal(new User(), "Player", null!);

        Assert.False(principal.Identity!.IsAuthenticated);
    }

    [Fact]
    public void AccountClaims_EmptySecurityStamp_IsLeftOutOfTheClaims()
    {
        var principal = Claims().CreatePrincipal(new User { SecurityStamp = string.Empty }, "Player", "test");

        Assert.False(principal.HasClaim(c => c.Type == "security_stamp"));
    }

    [Fact]
    public void AccountClaims_MasqueradeWithNullOriginalAdmin_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => Claims().CreateMasqueradePrincipal(new User(), "Player", null!, "test"));
    }

    [Fact]
    public void AccountClaims_IsMasqueradingAndConfirmed_OnAnEmptyPrincipal_AreFalse()
    {
        var empty = new ClaimsPrincipal();

        Assert.False(Claims().IsMasquerading(empty));
        Assert.False(Claims().IsEmailConfirmed(empty));
        Assert.Null(AccountClaims.RoleOf(empty));
        Assert.Null(AccountClaims.UserIdOf(empty));
    }

    [Fact]
    public void AccountClaims_WithRole_OnAPrincipalWithNoIdentity_AddsTheRole()
    {
        var principal = AccountClaims.WithRole(new ClaimsPrincipal(), "Admin", "test");

        Assert.Equal("Admin", AccountClaims.RoleOf(principal));
    }

    [Fact]
    public void AccountClaims_WithRole_ReplacesEveryExistingRoleClaim()
    {
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(ClaimTypes.Role, "Player"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "Organizer"));

        var principal = AccountClaims.WithRole(new ClaimsPrincipal(identity), "Admin", "test");

        Assert.Equal(["Admin"], principal.FindAll(ClaimTypes.Role).Select(c => c.Value));
    }

    [Fact]
    public void AccountClaims_RoleOf_ReturnsTheFirstOfSeveralRoleClaims()
    {
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(ClaimTypes.Role, "Player"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));

        Assert.Equal("Player", AccountClaims.RoleOf(new ClaimsPrincipal(identity)));
    }

    // ---- UserProfile role JSON (RoleNameJsonConverter) ----

    [Theory]
    [InlineData("{\"Role\":null}", null)]
    [InlineData("{\"Role\":\"\"}", "")]
    [InlineData("{\"Role\":\"Admin\"}", "Admin")]
    [InlineData("{\"Role\":1}", "1")]
    [InlineData("{\"Role\":-5}", "-5")]
    [InlineData("{\"Role\":0}", "0")]
    [InlineData("{\"Role\":\"\\u00e9t\\u00e9\"}", "été")]
    public void RoleNameJsonConverter_ReadsNamesNumbersAndNulls(string json, string? expected)
    {
        var profile = JsonSerializer.Deserialize<UserProfile>(json);

        Assert.Equal(expected, profile!.Role);
    }

    [Theory]
    [InlineData("{\"Role\":true}")]
    [InlineData("{\"Role\":{}}")]
    [InlineData("{\"Role\":[]}")]
    public void RoleNameJsonConverter_BooleansObjectsAndArrays_AreJsonExceptions(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UserProfile>(json));
    }

    [Theory]
    [InlineData("{\"Role\":1.5}")]
    [InlineData("{\"Role\":9223372036854775808}")]
    [InlineData("{\"Role\":1e30}")]
    public void RoleNameJsonConverter_FractionalOrHugeNumbers_AreJsonExceptions(string json)
    {
        // Checked: these come back as JsonException, so a store that skips corrupt documents does skip them.
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UserProfile>(json));
    }

    [Fact]
    public void RoleNameJsonConverter_Writes_TheNameAsAString()
    {
        var json = JsonSerializer.Serialize(new UserProfile { Role = "Admin" });

        Assert.Contains("\"Role\":\"Admin\"", json);
    }

    // ---- Clone ----

    [Fact]
    public void User_Clone_IsADeepEnoughCopy_OfUnknownFieldsAndScalars()
    {
        var original = new User { Id = "u1", UnknownFields = new Dictionary<string, JsonElement>() };
        var copy = original.Clone();

        copy.Id = "u2";
        copy.UnknownFields!["extra"] = JsonDocument.Parse("1").RootElement.Clone();

        Assert.Equal("u1", original.Id);
        Assert.False(original.UnknownFields.ContainsKey("extra"));
    }

    [Fact]
    public void User_Clone_OfNullUnknownFields_StaysNull()
    {
        Assert.Null(new User { UnknownFields = null }.Clone().UnknownFields);
    }

    [Fact]
    public void UserProfile_Clone_IsIndependentOfTheOriginal()
    {
        var original = new UserProfile { UserId = "u1", Role = "Player", UnknownFields = new Dictionary<string, JsonElement>() };
        var copy = original.Clone();

        copy.Role = "Admin";

        Assert.Equal("Player", original.Role);
    }

    [Fact]
    public void AccountMessages_NullOrUnknownStatus_GetsTheGenericText()
    {
        Assert.Equal(AccountMessages.ForPasswordError(null), AccountMessages.ForPasswordError("no-such-status"));
        Assert.Equal(AccountMessages.ForCodeError(null), AccountMessages.ForCodeError(string.Empty));
    }

    [Fact]
    public void AccountMessages_StatusCodesAreCaseSensitive_SoAShoutedCodeGetsTheGenericText_Finding()
    {
        // A status string written in another case quietly loses its specific message.
        Assert.Equal("That didn't work - please try again.", AccountMessages.ForPasswordError("SHORT"));
    }

    private static string Emoji(int count) => string.Concat(Enumerable.Repeat("\U0001F600", count));

    private static AccountRoles Roles() => new(["Player", "Organizer", "Admin"], "Player", "Admin");

    private static AccountClaims Claims() => new("test_");

    private enum TestRole
    {
        Player,
        Organizer,
        Admin,
    }
}
