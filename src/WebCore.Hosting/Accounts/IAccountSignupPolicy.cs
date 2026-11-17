/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */
using WebCore.Accounts;

namespace WebCore.Hosting.Accounts;

/// <summary>
/// An app's own rules for registering with a password (<c>/account/register-submit</c>): bot screening, a blocked network, an invite code that grants a
/// role. WebCore asks <see cref="Refusal"/> after its own form checks and BEFORE the shared attempt limiter, so a flood of refused bot attempts cannot use
/// up the attempts real people share; it calls <see cref="AccountCreatedAsync"/> once the account exists. The default (<see cref="AllowAllSignups"/>)
/// refuses nobody and does nothing. Accounts first made by an emailed code are not screened here.
/// </summary>
public interface IAccountSignupPolicy
{
    /// <summary>Why this signup is refused, in words for the person, or null when it may go on. The app logs its own refusals.</summary>
    string? Refusal(HttpContext http, string email, string displayName);

    /// <summary>After the account was created and before the browser is signed in: the app can record what it keeps of the signup (the address it came
    /// from) or grant a role from the form (an invite code).</summary>
    Task AccountCreatedAsync(HttpContext http, User user, IFormCollection form);
}

/// <summary>The default signup policy: everyone may register, and nothing more happens.</summary>
public sealed class AllowAllSignups : IAccountSignupPolicy
{
    public string? Refusal(HttpContext http, string email, string displayName) => null;

    public Task AccountCreatedAsync(HttpContext http, User user, IFormCollection form) => Task.CompletedTask;
}
