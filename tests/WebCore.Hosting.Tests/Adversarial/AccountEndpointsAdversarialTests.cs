using System.Net;
using static WebCore.Hosting.Tests.Adversarial.EndpointSite;
using System.Net.Http.Headers;
using System.Text;
using WebCore.Accounts;
using WebCore.Hosting.Accounts;

namespace WebCore.Hosting.Tests.Adversarial;

// The account form endpoints (logout, register, password sign-in, emailed sign-in codes, confirming an email) under missing and forged antiforgery
// tokens, wrong methods and content types, oversized and control-character fields, duplicated fields, hostile return URLs, and signed-out callers.
public sealed class AccountEndpointsAdversarialTests
{
    private const string Sam = "sam@example.com";
    private const string Pw = EndpointSite.Password;

    private static readonly string[] SignInPaths =
    [
        "/account/login-submit", "/account/register-submit", "/account/code-send", "/account/code-verify", "/account/reset-send", "/account/reset-submit",
    ];

    private static Task<EndpointSite> StartAsync() => EndpointSite.StartAccountsAsync(EndpointSite.NewFolder());

    // ---- Logout and the antiforgery gate ----

    [Fact]
    public async Task Logout_SignedOut_RedirectsHome()
    {
        await using var site = await StartAsync();

        var response = await site.PostFormAsync("/account/logout", []);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task Logout_GetIsNotAllowed()
    {
        await using var site = await StartAsync();

        var response = await site.GetAsync("/account/logout");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Logout_WithoutAToken_IsBadRequest()
    {
        await using var site = await StartAsync();

        var response = await site.PostFormAsync("/account/logout", [], withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Logout_ForgedToken_IsBadRequest()
    {
        await using var site = await StartAsync();
        await site.TokenAsync();

        var response = await site.PostFormAsync("/account/logout", EndpointSite.Fields(("__RequestVerificationToken", "forged")), withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Logout_TokenFromAnotherBrowser_IsBadRequest()
    {
        // A token is only good in the browser whose antiforgery cookie it was issued with (cross-site form posts cannot read it).
        await using var site = await StartAsync();
        var token = await site.TokenAsync();

        var response = await site.PostFormWithoutCookiesAsync("/account/logout", EndpointSite.Fields(("__RequestVerificationToken", token)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(FormPaths))]
    public async Task EveryFormEndpoint_JsonBody_IsRefused(string path)
    {
        await using var site = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("{\"email\":\"" + Sam + "\"}", Encoding.UTF8, "application/json"),
        };

        var response = await site.SendAnonymousAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    public static TheoryData<string> FormPaths()
    {
        var data = new TheoryData<string>();
        foreach (var path in SignInPaths)
        {
            data.Add(path);
        }

        return data;
    }

    [Fact]
    public async Task StaleAnonymousTokenOnLoginSubmit_AfterSignIn_GoesHome_AsRecorded()
    {
        // The token was issued to an anonymous visitor; once signed in the same form is refused, and the visitor goes home (no error page).
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Sam, Pw);
        var anonymousToken = await site.TokenAsync();
        await site.SignInAsync(Sam, Pw);

        var stale = await site.PostFormAsync("/account/login-submit", EndpointSite.Fields(
            ("__RequestVerificationToken", anonymousToken), ("email", Sam), ("password", Pw)), withToken: false);

        Assert.Equal("/", EndpointSite.LocationOf(stale));
    }

    // ---- Password sign-in ----

    [Fact]
    public async Task Login_CorrectPassword_SignsInAndRedirectsToTheReturnUrl()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Sam, Pw, role: "Admin");

        var response = await site.SignInAsync(Sam, Pw, "/quiz/7");

        Assert.Equal("/quiz/7", EndpointSite.LocationOf(response));
        Assert.StartsWith("id=", await site.WhoAsync());
        Assert.Contains("role=Admin", await site.WhoAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("a@b")]
    public async Task Login_BlankOrBrokenEmail_AnswersTheSameRefusal(string email)
    {
        await using var site = await StartAsync();

        var response = await site.SignInAsync(email, Pw);

        Assert.StartsWith("/account/login?error=1&", EndpointSite.LocationOf(response));
        Assert.Equal("anonymous", await site.WhoAsync());
    }

    [Fact]
    public async Task Login_OversizedEmail_AnswersTheSameRefusal()
    {
        await using var site = await StartAsync();

        var response = await site.SignInAsync(Big(64 * 1024), Pw);

        Assert.StartsWith("/account/login?error=1&", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task Login_UnknownAccountWrongPasswordAndNoPassword_AnswerTheSame()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync("wrong@example.com", Pw);
        await site.CreateAccountAsync("codeonly@example.com", null);

        var unknown = await site.SignInAsync("nobody@example.com", Pw);
        var wrong = await site.SignInAsync("wrong@example.com", "Wrong horse 1");
        var noPassword = await site.SignInAsync("codeonly@example.com", Pw);

        foreach (var response in new[] { unknown, wrong, noPassword })
        {
            Assert.StartsWith("/account/login?error=1&", EndpointSite.LocationOf(response));
        }
    }

    [Fact]
    public async Task Login_DisabledAccountWithTheRightPassword_SaysDisabled_AsRecorded()
    {
        // Only reachable with the right password, so naming the reason tells the owner of the password nothing new.
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Sam, Pw, disabled: true);

        var response = await site.SignInAsync(Sam, Pw);

        Assert.Contains("error=disabled", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task Login_DuplicatedEmailField_IsRefused()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Sam, Pw);

        var response = await site.PostFormAsync("/account/login-submit", EndpointSite.Fields(("email", Sam), ("email", "other@example.com"), ("password", Pw)));

        Assert.StartsWith("/account/login?error=1&", EndpointSite.LocationOf(response));
        Assert.Equal("anonymous", await site.WhoAsync());
    }

    [Fact]
    public async Task Login_EmailCarryingAReturnUrlParameter_StaysInsideItsOwnValue()
    {
        await using var site = await StartAsync();

        var response = await site.SignInAsync(Sam + "&returnUrl=https://evil.example", Pw);

        var location = EndpointSite.LocationOf(response);
        Assert.DoesNotContain("&returnUrl=https", location);
        Assert.Contains("returnUrl=%2F", location);
    }

    [Fact]
    public async Task Login_OversizedPassword_IsRefusedCleanly()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Sam, Pw);

        var response = await site.SignInAsync(Sam, Big(64 * 1024));

        Assert.StartsWith("/account/login?error=1&", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task Login_EleventhWrongPasswordForOneAccount_IsRateLimited()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Sam, Pw);

        for (var i = 0; i < 10; i++)
        {
            Assert.Contains("error=1", EndpointSite.LocationOf(await site.SignInAsync(Sam, "Wrong horse " + i)));
        }

        Assert.Contains("error=ratelimited", EndpointSite.LocationOf(await site.SignInAsync(Sam, Pw)));
    }

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("\\\\evil.example")]
    [InlineData("/\r\nSet-Cookie: x=1")]
    public async Task Login_HostileReturnUrl_IsReplacedWithHome(string returnUrl)
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Sam, Pw);

        var response = await site.SignInAsync(Sam, Pw, returnUrl);

        Assert.Equal("302 /", $"{(int)response.StatusCode} {EndpointSite.LocationOf(response)}");
    }

    [Fact]
    public async Task Login_NulInAFormValue_IsRefusedWith400_AsRecorded()
    {
        // A NUL byte in a form value is not sent home to "/": the post is refused outright (400, no redirect). Safe, and nothing signs in.
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Sam, Pw);

        var response = await site.SignInAsync(Sam, Pw, "/quiz\u0000");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("anonymous", await site.WhoAsync());
    }

    [Fact]
    public async Task Login_SchemeInReturnUrl_StaysOnThisSiteAsAPath_AsRecorded()
    {
        // The rule's comment says a scheme collapses to "/", but the code only adds a leading slash: the result is a path on this host, not another one.
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Sam, Pw);

        var response = await site.SignInAsync(Sam, Pw, "https://evil.example");

        Assert.Equal("/https://evil.example", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task Login_ReturnUrlOverTheCap_IsReplacedWithHome()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Sam, Pw);

        var response = await site.SignInAsync(Sam, Pw, "/" + Big(2048));

        Assert.Equal("/", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task Login_ReturnUrlAtTheCap_IsKept()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Sam, Pw);
        var atCap = "/" + Big(2047);

        var response = await site.SignInAsync(Sam, Pw, atCap);

        Assert.Equal(atCap, EndpointSite.LocationOf(response));
    }

    // ---- Registration ----

    [Fact]
    public async Task Register_ValidForm_CreatesAnAccountAndSignsIn()
    {
        await using var site = await StartAsync();

        var response = await site.PostFormAsync("/account/register-submit", EndpointSite.Fields(
            ("email", Sam), ("displayName", "Sam"), ("password", Pw), ("confirmPassword", Pw), ("returnUrl", "/quiz")));

        Assert.Equal("/quiz", EndpointSite.LocationOf(response));
        Assert.Contains("role=Member", await site.WhoAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("a@b")]
    [InlineData("sam@example.com\r\nBcc: x@evil.example")]
    public async Task Register_BadEmail_IsRefusedWithoutAnAccount(string email)
    {
        await using var site = await StartAsync();

        var response = await site.PostFormAsync("/account/register-submit", EndpointSite.Fields(
            ("email", email), ("displayName", "Sam"), ("password", Pw), ("confirmPassword", Pw)));

        Assert.StartsWith("/account/register?error=Please", EndpointSite.LocationOf(response));
        Assert.Equal("anonymous", await site.WhoAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public async Task Register_BlankDisplayName_IsRefused(string displayName)
    {
        await using var site = await StartAsync();

        var response = await site.PostFormAsync("/account/register-submit", EndpointSite.Fields(
            ("email", Sam), ("displayName", displayName), ("password", Pw), ("confirmPassword", Pw)));

        Assert.StartsWith("/account/register?error=Please%20enter%20a%20display%20name", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task Register_DisplayNameOverTheLimit_IsRefused()
    {
        await using var site = await StartAsync();

        var response = await site.PostFormAsync("/account/register-submit", EndpointSite.Fields(
            ("email", Sam), ("displayName", Big(DisplayNames.MaxLength + 1)), ("password", Pw), ("confirmPassword", Pw)));

        Assert.StartsWith("/account/register?error=Please%20enter%20a%20display%20name", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task Register_OversizedDisplayName_IsRefusedWithoutAnAccount()
    {
        await using var site = await StartAsync();

        var response = await site.PostFormAsync("/account/register-submit", EndpointSite.Fields(
            ("email", Sam), ("displayName", Big(64 * 1024)), ("password", Pw), ("confirmPassword", Pw)));

        Assert.Contains("error=Please", EndpointSite.LocationOf(response));
        Assert.Equal("anonymous", await site.WhoAsync());
    }

    [Fact(Skip = "FINDING F-W6 (Low): a display name with a control character is accepted and the account is created (desired: refused).")]
    public async Task Register_DisplayNameWithAControlCharacter_IsRefused()
    {
        // A name is shown on the account page and written into audit entries; a control character in it has no business there.
        await using var site = await StartAsync();

        var response = await site.PostFormAsync("/account/register-submit", EndpointSite.Fields(
            ("email", Sam), ("displayName", "Sam\u0001Eve"), ("password", Pw), ("confirmPassword", Pw)));

        Assert.Contains("error=", EndpointSite.LocationOf(response));
        Assert.Equal("anonymous", await site.WhoAsync());
    }

    [Theory]
    [InlineData("short", "short")]
    [InlineData("correct horse 9", "correct horse 8")]
    public async Task Register_PasswordTooShortOrMismatched_IsRefused(string password, string confirmation)
    {
        await using var site = await StartAsync();

        var response = await site.PostFormAsync("/account/register-submit", EndpointSite.Fields(
            ("email", Sam), ("displayName", "Sam"), ("password", password), ("confirmPassword", confirmation)));

        Assert.StartsWith("/account/register?error=", EndpointSite.LocationOf(response));
        Assert.Equal("anonymous", await site.WhoAsync());
    }

    [Fact]
    public async Task Register_PasswordOverTheLimit_IsRefusedWithoutAnAccount()
    {
        await using var site = await StartAsync();
        var long129 = Big(PasswordRules.MaximumLength + 1);

        var response = await site.PostFormAsync("/account/register-submit", EndpointSite.Fields(
            ("email", Sam), ("displayName", "Sam"), ("password", long129), ("confirmPassword", long129)));

        Assert.Contains("error=Password%20can%20be%20at%20most", EndpointSite.LocationOf(response));
        Assert.Equal("anonymous", await site.WhoAsync());
    }

    [Fact]
    public async Task Register_ExistingEmail_SaysSoAndRevealsTheAccount_AsRecorded()
    {
        // Registration has to say an address is taken for the person to act on it; the password forms do not, and this is the one place that does.
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Sam, Pw);

        var response = await site.PostFormAsync("/account/register-submit", EndpointSite.Fields(
            ("email", Sam), ("displayName", "Sam"), ("password", Pw), ("confirmPassword", Pw)));

        Assert.Contains("already", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task Register_OversizedEmailIsEchoedIntoTheRedirect_AsRecorded()
    {
        // The address typed is kept in the redirect (unless the app keeps addresses out of URLs); nothing caps its length.
        await using var site = await StartAsync();

        var response = await site.PostFormAsync("/account/register-submit", EndpointSite.Fields(
            ("email", Big(64 * 1024) + "@example.com"), ("displayName", "Sam"), ("password", Pw), ("confirmPassword", Pw)));

        Assert.True(EndpointSite.LocationOf(response).Length > 64 * 1024);
    }

    [Fact]
    public async Task Register_ReturnUrlWithAControlCharacter_IsReplacedWithHome()
    {
        await using var site = await StartAsync();

        var response = await site.PostFormAsync("/account/register-submit", EndpointSite.Fields(
            ("email", Sam), ("displayName", "Sam"), ("password", Pw), ("confirmPassword", Pw), ("returnUrl", "/quiz\r\nX-Injected: 1")));

        Assert.Equal("/", EndpointSite.LocationOf(response));
    }

    // ---- Emailed sign-in codes ----

    [Fact]
    public async Task SignInCode_UnknownAndKnownAddress_GetTheSameAnswer()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Sam, Pw);

        var known = await site.PostFormAsync("/account/code-send", EndpointSite.Fields(("email", Sam)));
        var unknown = await site.PostFormAsync("/account/code-send", EndpointSite.Fields(("email", "nobody@example.com")));

        Assert.StartsWith("/account/login?step=code&", EndpointSite.LocationOf(known));
        Assert.StartsWith("/account/login?step=code&", EndpointSite.LocationOf(unknown));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public async Task SignInCode_InvalidEmail_IsReportedAndNothingIsSent(string email)
    {
        await using var site = await StartAsync();

        var response = await site.PostFormAsync("/account/code-send", EndpointSite.Fields(("email", email)));

        Assert.Contains("codeError=invalidemail", EndpointSite.LocationOf(response));
        Assert.Empty(site.Mail.Sent);
    }

    [Fact]
    public async Task SignInCode_OversizedEmail_IsReportedAndNothingIsSent()
    {
        await using var site = await StartAsync();

        var response = await site.PostFormAsync("/account/code-send", EndpointSite.Fields(("email", Big(64 * 1024))));

        Assert.Contains("codeError=invalidemail", EndpointSite.LocationOf(response));
        Assert.Empty(site.Mail.Sent);
    }

    [Fact]
    public async Task SignInCode_TheEmailedCodeSignsIn_AndRedirectsToTheReturnUrl()
    {
        await using var site = await StartAsync();
        await site.PostFormAsync("/account/code-send", EndpointSite.Fields(("email", Sam), ("returnUrl", "/quiz")));
        var code = site.Mail.CodeFor(Sam);

        var response = await site.PostFormAsync("/account/code-verify", EndpointSite.Fields(("email", Sam), ("code", code!), ("returnUrl", "/quiz")));

        Assert.Equal("/quiz", EndpointSite.LocationOf(response));
        Assert.StartsWith("id=", await site.WhoAsync());
    }

    [Fact]
    public async Task SignInCode_VerifyWithoutTheBrowserCookie_IsExpired()
    {
        // A code is bound to the browser that asked for it: the same code from another browser does nothing.
        await using var site = await StartAsync();
        await site.PostFormAsync("/account/code-send", EndpointSite.Fields(("email", Sam)));
        var code = site.Mail.CodeFor(Sam);
        site.ForgetCookie(EndpointSite.CodeClientCookie);

        var response = await site.PostFormAsync("/account/code-verify", EndpointSite.Fields(("email", Sam), ("code", code!)));

        Assert.Contains("codeError=expired", EndpointSite.LocationOf(response));
        Assert.Equal("anonymous", await site.WhoAsync());
    }

    [Fact]
    public async Task SignInCode_SixthTryWithTheRightCode_FailsAfterFiveWrongGuesses()
    {
        await using var site = await StartAsync();
        await site.PostFormAsync("/account/code-send", EndpointSite.Fields(("email", Sam)));
        var code = site.Mail.CodeFor(Sam)!;
        var wrong = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < 5; i++)
        {
            Assert.Contains("codeError=wrongcode", EndpointSite.LocationOf(await site.PostFormAsync("/account/code-verify", EndpointSite.Fields(("email", Sam), ("code", wrong)))));
        }

        var last = await site.PostFormAsync("/account/code-verify", EndpointSite.Fields(("email", Sam), ("code", code)));

        Assert.Contains("codeError=", EndpointSite.LocationOf(last));
        Assert.Equal("anonymous", await site.WhoAsync());
    }

    [Fact]
    public async Task SignInCode_AdminWithAPassword_IsToldToUseIt()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Sam, Pw, role: "Admin");
        await site.PostFormAsync("/account/code-send", EndpointSite.Fields(("email", Sam)));
        var code = site.Mail.CodeFor(Sam);

        var response = await site.PostFormAsync("/account/code-verify", EndpointSite.Fields(("email", Sam), ("code", code!)));

        Assert.Contains("error=usepassword", EndpointSite.LocationOf(response));
        Assert.Equal("anonymous", await site.WhoAsync());
    }

    [Fact]
    public async Task SignInCode_DisabledAccount_IsSaidDisabledOnlyAfterTheCode()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Sam, null, disabled: true);
        await site.PostFormAsync("/account/code-send", EndpointSite.Fields(("email", Sam)));
        var code = site.Mail.CodeFor(Sam);

        var response = await site.PostFormAsync("/account/code-verify", EndpointSite.Fields(("email", Sam), ("code", code!)));

        Assert.Contains("error=disabled", EndpointSite.LocationOf(response));
        Assert.Equal("anonymous", await site.WhoAsync());
    }

    [Fact]
    public async Task SignInCode_EleventhRequestFromOneAddress_IsRateLimited()
    {
        await using var site = await StartAsync();

        for (var i = 0; i < 10; i++)
        {
            Assert.Contains("step=code", EndpointSite.LocationOf(await site.PostFormAsync("/account/code-send", EndpointSite.Fields(("email", $"u{i}@example.com")))));
        }

        var eleventh = await site.PostFormAsync("/account/code-send", EndpointSite.Fields(("email", "u10@example.com")));

        Assert.Contains("codeError=ratelimited", EndpointSite.LocationOf(eleventh));
    }

    // ---- Changing the email of a signed-in account ----

    [Theory]
    [InlineData("/account/email-send")]
    [InlineData("/account/email-verify")]
    [InlineData("/account/masquerade-stop")]
    [InlineData("/account/password-change")]
    public async Task SignedOutCallers_OfSignedInOnlyEndpoints_AreSentToLogin(string path)
    {
        await using var site = await StartAsync();

        var response = await site.PostFormAsync(path, EndpointSite.Fields(("email", Sam)));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        // The cookie handler's challenge is an absolute URL on this host; the path is what matters.
        Assert.Contains("/account/login?ReturnUrl", EndpointSite.LocationOf(response));
    }

    [Fact]
    public async Task EmailChange_InvalidAddress_IsReported()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Sam, Pw);
        await site.SignInAsync(Sam, Pw);

        var response = await site.PostFormAsync("/account/email-send", EndpointSite.Fields(("email", "bad")));

        Assert.StartsWith("/account?emailError=invalidemail", EndpointSite.LocationOf(response));
        Assert.Empty(site.Mail.Sent.Where(m => m.To == "bad"));
    }

    [Fact]
    public async Task EmailChange_ToAnAddressInUse_IsReportedOnlyAtConfirmation()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Sam, Pw);
        await site.CreateAccountAsync("taken@example.com", null);
        await site.SignInAsync(Sam, Pw);

        var request = await site.PostFormAsync("/account/email-send", EndpointSite.Fields(("email", "taken@example.com")));
        Assert.Contains("step=code", EndpointSite.LocationOf(request));
        var code = site.Mail.CodeFor("taken@example.com");
        var confirm = await site.PostFormAsync("/account/email-verify", EndpointSite.Fields(("email", "taken@example.com"), ("code", code!)));

        Assert.Contains("emailError=emailinuse", EndpointSite.LocationOf(confirm));
    }

    [Fact]
    public async Task EmailChange_CorrectCode_ConfirmsAndReissuesTheCookie()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Sam, Pw);
        await site.SignInAsync(Sam, Pw);
        var before = site.CookieValue(EndpointSite.SignInCookie);
        await site.PostFormAsync("/account/email-send", EndpointSite.Fields(("email", "new@example.com")));
        var code = site.Mail.CodeFor("new@example.com");

        var confirm = await site.PostFormAsync("/account/email-verify", EndpointSite.Fields(("email", "new@example.com"), ("code", code!)));

        Assert.Equal("/account?confirmed=1", EndpointSite.LocationOf(confirm));
        Assert.NotEqual(before, site.CookieValue(EndpointSite.SignInCookie));
    }

    [Fact]
    public async Task EmailChange_WrongCode_KeepsTheCodeBoxOpen()
    {
        await using var site = await StartAsync();
        await site.CreateAccountAsync(Sam, Pw);
        await site.SignInAsync(Sam, Pw);
        await site.PostFormAsync("/account/email-send", EndpointSite.Fields(("email", "new@example.com")));

        var response = await site.PostFormAsync("/account/email-verify", EndpointSite.Fields(("email", "new@example.com"), ("code", "000000")));

        Assert.Contains("emailError=wrongcode&step=code", EndpointSite.LocationOf(response));
    }
}
