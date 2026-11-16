/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Text.RegularExpressions;
using WebCore.Accounts;
using WebCore.Email;

using WebCore.Storage;

namespace WebCore.Tests.Accounts;

/// <summary>Changing, setting and resetting a password, and the emailed-code half of the reset.</summary>
public class PasswordServiceTests : IDisposable
{
    private sealed class CapturingSender : IEmailSender
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = new();

        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
        {
            Sent.Add((toEmail, subject, htmlBody));
            return Task.CompletedTask;
        }

        public string LastCode() => Regex.Match(Sent[^1].Body, @"<strong>(\d{6})</strong>").Groups[1].Value;
    }

    private sealed class ThrowingSender : IEmailSender
    {
        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default) => throw new InvalidOperationException("smtp down");
    }

    private const string OldPassword = "old-password-1";
    private const string NewPassword = "new-password-2";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "TriviaNightPasswordTests_" + Guid.NewGuid().ToString("N"));
    private readonly UserRepository _users;
    private readonly UserProfileRepository _profiles;
    private readonly CapturingSender _sender = new();

    public PasswordServiceTests()
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

    private PasswordService Passwords(IEmailSender? sender = null, bool delivery = true, Action<Exception>? onFailure = null) => new(_users, sender ?? _sender, new AccountEmailOptions("Better Trivia", delivery), onSendFailure: onFailure);

    private AccountEmailService Codes() => new(_users, _profiles, _sender, TestRoles.MemberAdmin, new AccountEmailOptions("Better Trivia", true));

    private async Task<User> UserAsync(string? password, bool confirmed = true)
    {
        var user = new User
        {
            Email = "sam@example.com",
            DisplayName = "Sam",
            PasswordHash = password is null ? string.Empty : PasswordHasher.Hash(password),
            EmailVerifiedAt = confirmed ? DateTimeOffset.UtcNow : null,
        };
        await _users.CreateAsync(user);
        return user;
    }

    private async Task<bool> PasswordIsAsync(User user, string password) => PasswordHasher.Verify(password, (await _users.GetByIdAsync(user.Id))!.PasswordHash);

    [Fact]
    public async Task Change_WithTheRightCurrentPassword_SavesTheNewOne_AndEmailsANotice()
    {
        var user = await UserAsync(OldPassword);

        var result = await Passwords().ChangeAsync(user.Id, OldPassword, NewPassword, NewPassword);

        Assert.Equal(PasswordChangeStatus.Changed, result.Status);
        Assert.True(await PasswordIsAsync(user, NewPassword));
        Assert.False(await PasswordIsAsync(user, OldPassword));
        Assert.Equal("sam@example.com", Assert.Single(_sender.Sent).To);
        Assert.Contains("password was changed", _sender.Sent[0].Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Change_GivesTheAccountANewSecurityStamp_AndAFailedChangeDoesNot()
    {
        var user = await UserAsync(OldPassword);

        await Passwords().ChangeAsync(user.Id, "not-the-password", NewPassword, NewPassword);
        Assert.Null((await _users.GetByIdAsync(user.Id))!.SecurityStamp);

        await Passwords().ChangeAsync(user.Id, OldPassword, NewPassword, NewPassword);
        var first = (await _users.GetByIdAsync(user.Id))!.SecurityStamp;
        Assert.False(string.IsNullOrEmpty(first));

        await Passwords().ResetAsync((await _users.GetByIdAsync(user.Id))!, "another-pass-3", "another-pass-3");
        Assert.NotEqual(first, (await _users.GetByIdAsync(user.Id))!.SecurityStamp);
    }

    [Fact]
    public async Task CachedRepository_ServesTheStamp_AndSeesAChangeMadeThroughIt()
    {
        var user = await UserAsync(OldPassword);
        using var cached = new CachedUserRepository(_users);
        Assert.Null((await cached.GetByIdAsync(user.Id))?.SecurityStamp);

        user.SecurityStamp = "s2";
        await cached.SaveAsync(user);

        Assert.Equal("s2", (await cached.GetByIdAsync(user.Id))?.SecurityStamp);
        Assert.Null((await cached.GetByIdAsync("nobody"))?.SecurityStamp);
    }

    [Fact]
    public async Task Change_WithTheWrongCurrentPassword_ChangesNothing_AndSendsNothing()
    {
        var user = await UserAsync(OldPassword);

        var result = await Passwords().ChangeAsync(user.Id, "not-the-password", NewPassword, NewPassword);

        Assert.Equal(PasswordChangeStatus.WrongCurrentPassword, result.Status);
        Assert.True(await PasswordIsAsync(user, OldPassword));
        Assert.Empty(_sender.Sent);
    }

    [Theory]
    [InlineData("short", "short", "at least 8")]
    [InlineData("new-password-2", "different-3", "don't match")]
    public async Task Change_RefusesAPasswordThatBreaksARule_AndSaysWhich(string password, string confirmation, string expected)
    {
        var user = await UserAsync(OldPassword);

        var result = await Passwords().ChangeAsync(user.Id, OldPassword, password, confirmation);

        Assert.Equal(PasswordChangeStatus.Invalid, result.Status);
        Assert.Contains(expected, result.Message, StringComparison.Ordinal);
        Assert.True(await PasswordIsAsync(user, OldPassword));
    }

    [Fact]
    public async Task Change_OnAnAccountWithNoPassword_SaysToSetOne()
    {
        var user = await UserAsync(null);

        Assert.Equal(PasswordChangeStatus.NoPasswordSet, (await Passwords().ChangeAsync(user.Id, string.Empty, NewPassword, NewPassword)).Status);
    }

    [Fact]
    public async Task Set_GivesACodeCreatedAccountItsFirstPassword()
    {
        var user = await UserAsync(null);

        var result = await Passwords().SetAsync(user.Id, NewPassword, NewPassword);

        Assert.Equal(PasswordChangeStatus.Changed, result.Status);
        Assert.True(await PasswordIsAsync(user, NewPassword));
    }

    [Fact]
    public async Task Set_NeedsAConfirmedEmail_AndNeverReplacesAnExistingPassword()
    {
        var unconfirmed = await UserAsync(null, confirmed: false);
        Assert.Equal(PasswordChangeStatus.EmailNotConfirmed, (await Passwords().SetAsync(unconfirmed.Id, NewPassword, NewPassword)).Status);

        var withPassword = new User { Email = "other@example.com", DisplayName = "Other", PasswordHash = PasswordHasher.Hash(OldPassword), EmailVerifiedAt = DateTimeOffset.UtcNow };
        await _users.CreateAsync(withPassword);
        Assert.Equal(PasswordChangeStatus.AlreadyHasPassword, (await Passwords().SetAsync(withPassword.Id, NewPassword, NewPassword)).Status);
        Assert.True(await PasswordIsAsync(withPassword, OldPassword));
    }

    [Fact]
    public async Task ASignedOutCaller_IsRefused()
    {
        Assert.Equal(PasswordChangeStatus.NotSignedIn, (await Passwords().ChangeAsync(null, OldPassword, NewPassword, NewPassword)).Status);
        Assert.Equal(PasswordChangeStatus.NotSignedIn, (await Passwords().SetAsync("no-such-user", NewPassword, NewPassword)).Status);
    }

    [Fact]
    public async Task ANoticeThatCannotBeSent_NeverUndoesTheChange_AndIsReported()
    {
        var user = await UserAsync(OldPassword);
        Exception? reported = null;

        var result = await Passwords(new ThrowingSender(), onFailure: ex => reported = ex).ChangeAsync(user.Id, OldPassword, NewPassword, NewPassword);

        Assert.Equal(PasswordChangeStatus.Changed, result.Status);
        Assert.True(await PasswordIsAsync(user, NewPassword));
        Assert.NotNull(reported);
    }

    [Fact]
    public async Task WithoutMailDelivery_TheChangeStillHappens_AndNothingIsSent()
    {
        var user = await UserAsync(OldPassword);

        var result = await Passwords(delivery: false).ChangeAsync(user.Id, OldPassword, NewPassword, NewPassword);

        Assert.Equal(PasswordChangeStatus.Changed, result.Status);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task Forgotten_ACodeToTheAddressLetsTheOwnerChooseANewPassword_EvenWithoutKnowingTheOldOne()
    {
        var user = await UserAsync(OldPassword);
        var codes = Codes();

        Assert.Equal(EmailRequestStatus.Sent, (await codes.RequestPasswordResetAsync("Sam@Example.com", "browser-1")).Status);
        var confirmed = await codes.ConfirmPasswordResetAsync("sam@example.com", "browser-1", _sender.LastCode());
        Assert.Equal(CodeCheckStatus.Confirmed, confirmed.Status);

        var result = await Passwords().ResetAsync(confirmed.User!, NewPassword, NewPassword);

        Assert.Equal(PasswordChangeStatus.Changed, result.Status);
        Assert.True(await PasswordIsAsync(user, NewPassword));
    }

    [Fact]
    public async Task Forgotten_AnUnknownAddress_GetsTheSameAnswer_ButNothingIsSent()
    {
        var result = await Codes().RequestPasswordResetAsync("nobody@example.com", "browser-1");

        Assert.Equal(EmailRequestStatus.Sent, result.Status);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task Forgotten_AWrongCodeIsRefused_AndACodeForOneBrowserFailsInAnother()
    {
        await UserAsync(OldPassword);
        var codes = Codes();
        await codes.RequestPasswordResetAsync("sam@example.com", "browser-1");
        var code = _sender.LastCode();

        Assert.Equal(CodeCheckStatus.WrongCode, (await codes.ConfirmPasswordResetAsync("sam@example.com", "browser-1", code == "000000" ? "000001" : "000000")).Status);
        Assert.Equal(CodeCheckStatus.Expired, (await codes.ConfirmPasswordResetAsync("sam@example.com", "browser-2", code)).Status);
    }

    [Fact]
    public async Task AResetCode_IsNotASignInCode()
    {
        await UserAsync(OldPassword);
        var codes = Codes();
        await codes.RequestPasswordResetAsync("sam@example.com", "browser-1");

        var signIn = await codes.ConfirmSignInAsync("sam@example.com", "browser-1", _sender.LastCode());

        Assert.Equal(CodeCheckStatus.Expired, signIn.Status);
    }

    [Theory]
    [InlineData("1234567", false)]
    [InlineData("12345678", true)]
    public void TheRules_RequireAtLeastEightCharacters(string password, bool ok) => Assert.Equal(ok, PasswordRules.Validate(password, password) is null);

    [Fact]
    public void TheRules_RefuseAnAbsurdlyLongPassword()
    {
        var huge = new string('x', PasswordRules.MaximumLength + 1);

        Assert.Contains("at most", PasswordRules.Validate(huge, huge), StringComparison.Ordinal);
    }
}
