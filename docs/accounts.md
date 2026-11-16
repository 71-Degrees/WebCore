# Accounts and sign-in

Shared since 2026-11-16 (lifted from CrateDigger, with Better Trivia's password service, security stamp and hash upgrade). One identity per person, signing in
with a password or an emailed six-digit code, roles issued as role claims, admin disable and masquerade.

## Where it lives

| Part | Namespace / file | What it owns |
|---|---|---|
| Types | `WebCore.Accounts`: `User`, `UserProfile`, `AccountRoles`, `AccountClaims` | the stored shapes, the role list, the claims a sign-in issues |
| Rules | `PasswordHasher`, `PasswordRules`, `DisplayNames`, `LocalReturnUrl`, `AccountMessages` | hashing (PBKDF2, 600 000 iterations, upgraded at sign-in), what a new password must be, safe return paths, the text for each status code |
| Storage | `UserRepository`, `UserProfileRepository`, `CachedUserRepository`, `CachedUserProfileRepository` | one document per email (SHA-256 name) and per user id; 30-second caches cleared by every write made through the app |
| Services | `AccountEmailService`, `PasswordService` | emailed codes (sign in, confirm or change email, reset), and every password change (rules, new security stamp, notice email) |
| Email | `WebCore.Email`: `IEmailSender`, `NullEmailSender` | the sender interface; each app picks its provider |
| Hosting | `WebCore.Hosting.Accounts`: `AddWebCoreAccounts`, `MapWebCoreAccountEndpoints`, `AccountSessionEvents`, `AccountSignIn`, `AccountRateLimits`, `SignInDiagnostics` | cookie, Admin policy, sign-in checks, form endpoints |
| Forms | `WebCore.Hosting.Security.FormAntiforgery` (`MapFormGroup`) | antiforgery on every form endpoint, refusals logged under `WebCore.Forms` |

Pages stay in each app (they post to the endpoints below).

## Using it

```csharp
// The app's own email provider first (or none: a sender that only logs is registered).
builder.Services.AddSingleton<IEmailSender>(...);
builder.AddWebCoreAccounts(o =>
{
    o.BrandName = "CrateDigger";
    o.Roles = AccountRoles.FromEnum(UserRole.Member, UserRole.Admin);
    o.UsersStore = storage.Documents("users");
    o.ProfilesStore = storage.Documents("profiles");
    o.EmailDeliveryConfigured = true;
    o.CookieName = "cratedigger_auth";            // never change a live app's value: it signs everyone out
    o.CodeClientCookieName = "cratedigger_code_client";
    o.ClaimPrefix = "cratedigger:";               // read back from live cookies
    o.LogCategory = "CrateDigger.Accounts";       // alert queries key on it
    o.MasqueradeLandingPath = "/";
});
builder.Services.AddCascadingAuthenticationState();   // Blazor apps
...
app.MapWebCoreAccountEndpoints();
```

Checks use the role claim: `RequireAuthorization(AccountPolicies.Admin)`, `[Authorize(Policy = AccountPolicies.Admin)]`, `<AuthorizeView Roles="Admin">`. Never read the
profile to decide who can do what.

Configuration keys (unchanged from the apps): `Auth:AdminEmails` (comma-separated; a CONFIRMED address there is promoted to admin at sign-in),
`AccountEmail:SendInDevelopment`, `AccountEmail:MaxPerClientPerHour` / `MaxPerHour` / `MaxPerDay`. In Development codes are shown on screen, not emailed.

## Roles are strings

The owner's decision (2026-11-15): WebCore stores the role as a name, and each app keeps its own enum for its list. `AccountRoles.Names` is in the enum's
order, because profiles saved before 2026-11-16 hold the enum's number (`"Role": 1`): `RoleNameJsonConverter` reads a number as its digits and the
repository turns index n into `Names[n]`. The next save writes the name. A name the app does not know is kept as it is (it may come from a newer version).

**Rollback:** once a profile has been saved with a name, a version of an app from before this change cannot read it (its enum expects a number). Rolling
an app back past its switch to WebCore accounts needs the role names written back as numbers first.

## Endpoints (form posts; the paths and field names are a contract with each app's pages)

| Path | Form fields | Redirects to |
|---|---|---|
| `/account/logout` | | `/` |
| `/account/register-submit` | `email`, `displayName`, `password`, `confirmPassword`, `returnUrl` | `returnUrl`, or `RegisterPath?error=...` |
| `/account/login-submit` | `email`, `password`, `returnUrl` | `returnUrl`, or `LoginPath?error=1|disabled|ratelimited` |
| `/account/code-send`, `/account/code-verify` | `email`, `returnUrl`, (`code`, `displayName`) | `LoginPath?step=code`, `codeError=...`, `error=usepassword|disabled` |
| `/account/email-send`, `/account/email-verify` (signed in) | `email`, (`code`) | `AccountPath?step=code|emailError=...|confirmed=1` |
| `/account/reset-send`, `/account/reset-submit` | `email`, (`code`, `password`, `confirmPassword`) | `ForgotPasswordPath?...`, then `LoginPath?reset=1` |
| `/account/password-change` (signed in) | `currentPassword`, `password`, `confirmPassword` | `AccountPath?passwordDone=1|passwordError=...` |
| `/account/masquerade-stop` (signed in) | | `AdminUsersPath` |
| `{AdminUsersPath}/role`, `/disabled`, `/masquerade` (Admin) | `userId`, `role` / `disabled` | `AdminUsersPath?done=...|error=...`, masquerade to `MasqueradeLandingPath` |

`AccountMessages.ForCodeError` and `ForPasswordError` turn the status codes into words for the pages.

## Sessions

`AccountSessionEvents` runs on every request with the cookie (`Judge` is the pure decision, tested on its own): no profile, a disabled one, or a security
stamp that differs from the cookie's signs the visitor out; a changed role re-issues the cookie with the stored one. A password set, changed or reset
gives the account a new stamp, so every other session ends; the endpoint signs the current browser in again. Both stamps empty (an account whose
password never changed, a cookie from before stamps) stays valid, so adopting this signs nobody out.

## Caching (for each app's performance inventory)

| What | How long | Cleared by | Copies |
|---|---|---|---|
| Profiles by user id, and the profile list | 30 s | any profile write through the app | callers get their own copy |
| The account list, and lookups by id served from it | 30 s | any account write through the app | callers get their own copy |

Lookups by email read the store directly (one document). `StorageCalls` wraps both repositories under the caches.

## For an app adopting it (Better Trivia's switch is its own job)

- Map the app's role enum with `AccountRoles.FromEnum` (Better Trivia: `Player, Organizer, Admin`, default `Player`), and compare roles as strings.
- App-only account fields (Better Trivia's `SignupIp`, confirmation counters, team name, avatar) survive in `UnknownFields`; read them through an app-side
  accessor, or keep them in an app-owned document.
- Keep the app's cookie names, claim prefix, log category and store names in `AccountOptions`.
- Extras that stay app-side: signup screening, Tor list, canary account, a remembered (persistent) sign-in, keeping typed emails out of URLs.
