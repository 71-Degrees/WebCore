/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Net;
using static WebCore.Hosting.Tests.Adversarial.EndpointSite;
using Microsoft.Extensions.DependencyInjection;
using WebCore.Accounts;

namespace WebCore.Hosting.Tests.Adversarial;

// The admin users page posts (role, disable, masquerade and resume) and the password endpoints (emailed reset, change) under non-admin callers, a masquerading
// admin, unknown and hostile ids, missing and odd form values, and oversized passwords. Each case builds its own site; nothing leaves the process.
public sealed class AdminAndPasswordEndpointsAdversarialTests
{
    private const string Admin = "owner@example.com";
    private const string Member = "sam@example.com";
    private const string Pw = EndpointSite.Password;
    private const string NewPw = "Another horse 7";

    private static Task<EndpointSite> StartAsync() => EndpointSite.StartAccountsAsync(EndpointSite.NewFolder());

    // Seeds an admin and a member, then leaves the browser signed in as the admin.
    private static async Task<(User Admin, User Member)> SeedAndSignInAsAdminAsync(EndpointSite site)
    {
        var admin = await site.CreateAccountAsync(Admin, Pw, role: "Admin");
        var member = await site.CreateAccountAsync(Member, Pw);
        await site.SignInAsync(Admin, Pw);
        return (admin, member);
    }

    private static async Task<string?> RoleOfAsync(EndpointSite site, string userId) =>
        (await site.Services.GetRequiredService<IUserProfileRepository>().GetAsync(userId))?.Role;

    // ---- Admin: roles ----

    [Fact]
    public async Task AdminRoles_SignedOut_AreSentToLogin()
    {
        await using var site = await StartAsync();

        var response = await site.PostFormAsync("/admin/users/role", EndpointSite.Fields(("userId", "x"), ("role", "Admin")));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/account/login?ReturnUrl", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task AdminRoles_AsAMember_AreDenied()
    {
        await using var site = await StartAsync();
        var member = await site.CreateAccountAsync(Member, Pw);
        await site.SignInAsync(Member, Pw);

        var response = await site.PostFormAsync("/admin/users/role", EndpointSite.Fields(("userId", member.Id), ("role", "Admin")));

        Assert.Contains("/account/denied?ReturnUr", EndpointSite.LocationOf(response));
        Assert.Equal("Member", await RoleOfAsync(site, member.Id));
    }

    [Fact]
    public async Task AdminRoles_GetIsNotAllowed()
    {
        await using var site = await StartAsync();

        var response = await site.GetAsync("/admin/users/role");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task AdminRoles_WithoutAToken_IsBadRequest()
    {
        await using var site = await StartAsync();
        await SeedAndSignInAsAdminAsync(site);

        var response = await site.PostFormAsync("/admin/users/role", EndpointSite.Fields(("userId", "x"), ("role", "Admin")), withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdminRoles_ValidChange_IsStoredAndReported()
    {
        await using var site = await StartAsync();
        var (_, member) = await SeedAndSignInAsAdminAsync(site);

        var response = await site.PostFormAsync("/admin/users/role", EndpointSite.Fields(("userId", member.Id), ("role", "Admin")));

        Assert.Equal("/admin/users?done=role", EndpointSite.LocationOf(response));
        Assert.Equal("Admin", await RoleOfAsync(site, member.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Owner")]
    [InlineData("admin")]
    [InlineData("Admin\n")]
    [InlineData(" Admin")]
    public async Task AdminRoles_UnknownOrOddRole_IsRefused(string role)
    {
        await using var site = await StartAsync();
        var (_, member) = await SeedAndSignInAsAdminAsync(site);

        var response = await site.PostFormAsync("/admin/users/role", EndpointSite.Fields(("userId", member.Id), ("role", role)));

        Assert.Equal("/admin/users?error=badrole", EndpointSite.LocationOf(response));
        Assert.Equal("Member", await RoleOfAsync(site, member.Id));
    }

    [Fact]
    public async Task AdminRoles_OversizedRole_IsRefused()
    {
        await using var site = await StartAsync();
        var (_, member) = await SeedAndSignInAsAdminAsync(site);

        var response = await site.PostFormAsync("/admin/users/role", EndpointSite.Fields(("userId", member.Id), ("role", Big(64 * 1024))));

        Assert.Equal("/admin/users?error=badrole", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task AdminRoles_OnItself_IsRefused()
    {
        await using var site = await StartAsync();
        var (admin, _) = await SeedAndSignInAsAdminAsync(site);

        var response = await site.PostFormAsync("/admin/users/role", EndpointSite.Fields(("userId", admin.Id), ("role", "Member")));

        Assert.Equal("/admin/users?error=self", EndpointSite.LocationOf(response));
        Assert.Equal("Admin", await RoleOfAsync(site, admin.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../x")]
    [InlineData("does-not-exist")]
    public async Task AdminRoles_UnknownOrMalformedTarget_IsReportedAsUnknown(string userId)
    {
        await using var site = await StartAsync();
        await SeedAndSignInAsAdminAsync(site);

        var response = await site.PostFormAsync("/admin/users/role", EndpointSite.Fields(("userId", userId), ("role", "Admin")));

        Assert.Equal("/admin/users?error=unknown", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task AdminRoles_ThousandCharacterTarget_IsReportedAsUnknown()
    {
        await using var site = await StartAsync();
        await SeedAndSignInAsAdminAsync(site);

        var response = await site.PostFormAsync("/admin/users/role", EndpointSite.Fields(("userId", Big(1000)), ("role", "Admin")));

        Assert.Equal("/admin/users?error=unknown", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task AdminRoles_MissingTargetField_IsReportedAsUnknown()
    {
        await using var site = await StartAsync();
        await SeedAndSignInAsAdminAsync(site);

        var response = await site.PostFormAsync("/admin/users/role", EndpointSite.Fields(("role", "Admin")));

        Assert.Equal("/admin/users?error=unknown", EndpointSite.LocationOf(response));
    }

    // ---- Admin: disabling ----

    [Fact]
    public async Task AdminDisable_EndsTheDisabledAccountsOpenSession()
    {
        await using var site = await StartAsync();
        var (_, member) = await SeedAndSignInAsAdminAsync(site);
        await site.SignInAsync(Member, Pw);
        var memberCookie = site.CookieValue(EndpointSite.SignInCookie)!;
        await site.SignInAsync(Admin, Pw);

        var response = await site.PostFormAsync("/admin/users/disabled", EndpointSite.Fields(("userId", member.Id), ("disabled", "true")));

        Assert.Equal("/admin/users?done=disabled", EndpointSite.LocationOf(response));
        var replay = await site.SendWithCookieAsync(HttpMethod.Get, "/probe/who", EndpointSite.SignInCookie, memberCookie);
        Assert.Equal("anonymous", await replay.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AdminDisable_OnItself_IsRefused()
    {
        await using var site = await StartAsync();
        var (admin, _) = await SeedAndSignInAsAdminAsync(site);

        var response = await site.PostFormAsync("/admin/users/disabled", EndpointSite.Fields(("userId", admin.Id), ("disabled", "true")));

        Assert.Equal("/admin/users?error=self", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task AdminDisable_UnknownTarget_IsReportedAsUnknown()
    {
        await using var site = await StartAsync();
        await SeedAndSignInAsAdminAsync(site);

        var response = await site.PostFormAsync("/admin/users/disabled", EndpointSite.Fields(("userId", "../x"), ("disabled", "true")));

        Assert.Equal("/admin/users?error=unknown", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task AdminDisable_AnythingOtherThanLowerCaseTrue_ReEnables_AsRecorded()
    {
        // Only the exact text "true" disables; "TRUE", "yes" or a missing field re-enable the account without a second look.
        await using var site = await StartAsync();
        await SeedAndSignInAsAdminAsync(site);
        var target = await site.CreateAccountAsync("second@example.com", Pw, disabled: true);
        await site.PostFormAsync("/admin/users/disabled", EndpointSite.Fields(("userId", target.Id), ("disabled", "TRUE")));

        var profile = await site.Services.GetRequiredService<IUserProfileRepository>().GetAsync(target.Id);

        Assert.False(profile!.IsDisabled);
    }

    // ---- Admin: masquerade and resume ----

    [Fact]
    public async Task Masquerade_AsAMember_ShowsTheSiteAsThemAndRedirectsHome()
    {
        await using var site = await StartAsync();
        var (_, member) = await SeedAndSignInAsAdminAsync(site);

        var response = await site.PostFormAsync("/admin/users/masquerade", EndpointSite.Fields(("userId", member.Id)));

        Assert.Equal("/", EndpointSite.LocationOf(response));
        Assert.Equal($"id={member.Id};role=Member;masquerade=True", await site.WhoAsync());
    }

    [Fact]
    public async Task Masquerade_AsAMember_CannotUseTheAdminForms()
    {
        await using var site = await StartAsync();
        var (_, member) = await SeedAndSignInAsAdminAsync(site);
        await site.PostFormAsync("/admin/users/masquerade", EndpointSite.Fields(("userId", member.Id)));

        var response = await site.PostFormAsync("/admin/users/disabled", EndpointSite.Fields(("userId", member.Id), ("disabled", "true")));

        Assert.Contains("/account/denied?ReturnUr", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task Masquerade_WhileAlreadyMasquerading_IsRefused_AndTheRealAdminIsKept()
    {
        // Only reachable with an admin as the target (a member cannot reach the admin forms while masquerading).
        await using var site = await StartAsync();
        var (admin, _) = await SeedAndSignInAsAdminAsync(site);
        var second = await site.CreateAccountAsync("second@example.com", Pw, role: "Admin");
        await site.PostFormAsync("/admin/users/masquerade", EndpointSite.Fields(("userId", second.Id)));

        var response = await site.PostFormAsync("/admin/users/masquerade", EndpointSite.Fields(("userId", admin.Id)));

        Assert.Equal("/admin/users?error=already", EndpointSite.LocationOf(response));
        Assert.Contains($"id={second.Id}", await site.WhoAsync());
    }

    [Fact]
    public async Task Masquerade_OfItself_IsRefused()
    {
        await using var site = await StartAsync();
        var (admin, _) = await SeedAndSignInAsAdminAsync(site);

        var response = await site.PostFormAsync("/admin/users/masquerade", EndpointSite.Fields(("userId", admin.Id)));

        Assert.Equal("/admin/users?error=self", EndpointSite.LocationOf(response));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../x")]
    [InlineData("missing")]
    public async Task Masquerade_UnknownTarget_IsReportedAsUnknown(string userId)
    {
        await using var site = await StartAsync();
        await SeedAndSignInAsAdminAsync(site);

        var response = await site.PostFormAsync("/admin/users/masquerade", EndpointSite.Fields(("userId", userId)));

        Assert.Equal("/admin/users?error=unknown", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task Masquerade_DisabledTarget_IsRefused()
    {
        await using var site = await StartAsync();
        await SeedAndSignInAsAdminAsync(site);
        var off = await site.CreateAccountAsync("off@example.com", Pw, disabled: true);

        var response = await site.PostFormAsync("/admin/users/masquerade", EndpointSite.Fields(("userId", off.Id)));

        Assert.Equal("/admin/users?error=disabled", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task Masquerade_SignedOut_IsSentToLogin()
    {
        await using var site = await StartAsync();
        var member = await site.CreateAccountAsync(Member, Pw);

        var response = await site.PostFormAsync("/admin/users/masquerade", EndpointSite.Fields(("userId", member.Id)));

        Assert.Contains("/account/login?ReturnUrl", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task Resume_ReturnsTheRealAdminWithoutALogin()
    {
        await using var site = await StartAsync();
        var (admin, member) = await SeedAndSignInAsAdminAsync(site);
        await site.PostFormAsync("/admin/users/masquerade", EndpointSite.Fields(("userId", member.Id)));

        var response = await site.PostFormAsync("/account/masquerade-stop", []);

        Assert.Equal("/admin/users", EndpointSite.LocationOf(response));
        Assert.Equal($"id={admin.Id};role=Admin;masquerade=False", await site.WhoAsync());
    }

    [Fact]
    public async Task Resume_WhenNotMasquerading_GoesHome()
    {
        await using var site = await StartAsync();
        await SeedAndSignInAsAdminAsync(site);

        var response = await site.PostFormAsync("/account/masquerade-stop", []);

        Assert.Equal("/", EndpointSite.LocationOf(response));
    }

    // ---- Password reset by emailed code ----

    [Fact]
    public async Task ResetSend_GetIsNotAllowed()
    {
        await using var site = await StartAsync();

        var response = await site.GetAsync("/account/reset-send");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task ResetSend_KnownAndUnknownAddresses_HitTheSameLimits()
    {
        // Past the per-address allowance the known and unknown addresses answer the same way: the limit cannot show which addresses are registered.
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Member, Pw);

        var knownAnswers = new List<string>();
        var unknownAnswers = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            knownAnswers.Add(EndpointSite.LocationOf(await site.PostFormAsync("/account/reset-send", EndpointSite.Fields(("email", Member)))));
            unknownAnswers.Add(EndpointSite.LocationOf(await site.PostFormAsync("/account/reset-send", EndpointSite.Fields(("email", "nobody@example.com")))));
        }

        Assert.Equal(unknownAnswers.Select(a => a.Contains("ratelimited")), knownAnswers.Select(a => a.Contains("ratelimited")));
        Assert.Contains("codeError=ratelimited", knownAnswers[5]);
        Assert.Contains("codeError=ratelimited", unknownAnswers[5]);
    }

    [Fact]
    public async Task ResetSend_UnknownAddress_SendsNothing()
    {
        await using var site = await StartAsync();

        var response = await site.PostFormAsync("/account/reset-send", EndpointSite.Fields(("email", "nobody@example.com")));

        Assert.StartsWith("/account/forgot?step=code", EndpointSite.LocationOf(response));
        Assert.Empty(site.Mail.Sent);
    }

    [Fact]
    public async Task ResetFlow_CodeThenNewPassword_ChangesTheSignIn()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Member, Pw);
        await site.PostFormAsync("/account/reset-send", EndpointSite.Fields(("email", Member)));
        var code = site.Mail.CodeFor(Member);

        var response = await site.PostFormAsync("/account/reset-submit", EndpointSite.Fields(
            ("email", Member), ("code", code!), ("password", NewPw), ("confirmPassword", NewPw)));

        Assert.StartsWith("/account/login?reset=1", EndpointSite.LocationOf(response));
        Assert.Equal("/", EndpointSite.LocationOf(await site.SignInAsync(Member, NewPw)));
        Assert.Contains("error=1", EndpointSite.LocationOf(await site.SignInAsync(Member, Pw)));
    }

    [Fact]
    public async Task ResetSubmit_MistypedPassword_DoesNotSpendTheCode()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Member, Pw);
        await site.PostFormAsync("/account/reset-send", EndpointSite.Fields(("email", Member)));
        var code = site.Mail.CodeFor(Member);

        var typo = await site.PostFormAsync("/account/reset-submit", EndpointSite.Fields(
            ("email", Member), ("code", code!), ("password", NewPw), ("confirmPassword", "Another horse 8")));
        var retry = await site.PostFormAsync("/account/reset-submit", EndpointSite.Fields(
            ("email", Member), ("code", code!), ("password", NewPw), ("confirmPassword", NewPw)));

        Assert.Contains("passwordError=mismatch", EndpointSite.LocationOf(typo));
        Assert.StartsWith("/account/login?reset=1", EndpointSite.LocationOf(retry));
    }

    [Fact]
    public async Task ResetSubmit_OversizedPassword_IsRefusedBeforeTheCodeIsChecked()
    {
        await using var site = await StartAsync();

        var response = await site.PostFormAsync("/account/reset-submit", EndpointSite.Fields(
            ("email", Member), ("code", "000000"), ("password", Big(64 * 1024)), ("confirmPassword", Big(64 * 1024))));

        Assert.Contains("passwordError=long", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task ResetSubmit_UnknownAddressAndAnyCode_ReportsExpired()
    {
        await using var site = await StartAsync();

        var response = await site.PostFormAsync("/account/reset-submit", EndpointSite.Fields(
            ("email", "nobody@example.com"), ("code", "123456"), ("password", NewPw), ("confirmPassword", NewPw)));

        Assert.Contains("codeError=expired", EndpointSite.LocationOf(response));
    }

    // ---- Change password (signed in) ----

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_IsRefused()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Member, Pw);
        await site.SignInAsync(Member, Pw);

        var response = await site.PostFormAsync("/account/password-change", EndpointSite.Fields(
            ("currentPassword", "Wrong horse 1"), ("password", NewPw), ("confirmPassword", NewPw)));

        Assert.Equal("/account?passwordError=wrong", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task ChangePassword_MissingCurrentPassword_IsRefused()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Member, Pw);
        await site.SignInAsync(Member, Pw);

        var response = await site.PostFormAsync("/account/password-change", EndpointSite.Fields(("password", NewPw), ("confirmPassword", NewPw)));

        Assert.Equal("/account?passwordError=wrong", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task ChangePassword_EleventhGuessAtTheCurrentPassword_IsRateLimited()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Member, Pw);
        await site.SignInAsync(Member, Pw);

        for (var i = 0; i < 10; i++)
        {
            await site.PostFormAsync("/account/password-change", EndpointSite.Fields(("currentPassword", "Wrong horse " + i), ("password", NewPw), ("confirmPassword", NewPw)));
        }

        var eleventh = await site.PostFormAsync("/account/password-change", EndpointSite.Fields(
            ("currentPassword", Pw), ("password", NewPw), ("confirmPassword", NewPw)));

        Assert.Equal("/account?passwordError=ratelimited", EndpointSite.LocationOf(eleventh));
    }

    [Fact]
    public async Task ChangePassword_OversizedNewPassword_IsRefused()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Member, Pw);
        await site.SignInAsync(Member, Pw);

        var response = await site.PostFormAsync("/account/password-change", EndpointSite.Fields(
            ("currentPassword", Pw), ("password", Big(64 * 1024)), ("confirmPassword", Big(64 * 1024))));

        Assert.Equal("/account?passwordError=long", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task ChangePassword_WhileMasquerading_IsRefusedForTheAdmin()
    {
        await using var site = await StartAsync();
        var (_, member) = await SeedAndSignInAsAdminAsync(site);
        await site.PostFormAsync("/admin/users/masquerade", EndpointSite.Fields(("userId", member.Id)));

        var response = await site.PostFormAsync("/account/password-change", EndpointSite.Fields(
            ("currentPassword", Pw), ("password", NewPw), ("confirmPassword", NewPw)));

        Assert.Equal("/account?passwordError=masquerade", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task ChangePassword_Success_EndsTheSessionItWasMadeFrom()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Member, Pw);
        await site.SignInAsync(Member, Pw);
        var oldCookie = site.CookieValue(EndpointSite.SignInCookie)!;

        var response = await site.PostFormAsync("/account/password-change", EndpointSite.Fields(
            ("currentPassword", Pw), ("password", NewPw), ("confirmPassword", NewPw)));

        Assert.Equal("/account?passwordDone=1", EndpointSite.LocationOf(response));
        Assert.StartsWith("id=", await site.WhoAsync());
        var replay = await site.SendWithCookieAsync(HttpMethod.Get, "/probe/who", EndpointSite.SignInCookie, oldCookie);
        Assert.Equal("anonymous", await replay.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ChangePassword_AccountWithoutAPassword_NeedsNoCurrentOne_AsRecorded()
    {
        // An account made by emailed code has no password; the first one is set without asking for a current one (the signed-in browser proved the address).
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Member, null);
        await site.PostFormAsync("/account/code-send", EndpointSite.Fields(("email", Member)));
        await site.PostFormAsync("/account/code-verify", EndpointSite.Fields(("email", Member), ("code", site.Mail.CodeFor(Member)!)));

        var response = await site.PostFormAsync("/account/password-change", EndpointSite.Fields(("password", NewPw), ("confirmPassword", NewPw)));

        Assert.Equal("/account?passwordDone=1", EndpointSite.LocationOf(response));
    }
}
