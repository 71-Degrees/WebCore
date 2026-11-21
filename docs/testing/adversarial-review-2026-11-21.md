# WebCore adversarial review, 2026-11-21

Scope: every file in `src/WebCore` (the shared core), then the security-facing files of `src/WebCore.Hosting`.
Method: read each file's methods, read its existing tests, then probe each method with null, empty, default,
full, boundary and extreme values, mistyped JSON, and hostile strings. Assertions state the behaviour the code
has today (documented) or the behaviour it should have (finding, skipped). Nothing in `src` was changed.

Status: the new tests are **uncommitted** in the working tree. The suite is green (0 failed).

## Numbers

| | Before | After |
|---|---|---|
| Tests (whole solution) | 457 pass | 928 pass, 15 skipped (findings), 0 fail |
| Line coverage, `src/WebCore` | 83.6% | 89.2% |
| Line coverage, `src/WebCore.Hosting` | 40.3% | 40.7% |
| Line coverage, `src/WebCore.Deploy` | 26.9% | 26.9% (not hammered) |
| Line coverage, `src/WebCore.UI` | 41.9% | 41.9% (not hammered) |
| Line coverage, all `src` | 51.2% | 53.3% |

Coverage is line coverage merged across the four test assemblies (a line counts if any test hits it). The
merge was done by hand from the cobertura files; it is an estimate, not a ReportGenerator figure.

New tests (`tests/WebCore.Tests/Adversarial`, `tests/WebCore.Hosting.Tests/Adversarial`):
486 cases (471 pass, 15 skipped), from 6 + 1 files.

## Findings

Each finding is a `[Fact(Skip = "FINDING F-xx ...")]` whose assertion is the *desired* behaviour. Every one was
also run unskipped once: **all 16 failed**, which confirms the current behaviour differs from the desired one.
Logs: `docs/testing/logs/findings-unskipped-core-2026-11-21.log` and `...-hosting-...log`.

To re-check any of them: remove its `Skip = ...`, run the test, expect a failure.

| ID | Sev (my judgement) | File | What happens | Evidence |
|---|---|---|---|---|
| F-A2 | High (needs write access to the user store) | `Accounts/PasswordHasher.cs` | A stored hash with an empty key (`600000.<salt>.`) verifies **any** password as true: PBKDF2 returns zero bytes, and `FixedTimeEquals(empty, empty)` is true. | run: failed |
| F-E4 | Medium (security) | `Accounts/AccountEmailService.cs` `Check()` | `Attempts++` is unlocked. A 300-call parallel burst of wrong codes can get more than the five allowed guesses. The count was not captured. | run: failed |
| F-E2 | Medium (security) | `Accounts/AccountEmailService.cs` `TryAcquire` | When more than 5000 keys are tracked, the whole limiter table is cleared. Flooding 3000 addresses resets a victim's per-email limit. | run: failed |
| F-R1 | Medium (security) | `RateLimiting/KeyedRateLimiter.cs` | Same pattern: a full table is `Clear()`ed, so distinct-key floods reset every key's limit. The "clears the table" test passes, so the mechanism is shown; the victim-reset assertion was run and failed. | run: failed |
| F-E1 | Medium (privacy) | `Accounts/AccountEmailService.cs` `RequestPasswordResetAsync` | Known addresses return `RateLimited` after five requests; unknown addresses never do. Account existence can be read from the response. | run: failed |
| F-AU1 | Medium (privacy) | `Audit/AuditLog.cs` `ForgetOwnerAsync` | Forgetting an owner removes their entries from the owner list, the recent list and the item histories of items they own. Their actions on *other owners'* items stay, with their actor id. | run: failed |
| F-H1 | Medium-low | `Health/HealthSignal.cs` `IsStale` | `ExpectedEveryHours` comes from the publish PUT. `+Infinity` or `1e300` makes `TimeSpan.FromHours` throw `OverflowException` when judged, so one bad signal can break the health page. | run: failed (1e300); +Infinity throws (test passes) |
| F-A1 | Low-medium | `Accounts/PasswordHasher.cs` `Verify` | A stored hash with iteration count `0` or `-1` throws `ArgumentOutOfRange` instead of returning false, so one bad row becomes an error on sign-in. | run: failed |
| F-H3 | Low (defence in depth) | `WebCore.Hosting/Security/ProbeRequestMiddleware.cs` | Traversal written after `/.well-known/` (`/.well-known/../../x.php`) is exempted before the `..` check runs. | run: failed |
| F-H2 | Low (only if a caller passes untrusted input; callers not checked) | `Health/Sparkline.cs` | The `stroke` argument is written into an SVG attribute unescaped. | run: failed |
| F-CSP1 | Low (config only) | `WebCore.Hosting/Edge/ContentSecurityPolicy.cs` | A `;` in a configured source writes an extra directive into the header. | run: failed |
| F-E3 | Low | `Accounts/AccountEmailService.cs` | `maxPerClientPerHour: 0` is accepted at construction and throws `ArgumentOutOfRange` on the first request (a 500, not a config error). | run: request-time throw confirmed |
| F-AU2 | Low | `Audit/AuditActor.cs` `AuditActorContext` | Scopes disposed out of order restore the wrong actor. | run: failed |
| F-C1 | Low | `Accessibility/CssContrast.cs` `Ratio` | Public `Ratio` takes only six-digit hex; `#fff` throws. Exception type not checked. | run: failed |
| F-G1 | Low | `Ai/GeminiApi.cs` `ArrayIn` | Greedy regex from first `[` to last `]`: stray brackets in a reply hide a valid array (returns empty). | run: failed |

### Recorded behaviour (passing tests that document a questionable choice)

These pass and assert today's behaviour. Each is named `_Finding` or `_AsRecorded` in its test file.

- `PasswordRules` counts UTF-16 units, so four emoji satisfy the eight-character minimum.
- `PasswordHasher.Verify(null hash)` throws NullReference; `NeedsRehash(null)` throws NullReference.
- `UserProfile` role `null` reads as `null`, not `""` (the converter is not called for JSON null).
- `AccountRoles`: default equal to admin makes every new account an admin (`FromEnum(P, P)`).
- `AccountRoles.Normalize("-1")`, `" 1"`, `"+1"` pass through raw; callers must check `IsKnown`.
- `AccountClaims` with a null prefix quietly yields bare claim names; a null auth type yields an unauthenticated principal.
- `LocalReturnUrl` has no length cap (64 KB passes through).
- `DisplayNames` accepts a zero-width-space-only name.
- `ReleaseNotesParser.ParseReleaseTag` throws on impossible dates (`2026-02-30`, hour 25) instead of returning null.
- `ParseShortStat` throws `OverflowException` on a huge count.
- `FileDocumentStore`: `"a/doc"` and `"doc"` are the same document (`Path.GetFileName` drops folders); a wrong-type read returns default silently; an empty name throws after retries.
- `StaticAssetCachePolicy.Exempt` is typed read-only but is a mutable dictionary (a cast edits the process-wide policy).
- `StorageCalls` and `CacheRegistry` hold mutable static state, against the CLAUDE.md standard.
- `KeyedCache` with `maxEntries: 0` holds one entry, not zero.
- `KeyedCache` returns a completed load even for a cancelled token.
- `AccountAttemptLimiter` with a null or empty address skips the per-address bucket (the global cap still applies).
- `AccountAttemptLimiter(scale: int.MaxValue)` overflows and throws at construction.
- `ManualTimeProvider`: an infinite timer fires on the next advance; periodic timers fire once.
- `StaticAssetVersion`: a missing file stays unversioned, and a changed file keeps its first hash, until restart.
- `CanonicalHostRedirect`: method matching is case-insensitive, so `get` is moved.
- `GeminiApi.Usage("not json")` throws `JsonReaderException`, a `JsonException` subclass.
- `AuditLog` with `KeepRecent: -1` throws on append (no option validation).

## Coverage gaps (not hammered in this pass)

- `src/WebCore/Storage/Blob/BlobJsonStore.cs` 0%. Its tests return early when `WEBCORE_TEST_BLOB_CONNECTION_STRING` is unset, so they **count as passed**. The "0 skipped" total is misleading on a machine without it.
- `src/WebCore/Health/HealthPublisher.cs` 0%; `Email/IEmailSender.cs` (`NullEmailSender`) 20%. The null sender logs the recipient's address at warning level (privacy, not tested).
- `src/WebCore/Accounts/AccountMessages.cs` 35%.
- `src/WebCore.Hosting`: endpoint files at 0% (`AccountEndpoints`, `AdminUserEndpoints`, `PasswordEndpoints`, `HealthEndpoints`), `FormAntiforgery`, `AuditTrailRegistration`. Not probed.
- `src/WebCore.Deploy` (26.9%) and `src/WebCore.UI` (41.9%): not probed. Deploy is mostly process and git plumbing; it needs its own pass.

## Suggested tests not yet written (method ideas)

- Property/fuzz tests: `LocalReturnUrl` is already fuzzed; the same shape would suit `DisplayNames`, `NormalizeEmail`, `CssContrast.Hex/Rgb`, `DocumentNames`.
- Concurrency stress: `KeyedCache.GetOrLoadAsync` under churn with `Invalidate`; `FileDocumentStore` concurrent `TrySetIfNotExists` (only the two-writer case is tested today).
- Round-trip: `LiveSettingDefinition` values through the document store and back; `User`/`UserProfile` JSON with unknown fields.
- Clock edges: `DateTimeOffset.MinValue`/`MaxValue` inputs for every `TimeProvider` consumer (only the limiter was probed).
- Not yet probed: `UserRepository.SaveAsync` looks up the account by the *current* `Email`, so an edited email without `ChangeEmailAsync` saves nothing and reports no error. Worth a test.
- Fault injection: a store that fails mid-`ChangeEmailAsync` (after `TrySetIfNotExists`, before `Delete`), to see the duplicate-account window.
- Mutation check: run Stryker or a hand-rolled mutator on `PasswordRules`, `LocalReturnUrl`, `AccountRoles.Normalize`, `CanonicalHostRedirect` to see whether the new tests fail when the code is changed.

## How another model can evaluate this

1. Read this file and the findings table.
2. Logs: `docs/testing/logs/`. The full run is `full-suite-2026-11-21.log`; the unskipped finding runs are `findings-unskipped-*.log`.
3. Tests: `tests/WebCore.Tests/Adversarial/*.cs` and `tests/WebCore.Hosting.Tests/Adversarial/*.cs`. Names say what input was used; `_Finding` and `_AsRecorded` say whether the assertion is the desired behaviour or a recording.
4. To verify a finding: unskip it and run `dotnet test --filter "FullyQualifiedName~Adversarial"`.
5. Coverage numbers come from cobertura output of `dotnet test WebCore.slnx --collect:"XPlat Code Coverage"`, merged by line union (method in this file's Numbers section).

## Resolution (same day: Opus judged, Haiku built, one slice per commit)

Every finding test above is now live (unskipped) and passing; the "recorded" tests that described a fixed behaviour were flipped to the new one.

| Finding | Commit | What was done |
|---|---|---|
| F-A2, F-A1 | 6c08b84 | `PasswordHasher.Verify` refuses before any PBKDF2 work: null/empty hash, iterations outside 1..10 000 000, salt under 8 bytes, key outside 16..64 bytes. |
| F-E4, F-E1, F-E3 | 51f3c65 | Attempts counted with Interlocked; a correct code confirms only for the call that removes it; reset spends the limits before the lookup; ceilings under 1 fail at construction. |
| F-R1, F-E2 | f5434ca | A full key table forgets idle, then least-used keys (was: cleared). AccountEmailService uses KeyedRateLimiter instead of its own copy. KeyedCache and KeyedRateLimiter refuse sizes under 1; AccountAttemptLimiter no longer overflows. |
| F-H1, F-H2 | 1337032 | IsStale compares hours (no overflow); Sparkline skips non-finite values, computes in halves, encodes the stroke. Ordinary output pinned and unchanged. |
| F-H3, F-CSP1, F-C1, F-G1 | 5f13f4f | Traversal checked before the well-known exemption; CSP sources validated; Ratio normalises colours; ArrayIn returns the first complete JSON array; ParseReleaseTag/ParseShortStat never throw. |
| Recorded items | 50e92c4 | Return URL cap 2048; frozen Exempt table; EmailMask (null sender logs a masked address); AuditLog options checked at construction. |
| Test plumbing | 4af9df8 | ManualTimeProvider: infinite never fires, periodic repeats, Change re-arms. Blob tests are `[BlobFact]` and report as skipped. |
| F-AU1 | c14174e | Owner's decision: a forgotten person's own entries are removed; their actions on others' items keep the name, with id `forgotten` and no trace or path. Actor index `actor-{id}.json`. |
| F-AU2 | (dropped) | Tested misuse (scopes disposed out of order); the test was removed. |

Still open: the Hosting endpoint files, WebCore.Deploy and WebCore.UI were not probed. The apps' own tests that use ManualTimeProvider (CrateDigger, TriviaNight) should be run at the next WebCore submodule bump.

## Second pass: Hosting endpoints (2026-11-21)

Scope: the endpoint files the first pass did not reach, `src/WebCore.Hosting`: `Accounts/AccountEndpoints.cs`, `Accounts/AdminUserEndpoints.cs`,
`Accounts/PasswordEndpoints.cs`, `Health/HealthEndpoints.cs`, `Security/FormAntiforgery.cs`, `Audit/AuditTrailRegistration.cs`. Nothing in `src` was changed.

Method: a `TestServer` (package `Microsoft.AspNetCore.TestHost` **10.0.12**, added to `tests/WebCore.Hosting.Tests` only, matching the shared framework 10.0.12)
runs the real endpoints with temp-folder stores, an ephemeral data-protection provider and a recording mail sender (no network, no real blob store). A
small cookie jar keeps the sign-in and antiforgery cookies, so the probes sign in for real: password, emailed code, masquerade. Shared host setup is in
`tests/WebCore.Hosting.Tests/Adversarial/EndpointSite.cs`.

Files (`tests/WebCore.Hosting.Tests/Adversarial/`):

| File | Lines | Cases | Passing | Skipped (findings) |
|---|---|---|---|---|
| `EndpointSite.cs` (shared host helper, no tests) | 286 | - | - | - |
| `AccountEndpointsAdversarialTests.cs` | 650 | 68 | 67 | 1 |
| `AdminAndPasswordEndpointsAdversarialTests.cs` | 533 | 47 | 46 | 1 |
| `HealthAndAuditEndpointsAdversarialTests.cs` | 635 | 63 | 57 | 6 |
| **Total** | | **178** | **170** | **8** |

Whole hosting test project after this pass: 334 total, 326 pass, 8 skipped, 0 failed. `dotnet build WebCore.slnx`: 0 errors.

Every skipped test was first run unskipped and failed (the actual value is in the evidence column). The `_AsRecorded` tests pass and pin a questionable choice.

### Findings

| ID | Sev | File | What happens | Evidence |
|---|---|---|---|---|
| F-W1 | Medium | `src/WebCore.Hosting/Health/HealthEndpoints.cs` (`PutReportAsync`) | A chunked report upload (no `Content-Length`) over 2 MB is read whole into memory before the size check; `HealthReportStore.SaveAsync` then throws, so the answer is a 500, not a 413. The early check only sees `Content-Length`. | run unskipped: failed with `ArgumentException: The report is 2,097,153 bytes; the most kept is 2,097,152` |
| F-W2 | Medium | `src/WebCore.Hosting/Audit/AuditTrailRegistration.cs` (`AuditActors.From`) | While an admin masquerades, the audit actor is the account being viewed, not the admin who made the change, so the trail names the wrong person. The masquerade's original-admin claim is ignored. | run unskipped: failed with `Expected "u1", Actual "u2"` |
| F-W3 | Low | `HealthEndpoints.cs` (`Clamp`, link rule) | A signal link of `/\evil.example` passes (starts with `/`, not `//`); browsers read it as `//evil.example`, so the dashboard link leaves the site. Needs the publish key. | run unskipped: failed with `Actual "/\\evil.example"` (stored) |
| F-W4 | Low | `HealthEndpoints.cs` (`Clamp`), `HealthSignalStore` | `expectedEveryHours` of `1e400` parses to +Infinity and the store's JSON write throws, so the publish is a 500 and nothing is stored. `IsStale` already guards infinity; the write does not. | run unskipped: failed with `ArgumentException: .NET number values such as positive and negative infinity cannot be written as valid JSON` |
| F-W5 | Low | `src/WebCore.Hosting/Accounts/AdminUserEndpoints.cs` (`TryGetProfileAsync`) | A 1000-character user id reaches the file store; the `IOException` (file name too long) is not caught (only `ArgumentException` is), so an admin form answers 500 instead of `error=unknown`. Shared helper: masquerade and disable take the same path (not separately run). | run unskipped: failed with `IOException: The filename, directory name, or volume label syntax is incorrect` |
| F-W6 | Low | `AccountEndpoints.cs` (`RegisterAsync`) and `WebCore/Accounts/DisplayNames.cs` (`IsValid`) | A display name with a control character (`Sam\u0001Eve`) is accepted and the account is created; `Tidy` only collapses whitespace. | run unskipped: failed with redirect `/` (account created, no error) |
| F-W7 | Low | `HealthEndpoints.cs` (signal id via `DocumentNames.IsValid`) | No length cap on a signal id; a 1000-character id is stored as a dictionary key with every publication. | run unskipped: failed with `Expected BadRequest, Actual OK` |
| F-W8 | Low | `HealthEndpoints.cs` (publish) | A numeric status outside the enum (`"Status": 7`) is accepted (`JsonStringEnumConverter` allows integers) and stored. | run unskipped: failed with `Expected BadRequest, Actual OK` |

### Recorded behaviour (passing, questionable but deliberate or harmless)

- `Login_SchemeInReturnUrl_StaysOnThisSiteAsAPath_AsRecorded`: `https://evil.example` becomes `/https://evil.example`, a path on this host. Not an open redirect, but `LocalReturnUrl`'s comment says a scheme collapses to `/`.
- `Login_NulInAFormValue_IsRefusedWith400_AsRecorded`: a NUL byte anywhere in a form value gets 400 with no redirect. Safe; nothing signs in.
- `Register_ExistingEmail_SaysSoAndRevealsTheAccount_AsRecorded`: registration says "already registered", so it reveals whether an address has an account (the sign-in and reset forms do not).
- `Register_OversizedEmailIsEchoedIntoTheRedirect_AsRecorded`: a 64 KB address is echoed into the redirect URL (only when the app keeps emails in URLs).
- `Login_DisabledAccountWithTheRightPassword_SaysDisabled_AsRecorded`: "disabled" is named only after the right password.
- `StaleAnonymousTokenOnLoginSubmit_AfterSignIn_GoesHome_AsRecorded`: a token issued to an anonymous visitor, posted once signed in, sends the visitor home rather than an error page.
- `AdminDisable_AnythingOtherThanLowerCaseTrue_ReEnables_AsRecorded`: `disabled=TRUE`, or a missing field, re-enables the account. Admin-only and CSRF-protected.
- `ChangePassword_AccountWithoutAPassword_NeedsNoCurrentOne_AsRecorded`: an account made by emailed code sets its first password without a current one.
- `Publish_DuplicateIdsInOnePublication_KeepTheLast_AsRecorded`: `accepted` counts both, the store keeps the last.
- `FormGate_WithoutTheAntiforgeryServiceRegistered_FailsLoudly_AsRecorded`: a host that maps the form endpoints without `AddAntiforgery` gets an exception on each post (fails closed, but only at request time, not at startup).

Confirmed as correct (passing, named after the input): logout and every form endpoint refuse GET, a missing or forged token, a token from another browser and JSON bodies; sign-in gives one answer for unknown, wrong and no-password accounts; return URLs `//`, `/\`, control characters and over 2048 characters go home, and 2048 is kept; the emailed-code limits (five guesses, the eleventh request limited, a code bound to its browser); reset-send gives the same answer for known and unknown addresses, including the rate limit; the health key gate (401 wrong or missing, 503 unset); `Clamp` cuts headline, detail, unit, and moves a future `AsOf` to now; the 2 MB report limit with a `Content-Length`; the masquerade rules (not while masquerading, not for self, unknown or disabled targets, a masquerading member cannot use admin forms); masquerade stop restores the admin; a changed password ends the session it was made from.

### Not probed

- Real Kestrel: everything ran on `TestServer` over http. Kestrel's own request-size and form-count limits, Secure cookies over https, and the real `Content-Length` handling are not exercised.
- `HealthWatchdog` and the alert path: the watchdog ran as a hosted service but its pass was not driven; no notifier was registered, so `HealthAlertDispatcher` and `NtfyNotifier` (outbound HTTP) were not exercised.
- Email failure branches: `SendFailed` and `Unavailable` for emailed codes were not driven (only the recording sender).
- Session judging while masquerading: an admin disabled while masquerading, and "resume" when the admin account is gone (`AccountSessionEvents`), were not driven.
- `AuditCircuitHandler` (the Blazor circuit actor) and an end-to-end masquerade through `UseAuditActors` (only `AuditActors.From` and anonymous requests).
- Cross-user antiforgery (a token issued to a second signed-in user).
- Read endpoints of the health dashboard and report page (only the PUT write door was in these files).
- `WebCore.Deploy` and `WebCore.UI`: still not probed.
- Note, outside this pass: `tests/WebCore.Hosting.Tests/Adversarial/EdgeHostingAdversarialTests.cs` (first pass) contains non-ASCII characters in string literals (`clé-secrète`); it is not touched here.
