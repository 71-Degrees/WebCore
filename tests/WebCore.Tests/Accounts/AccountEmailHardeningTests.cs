/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using WebCore.Accounts;
using WebCore.Email;
using WebCore.Storage;

namespace WebCore.Tests.Accounts;

/// <summary>The hardening carried over from Better Trivia's fork: atomic guesses, a bounded limiter table, the unknown-address reset limits, a role that
/// must use its password, clearing an unverified password, the confirmation hook and <see cref="AccountEmailService.PendingConfirmationEmail"/>.</summary>
public class AccountEmailHardeningTests : IDisposable
{
    private static readonly AccountRoles Organizers = new(["Member", "Organizer", "Admin"], "Member", "Admin");
    private static readonly AccountRoles OrganizersNeedPasswords = Organizers.WithPasswordRequired("Organizer");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WebCoreAccountHardening_" + Guid.NewGuid().ToString("N"));
    private readonly UserRepository _users;
    private readonly CapturingSender _sender = new();
    private DateTimeOffset _now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    public AccountEmailHardeningTests()
    {
        _users = new UserRepository(new FileDocumentStore(Path.Combine(_folder, "users")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private UserProfileRepository ProfilesFor(AccountRoles roles) => new(new FileDocumentStore(Path.Combine(_folder, "profiles")), roles);

    private AccountEmailService Service(AccountEmailOptions? options = null, AccountRoles? roles = null, IEmailSender? sender = null, Action<Exception>? onFailure = null)
    {
        var accountRoles = roles ?? TestRoles.MemberAdmin;
        return new AccountEmailService(_users, ProfilesFor(accountRoles), sender ?? _sender, accountRoles, options ?? new AccountEmailOptions("CrateDigger", true), () => _now, onFailure);
    }

    private static AccountEmailOptions Unlimited() => new("CrateDigger", true, MaxPerClientPerHour: 1_000_000, MaxPerHour: 1_000_000, MaxPerDay: 1_000_000);

    private static AccountEmailOptions WithHook(Action<User, EmailRequestStatus> hook) => new("CrateDigger", true) { OnConfirmationRequested = hook };

    private Task<User> UserAsync(string email, string? password = null, string name = "Sam") =>
        _users.CreateAsync(new User { Email = email, DisplayName = name, PasswordHash = password is null ? string.Empty : PasswordHasher.Hash(password) });

    private async Task<AccountCodeResult> SignInAsync(AccountEmailService service, string email, string client)
    {
        Assert.Equal(EmailRequestStatus.Sent, (await service.RequestSignInCodeAsync(email, client)).Status);
        return await service.ConfirmSignInAsync(email, client, _sender.LastCode());
    }

    [Fact]
    public async Task ParallelWrongGuesses_NeverGetMoreThanFiveTries()
    {
        var service = Service();
        await service.RequestSignInCodeAsync("sam@example.com", "browser-1");
        var wrong = _sender.LastCode() == "000000" ? "111111" : "000000";

        var results = await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => Task.Run(() => service.ConfirmSignInAsync("sam@example.com", "browser-1", wrong))));

        Assert.Equal(5, results.Count(r => r.Status == CodeCheckStatus.WrongCode));
    }

    [Fact]
    public async Task AFloodOfMadeUpBrowserKeys_DoesNotResetALiveAddressLimit()
    {
        var service = Service(Unlimited());
        for (var i = 0; i < 1500; i++)
        {
            await service.RequestSignInCodeAsync($"old{i}@example.com", $"old-browser-{i}");
        }

        _now = _now.AddMinutes(1);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(EmailRequestStatus.Sent, (await service.RequestSignInCodeAsync("victim@example.com", "victim-browser")).Status);
        }

        // The first batch is now older than the one-hour window, the victim is not: the flood pushes the table past its cap.
        _now = _now.AddMinutes(59.5);
        for (var i = 0; i < 2100; i++)
        {
            await service.RequestSignInCodeAsync($"new{i}@example.com", $"new-browser-{i}");
        }

        Assert.Equal(EmailRequestStatus.RateLimited, (await service.RequestSignInCodeAsync("victim@example.com", "victim-browser")).Status);
    }

    private static async Task<List<EmailRequestStatus>> ResetRequestsAsync(AccountEmailService service, string email, string browser, int count)
    {
        var statuses = new List<EmailRequestStatus>();
        for (var i = 0; i < count; i++)
        {
            statuses.Add((await service.RequestPasswordResetAsync(email, browser)).Status);
        }

        return statuses;
    }

    [Fact]
    public async Task AnUnknownAddressReset_IsLimitedPerAddress_LikeAKnownOne()
    {
        await UserAsync("known@example.com");
        var service = Service(Unlimited());

        var known = await ResetRequestsAsync(service, "known@example.com", "browser-1", 6);
        var unknown = await ResetRequestsAsync(service, "nobody@example.com", "browser-1", 6);

        var expected = new[] { EmailRequestStatus.Sent, EmailRequestStatus.Sent, EmailRequestStatus.Sent, EmailRequestStatus.Sent, EmailRequestStatus.Sent, EmailRequestStatus.RateLimited };
        Assert.Equal(expected, known);
        Assert.Equal(expected, unknown);
    }

    [Fact]
    public async Task AnUnknownAddressReset_CountsAgainstTheBrowsersLimit_AndSendsNothing()
    {
        var service = Service(new AccountEmailOptions("CrateDigger", true, MaxPerClientPerHour: 3));

        var statuses = new List<EmailRequestStatus>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await service.RequestPasswordResetAsync($"nobody{i}@example.com", "browser-1")).Status);
        }

        Assert.Equal(3, statuses.Count(s => s == EmailRequestStatus.Sent));
        Assert.Equal(2, statuses.Count(s => s == EmailRequestStatus.RateLimited));
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task AnUnknownAddressReset_WithNoDelivery_IsUnavailableInProduction_AndShownInDevelopment()
    {
        var production = await Service(new AccountEmailOptions("CrateDigger", false)).RequestPasswordResetAsync("nobody@example.com", "browser-1");
        Assert.Equal(EmailRequestStatus.Unavailable, production.Status);

        var development = await Service(new AccountEmailOptions("CrateDigger", false, ExposeCodeForDevelopment: true)).RequestPasswordResetAsync("nobody@example.com", "browser-1");
        Assert.Equal(EmailRequestStatus.Sent, development.Status);
    }

    [Fact]
    public void RequiresPassword_IsTheAdminRoleAndTheAddedOnes()
    {
        Assert.True(OrganizersNeedPasswords.RequiresPassword("Admin"));
        Assert.True(OrganizersNeedPasswords.RequiresPassword("Organizer"));
        Assert.False(OrganizersNeedPasswords.RequiresPassword("Member"));
        Assert.False(OrganizersNeedPasswords.RequiresPassword(null));

        // The list it was made from is unchanged, and the names and defaults carry over.
        Assert.False(Organizers.RequiresPassword("Organizer"));
        Assert.Equal(Organizers.Names, OrganizersNeedPasswords.Names);
        Assert.Equal("Member", OrganizersNeedPasswords.Default);
        Assert.Equal("Admin", OrganizersNeedPasswords.Admin);
    }

    [Fact]
    public void WithPasswordRequired_RefusesANameThatIsNotARole() =>
        Assert.Throws<ArgumentException>(() => TestRoles.MemberAdmin.WithPasswordRequired("Wizard"));

    [Fact]
    public async Task AnOrganizerWithAPassword_MustUseThePassword_AndAMemberMayUseACode()
    {
        var organizer = await UserAsync("host@example.com", "correct horse", "Host");
        await ProfilesFor(Organizers).EnsureProfileAsync(organizer.Id, "Host");
        await ProfilesFor(Organizers).SetRoleAsync(organizer.Id, "Organizer");
        var service = Service(roles: OrganizersNeedPasswords);

        var refused = await SignInAsync(service, "host@example.com", "browser-1");

        Assert.Equal(CodeCheckStatus.UsePassword, refused.Status);
        Assert.Null((await _users.GetByIdAsync(organizer.Id))!.EmailVerifiedAt);

        var member = await UserAsync("member@example.com", "correct horse", "Member");
        var admitted = await SignInAsync(service, "member@example.com", "browser-2");

        Assert.Equal(CodeCheckStatus.Confirmed, admitted.Status);
        Assert.Equal(member.Id, admitted.User!.Id);
    }

    [Fact]
    public async Task AnUnverifiedPassword_IsClearedWhenTheOwnerProvesTheEmail_WhenTheOptionIsOn()
    {
        var existing = await UserAsync("sam@example.com", "squatter pw");
        var service = Service(new AccountEmailOptions("CrateDigger", true) { ClearUnverifiedPassword = true });

        var result = await SignInAsync(service, "sam@example.com", "browser-1");

        var saved = (await _users.GetByIdAsync(existing.Id))!;
        Assert.Equal(CodeCheckStatus.Confirmed, result.Status);
        Assert.NotNull(saved.EmailVerifiedAt);
        Assert.Equal(string.Empty, saved.PasswordHash);
        Assert.False(string.IsNullOrEmpty(saved.SecurityStamp));
    }

    [Fact]
    public async Task AnUnverifiedPassword_IsKept_WhenTheOptionIsOff()
    {
        var existing = await UserAsync("sam@example.com", "correct horse");

        await SignInAsync(Service(), "sam@example.com", "browser-1");

        var saved = (await _users.GetByIdAsync(existing.Id))!;
        Assert.NotNull(saved.EmailVerifiedAt);
        Assert.True(PasswordHasher.Verify("correct horse", saved.PasswordHash));
        Assert.Null(saved.SecurityStamp);
    }

    [Fact]
    public async Task TheConfirmationHook_RunsWhenASendIsSent_AndItsChangesAreSaved()
    {
        var user = await UserAsync("sam@example.com");
        var seen = new List<EmailRequestStatus>();
        var service = Service(WithHook((u, status) =>
        {
            seen.Add(status);
            u.DisplayName = "Asked " + status;
        }));

        Assert.Equal(EmailRequestStatus.Sent, (await service.RequestEmailConfirmationAsync(user.Id, user.Email)).Status);

        Assert.Equal(new[] { EmailRequestStatus.Sent }, seen);
        Assert.Equal("Asked Sent", (await _users.GetByIdAsync(user.Id))!.DisplayName);
    }

    [Fact]
    public async Task TheConfirmationHook_RunsWhenASendFails()
    {
        var user = await UserAsync("sam@example.com");
        var seen = new List<EmailRequestStatus>();
        var service = Service(WithHook((_, status) => seen.Add(status)), sender: new ThrowingSender());

        Assert.Equal(EmailRequestStatus.SendFailed, (await service.RequestEmailConfirmationAsync(user.Id, user.Email)).Status);

        Assert.Equal(new[] { EmailRequestStatus.SendFailed }, seen);
    }

    [Fact]
    public async Task TheConfirmationHook_IsNotCalledWhenNothingWasSentOrAttempted()
    {
        var user = await UserAsync("sam@example.com");
        var seen = new List<EmailRequestStatus>();
        var service = Service(WithHook((_, status) => seen.Add(status)));

        // Five codes go out; the sixth is rate limited and must not be recorded.
        for (var i = 0; i < 6; i++)
        {
            await service.RequestEmailConfirmationAsync(user.Id, user.Email);
        }

        Assert.Equal(5, seen.Count);
        Assert.DoesNotContain(EmailRequestStatus.RateLimited, seen);

        var unavailable = Service(new AccountEmailOptions("CrateDigger", false) { OnConfirmationRequested = (_, status) => seen.Add(status) });
        Assert.Equal(EmailRequestStatus.Unavailable, (await unavailable.RequestEmailConfirmationAsync(user.Id, user.Email)).Status);
        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public async Task AThrowingConfirmationHook_DoesNotFailTheRequest_AndIsReported()
    {
        var user = await UserAsync("sam@example.com");
        Exception? reported = null;
        var service = Service(WithHook((_, _) => throw new InvalidOperationException("hook broke")), onFailure: ex => reported = ex);

        var result = await service.RequestEmailConfirmationAsync(user.Id, user.Email);

        Assert.Equal(EmailRequestStatus.Sent, result.Status);
        Assert.Equal("hook broke", reported?.Message);
    }

    [Fact]
    public async Task AWaitingConfirmationCode_CanBeFoundWithoutAskingForAnother_AndGoesAwayWhenUsedOrExpired()
    {
        var user = await UserAsync("sam@example.com", "correct horse");
        var service = Service();
        Assert.Null(service.PendingConfirmationEmail(user.Id));

        await service.RequestEmailConfirmationAsync(user.Id, "sam@example.com");   // what an admin's "Resend code" does

        Assert.Equal("sam@example.com", service.PendingConfirmationEmail(user.Id));
        Assert.Single(_sender.Sent);   // looking never sends or replaces the code
        Assert.Equal(CodeCheckStatus.Confirmed, (await service.ConfirmEmailAsync(user.Id, _sender.LastCode())).Status);
        Assert.Null(service.PendingConfirmationEmail(user.Id));

        await service.RequestEmailConfirmationAsync(user.Id, "sam@example.com");
        _now = _now.AddMinutes(11);
        Assert.Null(service.PendingConfirmationEmail(user.Id));
        Assert.Null(service.PendingConfirmationEmail(null));
    }
}
