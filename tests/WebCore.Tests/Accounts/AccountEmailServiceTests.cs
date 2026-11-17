/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.Storage;
using System.Text.RegularExpressions;
using WebCore.Accounts;
using WebCore.Email;


namespace WebCore.Tests.Accounts;

public class AccountEmailServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "CrateDiggerAccountEmailTests_" + Guid.NewGuid().ToString("N"));
    private static readonly AccountClaims Claims = new("cratedigger:");
    private readonly UserRepository _users;
    private readonly UserProfileRepository _profiles;
    private readonly CapturingSender _sender = new();
    private DateTimeOffset _now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    public AccountEmailServiceTests()
    {
        _users = new UserRepository(new FileDocumentStore(Path.Combine(_folder, "users")));
        _profiles = new UserProfileRepository(new FileDocumentStore(Path.Combine(_folder, "profiles")), TestRoles.MemberAdmin);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private AccountEmailService Service(bool delivery = true, bool exposeCode = false, int maxPerClient = 10, int maxPerHour = 60, int maxPerDay = 200) =>
        new(_users, _profiles, _sender, TestRoles.MemberAdmin, new AccountEmailOptions("CrateDigger", delivery, exposeCode, maxPerClient, maxPerHour, maxPerDay), () => _now);

    private async Task<AccountCodeResult> SignInAsync(AccountEmailService service, string email, string client, string? name = null)
    {
        var request = await service.RequestSignInCodeAsync(email, client);
        Assert.Equal(EmailRequestStatus.Sent, request.Status);
        return await service.ConfirmSignInAsync(email, client, _sender.LastCode(), name);
    }

    [Fact]
    public async Task FirstConfirmation_CreatesAConfirmedAccountWithNoPassword()
    {
        var result = await SignInAsync(Service(), "  Sam@Example.com ", "browser-1", "Quiz Khalifa");

        Assert.Equal(CodeCheckStatus.Confirmed, result.Status);
        Assert.True(result.IsNewAccount);
        Assert.Equal("sam@example.com", result.User!.Email);
        Assert.Equal("Quiz Khalifa", result.User.DisplayName);
        Assert.NotNull(result.User.EmailVerifiedAt);
        Assert.Equal(string.Empty, result.User.PasswordHash);
        Assert.Equal("sam@example.com", _sender.Sent[0].To);
        Assert.Single(await _users.ListAsync());
        Assert.False(PasswordHasher.Verify("", result.User.PasswordHash));
    }

    [Fact]
    public async Task ABlankName_DefaultsToTheEmailsFirstPart()
    {
        var result = await SignInAsync(Service(), "sam@example.com", "browser-1");

        Assert.Equal("sam", result.User!.DisplayName);
    }

    [Fact]
    public async Task ASecondBrowser_ConfirmingTheSameEmail_SignsIntoTheSameAccount()
    {
        var service = Service();
        var first = await SignInAsync(service, "sam@example.com", "browser-1", "Quiz Khalifa");

        var second = await SignInAsync(service, "SAM@example.com", "browser-2");

        Assert.False(second.IsNewAccount);
        Assert.Equal(first.User!.Id, second.User!.Id);
        Assert.Equal("Quiz Khalifa", second.User.DisplayName);
        Assert.Single(await _users.ListAsync());
    }

    [Fact]
    public async Task AnExistingPasswordAccount_IsMergedByEmail_AndBecomesConfirmed()
    {
        var existing = await _users.CreateAsync(new User { Email = "sam@example.com", DisplayName = "Sam", PasswordHash = PasswordHasher.Hash("correct horse") });
        Assert.Null(existing.EmailVerifiedAt);

        var result = await SignInAsync(Service(), "sam@example.com", "browser-1");

        Assert.Equal(existing.Id, result.User!.Id);
        Assert.False(result.IsNewAccount);
        Assert.NotNull((await _users.GetByIdAsync(existing.Id))!.EmailVerifiedAt);
        Assert.True(PasswordHasher.Verify("correct horse", (await _users.GetByIdAsync(existing.Id))!.PasswordHash));
    }

    [Fact]
    public async Task AnAdminWithAPassword_MustUseThePasswordNotACode()
    {
        var admin = await _users.CreateAsync(new User { Email = "host@example.com", DisplayName = "Host", PasswordHash = PasswordHasher.Hash("correct horse") });
        await _profiles.EnsureProfileAsync(admin.Id, "Host");
        await _profiles.SetRoleAsync(admin.Id, "Admin");

        var result = await SignInAsync(Service(), "host@example.com", "browser-1");

        Assert.Equal(CodeCheckStatus.UsePassword, result.Status);
        Assert.Null(result.User);
        Assert.Null((await _users.GetByIdAsync(admin.Id))!.EmailVerifiedAt);
    }

    [Fact]
    public async Task AWrongCodeDoesNotSignIn_AndFiveWrongGuessesBurnTheCode()
    {
        var service = Service();
        await service.RequestSignInCodeAsync("sam@example.com", "browser-1");
        var real = _sender.LastCode();
        var wrong = real == "000000" ? "111111" : "000000";

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(CodeCheckStatus.WrongCode, (await service.ConfirmSignInAsync("sam@example.com", "browser-1", wrong)).Status);
        }

        Assert.Equal(CodeCheckStatus.TooManyAttempts, (await service.ConfirmSignInAsync("sam@example.com", "browser-1", real)).Status);
        Assert.Empty(await _users.ListAsync());
    }

    [Fact]
    public async Task ACode_IsBoundToTheBrowserThatAskedForIt()
    {
        var service = Service();
        await service.RequestSignInCodeAsync("sam@example.com", "browser-1");

        var stolen = await service.ConfirmSignInAsync("sam@example.com", "browser-2", _sender.LastCode());

        Assert.Equal(CodeCheckStatus.Expired, stolen.Status);
        Assert.Empty(await _users.ListAsync());
    }

    [Fact]
    public async Task Codes_ExpireAfterTenMinutes_AndWorkOnlyOnce()
    {
        var service = Service();
        await service.RequestSignInCodeAsync("sam@example.com", "browser-1");
        var code = _sender.LastCode();
        _now = _now.AddMinutes(11);

        Assert.Equal(CodeCheckStatus.Expired, (await service.ConfirmSignInAsync("sam@example.com", "browser-1", code)).Status);

        await service.RequestSignInCodeAsync("sam@example.com", "browser-1");
        var fresh = _sender.LastCode();
        Assert.Equal(CodeCheckStatus.Confirmed, (await service.ConfirmSignInAsync("sam@example.com", "browser-1", fresh)).Status);
        Assert.Equal(CodeCheckStatus.Expired, (await service.ConfirmSignInAsync("sam@example.com", "browser-1", fresh)).Status);
    }

    [Theory]
    [InlineData("not an email")]
    [InlineData("sam@nodot")]
    [InlineData("")]
    public async Task InvalidEmails_AreRefused_AndNothingIsSent(string email)
    {
        var request = await Service().RequestSignInCodeAsync(email, "browser-1");

        Assert.Equal(EmailRequestStatus.InvalidEmail, request.Status);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task ConfirmingAPasswordAccountsOwnEmail_MarksItConfirmed()
    {
        var user = await _users.CreateAsync(new User { Email = "sam@example.com", DisplayName = "Sam", PasswordHash = PasswordHasher.Hash("correct horse") });
        var service = Service();

        var request = await service.RequestEmailConfirmationAsync(user.Id, "sam@example.com");
        Assert.Equal(EmailRequestStatus.Sent, request.Status);
        var result = await service.ConfirmEmailAsync(user.Id, _sender.LastCode());

        Assert.Equal(CodeCheckStatus.Confirmed, result.Status);
        Assert.NotNull((await _users.GetByIdAsync(user.Id))!.EmailVerifiedAt);
    }

    [Fact]
    public async Task ChangingEmail_MovesTheAccountToTheNewAddress_AfterConfirmingIt()
    {
        var service = Service();
        var signedIn = await SignInAsync(service, "old@example.com", "browser-1", "Quiz Khalifa");

        var request = await service.RequestEmailConfirmationAsync(signedIn.User!.Id, "New@Example.com");
        Assert.Equal(EmailRequestStatus.Sent, request.Status);
        Assert.Equal("new@example.com", _sender.Sent[^1].To);
        var result = await service.ConfirmEmailAsync(signedIn.User.Id, _sender.LastCode());

        Assert.Equal(CodeCheckStatus.Confirmed, result.Status);
        Assert.Equal("new@example.com", (await _users.GetByIdAsync(signedIn.User.Id))!.Email);
        Assert.Null(await _users.GetByEmailAsync("old@example.com"));
        Assert.Equal(signedIn.User.Id, (await _users.GetByEmailAsync("new@example.com"))!.Id);
    }

    [Fact]
    public async Task ChangingEmail_ToOneAnotherAccountUses_IsRefusedAtConfirmation()
    {
        var service = Service();
        var a = await SignInAsync(service, "a@example.com", "browser-a", "A");
        await SignInAsync(service, "b@example.com", "browser-b", "B");

        var request = await service.RequestEmailConfirmationAsync(a.User!.Id, "b@example.com");
        Assert.Equal(EmailRequestStatus.Sent, request.Status);
        var result = await service.ConfirmEmailAsync(a.User.Id, _sender.LastCode());

        Assert.Equal(CodeCheckStatus.EmailInUse, result.Status);
        Assert.Equal("a@example.com", (await _users.GetByIdAsync(a.User.Id))!.Email);
    }

    [Fact]
    public async Task EmailConfirmation_NeedsAnExistingAccount()
    {
        var service = Service();
        _sender.Sent.Clear();

        Assert.Equal(EmailRequestStatus.NotSignedIn, (await service.RequestEmailConfirmationAsync("no-such-user", "x@example.com")).Status);
        Assert.Equal(CodeCheckStatus.NotSignedIn, (await service.ConfirmEmailAsync("no-such-user", "123456")).Status);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task Requests_AreRateLimitedPerAddress()
    {
        var service = Service();

        var statuses = new List<EmailRequestStatus>();
        for (var i = 0; i < 6; i++)
        {
            statuses.Add((await service.RequestSignInCodeAsync("sam@example.com", "browser-" + i)).Status);
        }

        Assert.Equal(5, statuses.Count(s => s == EmailRequestStatus.Sent));
        Assert.Equal(EmailRequestStatus.RateLimited, statuses[5]);
    }

    [Fact]
    public async Task OneBrowser_CannotAskForManyCodes_AcrossManyAddresses()
    {
        var service = Service(maxPerClient: 3);

        var statuses = new List<EmailRequestStatus>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await service.RequestSignInCodeAsync($"victim{i}@example.com", "browser-1")).Status);
        }

        Assert.Equal(3, statuses.Count(s => s == EmailRequestStatus.Sent));
        Assert.Equal(2, statuses.Count(s => s == EmailRequestStatus.RateLimited));
    }

    [Fact]
    public async Task AllCodeEmailsTogether_StayUnderTheHourlyAndDailyCeilings()
    {
        var hourly = Service(maxPerHour: 3);
        var statuses = new List<EmailRequestStatus>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await hourly.RequestSignInCodeAsync($"p{i}@example.com", "browser-" + i)).Status);
        }

        Assert.Equal(3, statuses.Count(s => s == EmailRequestStatus.Sent));
        _now = _now.AddHours(2);
        Assert.Equal(EmailRequestStatus.Sent, (await hourly.RequestSignInCodeAsync("later@example.com", "browser-later")).Status);

        var daily = Service(maxPerDay: 2);
        var dailyStatuses = new List<EmailRequestStatus>();
        for (var i = 0; i < 4; i++)
        {
            dailyStatuses.Add((await daily.RequestSignInCodeAsync($"d{i}@example.com", "dbrowser-" + i)).Status);
            _now = _now.AddHours(2);
        }

        Assert.Equal(new[] { EmailRequestStatus.Sent, EmailRequestStatus.Sent, EmailRequestStatus.RateLimited, EmailRequestStatus.RateLimited }, dailyStatuses);
    }

    [Fact]
    public async Task AFailedSend_ReturnsSendFailed_DiscardsTheCode_AndReportsTheError()
    {
        Exception? reported = null;
        var service = new AccountEmailService(_users, _profiles, new ThrowingSender(), TestRoles.MemberAdmin, new AccountEmailOptions("CrateDigger", true), () => _now, ex => reported = ex);

        var request = await service.RequestSignInCodeAsync("sam@example.com", "browser-1");

        Assert.Equal(EmailRequestStatus.SendFailed, request.Status);
        Assert.Contains("limit exceeded", reported!.Message);
        Assert.Equal(CodeCheckStatus.Expired, (await service.ConfirmSignInAsync("sam@example.com", "browser-1", "123456")).Status);
    }

    [Fact]
    public async Task WithNoDelivery_ProductionRefuses_ButDevelopmentShowsTheCode()
    {
        var production = await Service(delivery: false).RequestSignInCodeAsync("sam@example.com", "browser-1");
        Assert.Equal(EmailRequestStatus.Unavailable, production.Status);

        var dev = Service(delivery: false, exposeCode: true);
        var shown = await dev.RequestSignInCodeAsync("sam@example.com", "browser-1");
        Assert.Equal(EmailRequestStatus.Sent, shown.Status);
        Assert.Matches(@"^\d{6}$", shown.DevCode);
        Assert.Equal(CodeCheckStatus.Confirmed, (await dev.ConfirmSignInAsync("sam@example.com", "browser-1", shown.DevCode)).Status);
    }

    [Fact]
    public async Task ConcurrentConfirmations_OfOneEmail_MakeExactlyOneAccount()
    {
        var service = Service(maxPerClient: 100, maxPerHour: 1000, maxPerDay: 1000);
        for (var i = 0; i < 5; i++)
        {
            await service.RequestSignInCodeAsync("race@example.com", "browser-" + i);
        }

        var codes = _sender.Sent.Select(m => Regex.Match(m.Body, @"<strong>(\d{6})</strong>").Groups[1].Value).ToList();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => service.ConfirmSignInAsync("race@example.com", "browser-" + i, codes[i], "Race")));

        Assert.All(results, r => Assert.Equal(CodeCheckStatus.Confirmed, r.Status));
        Assert.Single(await _users.ListAsync());
        Assert.Equal(1, results.Count(r => r.IsNewAccount));
    }

    [Fact]
    public void MaskEmail_ShowsTheFirstLettersAndTheDomain_SoSameNamedTeamsCanBeToldApart()
    {
        Assert.Equal("sa***@example.com", AccountEmailService.MaskEmail("sam@example.com"));
        Assert.Equal("su***@example.com", AccountEmailService.MaskEmail("sue@example.com"));
        Assert.Equal("a***@example.com", AccountEmailService.MaskEmail("ab@example.com"));
        Assert.Equal("***", AccountEmailService.MaskEmail("not-an-email"));
    }

    [Fact]
    public void DisplayNames_TidyAndValidate()
    {
        Assert.Equal("A B", DisplayNames.Tidy("  A    B "));
        Assert.True(DisplayNames.IsValid("Ok"));
        Assert.False(DisplayNames.IsValid(null));
        Assert.False(DisplayNames.IsValid(new string('x', DisplayNames.MaxLength + 1)));
        Assert.Equal(DisplayNames.Normalize("Quiz  KHALIFA"), DisplayNames.Normalize(" quiz khalifa"));
    }

    [Fact]
    public void AccountClaims_MarkAConfirmedEmail_OnlyWhenItIsConfirmed()
    {
        var unconfirmed = Claims.CreatePrincipal(new User { Email = "a@example.com", DisplayName = "A" }, "Member", "test");
        var confirmed = Claims.CreatePrincipal(new User { Email = "a@example.com", DisplayName = "A", EmailVerifiedAt = _now }, "Member", "test");

        Assert.False(unconfirmed.HasClaim(Claims.EmailConfirmed, "1"));
        Assert.True(confirmed.HasClaim(Claims.EmailConfirmed, "1"));
    }

    [Fact]
    public void AccountClaims_CarryTheRole_AndWithRoleReplacesIt()
    {
        var member = Claims.CreatePrincipal(new User { Email = "a@example.com", DisplayName = "A", EmailVerifiedAt = _now }, "Member", "test");
        Assert.True(member.IsInRole("Member"));
        Assert.False(member.IsInRole("Admin"));

        var admin = AccountClaims.WithRole(member, "Admin", "test");

        Assert.True(admin.IsInRole("Admin"));
        Assert.False(admin.IsInRole("Member"));
        Assert.Equal("Admin", AccountClaims.RoleOf(admin));
        Assert.True(admin.HasClaim(Claims.EmailConfirmed, "1"));
        Assert.Equal(member.Identity!.Name, admin.Identity!.Name);
    }
}
