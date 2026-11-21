/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using WebCore.Accounts;
using WebCore.Email;

namespace WebCore.Tests.Adversarial;

// Edge and hostile input for the email-code service: malformed addresses, blank client keys, code boundaries,
// guessing races, account enumeration through status codes, and limiter tables that can be emptied by flooding.
public sealed class AccountEmailAdversarialTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    // ---- NormalizeEmail / MaskEmail (static, pure) ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@b.co")]
    [InlineData("a@")]
    [InlineData("a@localhost")]
    [InlineData("Name <a@b.co>")]
    [InlineData("a@b.co,c@d.ef")]
    [InlineData("a b@c.de")]
    [InlineData("a@b.co\r\nBcc: x@y.zz")]
    public void NormalizeEmail_MalformedOrHostile_IsNull(string? raw)
    {
        Assert.Null(AccountEmailService.NormalizeEmail(raw));
    }

    [Theory]
    [InlineData("a@b.co", "a@b.co")]
    [InlineData("  A@B.CO  ", "a@b.co")]
    [InlineData("first.last+tag@sub.example.org", "first.last+tag@sub.example.org")]
    public void NormalizeEmail_WellFormed_IsTrimmedAndLowerCased(string raw, string expected)
    {
        Assert.Equal(expected, AccountEmailService.NormalizeEmail(raw));
    }

    [Fact]
    public void NormalizeEmail_TheLengthCeilingIs254()
    {
        var local = new string('a', 64);
        var atLimit = local + "@" + new string('b', 254 - 64 - 1 - 3) + ".co";
        var over = atLimit.Replace("@", "@x") + "x";

        Assert.Equal(254, atLimit.Length);
        Assert.NotNull(AccountEmailService.NormalizeEmail(atLimit));
        Assert.Null(AccountEmailService.NormalizeEmail(over));
    }

    [Theory]
    [InlineData("ab@x.com", "a***@x.com")]
    [InlineData("alice@x.com", "al***@x.com")]
    [InlineData("@x.com", "***")]
    [InlineData("", "***")]
    [InlineData("noat", "***")]
    public void MaskEmail_ShowsOneOrTwoLettersAndTheDomain(string email, string expected)
    {
        Assert.Equal(expected, AccountEmailService.MaskEmail(email));
    }

    [Fact]
    public void MaskEmail_OfNull_ThrowsNullReference()
    {
        Assert.Throws<NullReferenceException>(() => AccountEmailService.MaskEmail(null!));
    }

    // ---- Requests with bad or missing inputs ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Request_BlankOrNullClientKey_IsInvalidEmailAndNothingIsSent(string? clientKey)
    {
        var fixture = new Fixture(deliveryConfigured: true);

        var result = fixture.Service.RequestSignInCodeAsync("a@b.co", clientKey!).Result;

        Assert.Equal(EmailRequestStatus.InvalidEmail, result.Status);
        Assert.Empty(fixture.Sender.Sent);
    }

    [Fact]
    public void Request_WithNoDeliveryAndNoDevelopmentExposure_IsUnavailable()
    {
        var fixture = new Fixture(deliveryConfigured: false, exposeCode: false);

        var result = fixture.Service.RequestSignInCodeAsync("a@b.co", "client").Result;

        Assert.Equal(EmailRequestStatus.Unavailable, result.Status);
    }

    [Fact]
    public void Request_EmailIsCaseInsensitive_SoACodeAskedForInCapitalsIsConfirmedInLower()
    {
        var fixture = new Fixture(deliveryConfigured: false, exposeCode: true);
        var code = fixture.Service.RequestSignInCodeAsync("A@B.CO", "client").Result.DevCode!;

        var result = fixture.Service.ConfirmSignInAsync("a@b.co", "client", code).Result;

        Assert.Equal(CodeCheckStatus.Confirmed, result.Status);
    }

    // ---- Code checking edges ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12 345 6")]
    [InlineData("１２３４５６")]
    public void Confirm_MissingOrMalformedCode_IsWrongNotAnError(string? code)
    {
        var fixture = new Fixture(deliveryConfigured: false, exposeCode: true);
        var real = fixture.Service.RequestSignInCodeAsync("a@b.co", "client").Result.DevCode!;
        if (code == real)
        {
            return;
        }

        var result = fixture.Service.ConfirmSignInAsync("a@b.co", "client", code).Result;

        Assert.Equal(CodeCheckStatus.WrongCode, result.Status);
    }

    [Fact]
    public void Confirm_CodeWithSurroundingSpaces_IsTrimmedAndAccepted()
    {
        var fixture = new Fixture(deliveryConfigured: false, exposeCode: true);
        var code = fixture.Service.RequestSignInCodeAsync("a@b.co", "client").Result.DevCode!;

        var result = fixture.Service.ConfirmSignInAsync("a@b.co", "client", "  " + code + "\t").Result;

        Assert.Equal(CodeCheckStatus.Confirmed, result.Status);
    }

    [Fact]
    public void Confirm_CodeIsValidUpToTenMinutesAndExpiresOneTickLater()
    {
        var clock = new MutableClock(Start);
        var fixture = new Fixture(deliveryConfigured: false, exposeCode: true, clock: clock);
        var code = fixture.Service.RequestSignInCodeAsync("a@b.co", "client").Result.DevCode!;

        clock.Now = Start.AddMinutes(10);
        Assert.Equal(CodeCheckStatus.Confirmed, fixture.Service.ConfirmSignInAsync("a@b.co", "client", code).Result.Status);

        var secondRequest = fixture.Service.RequestSignInCodeAsync("a@b.co", "other").Result.DevCode!;
        clock.Now = Start.AddMinutes(10).AddMinutes(10).AddTicks(1);
        Assert.Equal(CodeCheckStatus.Expired, fixture.Service.ConfirmSignInAsync("a@b.co", "other", secondRequest).Result.Status);
    }

    [Fact]
    public void Confirm_AfterAFreshRequest_TheOlderCodeNoLongerWorks()
    {
        // Each request for the same email and browser replaces the pending code.
        var fixture = new Fixture(deliveryConfigured: false, exposeCode: true);
        var first = fixture.Service.RequestSignInCodeAsync("a@b.co", "client").Result.DevCode!;
        var second = fixture.Service.RequestSignInCodeAsync("a@b.co", "client").Result.DevCode!;

        if (first != second)
        {
            Assert.Equal(CodeCheckStatus.WrongCode, fixture.Service.ConfirmSignInAsync("a@b.co", "client", first).Result.Status);
        }

        Assert.Equal(CodeCheckStatus.Confirmed, fixture.Service.ConfirmSignInAsync("a@b.co", "client", second).Result.Status);
    }

    [Fact(Skip = "FINDING F-E4 (security): Check() increments Attempts without a lock, so a parallel burst can exceed the five-guess limit")]
    public void Confirm_FiveWrongGuessesInParallel_AllowsNoMoreThanFiveGuesses_Finding()
    {
        // Check() reads and increments Attempts without a lock, so a burst of parallel guesses can all read a low count.
        // Only five wrong answers are allowed; the rest should be TooManyAttempts.
        var fixture = new Fixture(deliveryConfigured: false, exposeCode: true);
        var real = fixture.Service.RequestSignInCodeAsync("a@b.co", "client").Result.DevCode!;
        var wrong = real == "000000" ? "111111" : "000000";

        var results = Enumerable.Range(0, 300)
            .AsParallel()
            .Select(_ => fixture.Service.ConfirmSignInAsync("a@b.co", "client", wrong).Result.Status)
            .ToList();

        Assert.True(results.Count(s => s == CodeCheckStatus.WrongCode) <= 5);
    }

    // ---- Account enumeration and limiter edges ----

    [Fact(Skip = "FINDING F-E1 (privacy): a known account reports RateLimited after five requests while an unknown address never does")]
    public void PasswordReset_KnownAndUnknownAddresses_AnswerTheSameWhenRateLimited_Finding()
    {
        var fixture = new Fixture(deliveryConfigured: true);
        fixture.Users.Add(new User { Email = "known@b.co", EmailVerifiedAt = Start });

        EmailRequestResult? knownLast = null;
        EmailRequestResult? unknownLast = null;
        for (var i = 0; i < 8; i++)
        {
            knownLast = fixture.Service.RequestPasswordResetAsync("known@b.co", "client-" + i).Result;
            unknownLast = fixture.Service.RequestPasswordResetAsync("unknown@b.co", "client-" + i).Result;
        }

        Assert.Equal(unknownLast!.Status, knownLast!.Status);
    }

    [Fact(Skip = "FINDING F-E2 (security): flooding more than 5000 distinct client keys clears every limiter, resetting a victim's per-email limit")]
    public void PerEmailLimit_SurvivesAFloodOfOtherClients_Finding()
    {
        var fixture = new Fixture(deliveryConfigured: false, exposeCode: true, maxPerHour: 100_000, maxPerDay: 100_000);
        for (var i = 0; i < 5; i++)
        {
            fixture.Service.RequestSignInCodeAsync("victim@b.co", "victim-" + i);
        }

        for (var i = 0; i < 3000; i++)
        {
            fixture.Service.RequestSignInCodeAsync("flood" + i + "@b.co", "bot-" + i);
        }

        Assert.Equal(EmailRequestStatus.RateLimited, fixture.Service.RequestSignInCodeAsync("victim@b.co", "victim-6").Result.Status);
    }

    [Fact]
    public void Options_ZeroHourlyCeiling_IsRefusedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fixture(deliveryConfigured: true, maxPerHour: 0));
    }

    [Fact(Skip = "FINDING F-E3: a zero per-client ceiling passes construction and throws ArgumentOutOfRange on the first request, a 500 instead of a config error")]
    public void Options_ZeroPerClientCeiling_IsRefusedAtConstruction_Finding()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fixture(deliveryConfigured: true, maxPerClientPerHour: 0));
    }

    [Fact]
    public async Task Request_ZeroPerClientCeiling_ThrowsOnTheFirstRequest_AsRecorded()
    {
        var fixture = new Fixture(deliveryConfigured: true, maxPerClientPerHour: 0);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Service.RequestSignInCodeAsync("a@b.co", "client"));
    }

    // ---- helpers ----

    private sealed class MutableClock(DateTimeOffset start)
    {
        public DateTimeOffset Now { get; set; } = start;
    }

    private sealed class RecordingSender : IEmailSender
    {
        public List<string> Sent { get; } = [];

        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
        {
            Sent.Add(toEmail);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryUsers : IUserRepository
    {
        private readonly List<User> _users = [];

        public void Add(User user) => _users.Add(user);

        public Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default) =>
            Task.FromResult(_users.FirstOrDefault(u => u.Email == email));

        public Task<User?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_users.FirstOrDefault(u => u.Id == id));

        public Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<User>>(_users.ToList());

        public Task SaveAsync(User user, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ChangeEmailAsync(User user, string newEmail, CancellationToken cancellationToken = default)
        {
            user.Email = newEmail;
            return Task.CompletedTask;
        }

        public Task<User> CreateAsync(User user, CancellationToken cancellationToken = default)
        {
            _users.Add(user);
            return Task.FromResult(user);
        }
    }

    private sealed class NoProfiles : IUserProfileRepository
    {
        public Task<UserProfile?> GetAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult<UserProfile?>(null);

        public Task<UserProfile> EnsureProfileAsync(string userId, string displayName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetRoleAsync(string userId, string role, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetDisabledAsync(string userId, bool disabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<UserProfile>> ListAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Fixture
    {
        public Fixture(
            bool deliveryConfigured,
            bool exposeCode = false,
            int maxPerClientPerHour = AccountEmailService.DefaultMaxPerClientPerHour,
            int maxPerHour = AccountEmailService.DefaultMaxPerHour,
            int maxPerDay = AccountEmailService.DefaultMaxPerDay,
            MutableClock? clock = null)
        {
            var time = clock ?? new MutableClock(Start);
            Users = new MemoryUsers();
            Sender = new RecordingSender();
            var options = new AccountEmailOptions("Test", deliveryConfigured, exposeCode, maxPerClientPerHour, maxPerHour, maxPerDay);
            Service = new AccountEmailService(
                Users,
                new NoProfiles(),
                Sender,
                new AccountRoles(["Player", "Admin"], "Player", "Admin"),
                options,
                () => time.Now);
        }

        public MemoryUsers Users { get; }

        public RecordingSender Sender { get; }

        public AccountEmailService Service { get; }
    }
}
