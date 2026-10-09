# C# engineering standard

How code is written in this repo. It is the owner's guardrail and it applies to everything written here - features, tools, tests, one-offs. It is enforced by `tools/CodeQuality` (the smell ratchet runs in the
pre-push check), not by good intentions. The persona and the short form live in CLAUDE.md; this is the long form.

## The design checkpoint (before typing)

Answer four questions, out loud in the plan or the commit message when the change is more than trivial:

1. **What is the single concern?** One sentence, no "and". If it needs an "and", it is two things.
2. **Where does it belong?** A page renders and wires; a service decides; a repository stores; an endpoint translates HTTP into a service call. Logic does not live in `.razor` files or in `Program.cs`.
3. **What already exists?** Search before writing. Reuse or extend; do not build a second copy (DRY applies to knowledge: one place that knows each rule).
4. **How will it be tested?** If the answer is "only by clicking", the design is wrong: pull the logic into something a unit test can reach.

## SOLID, as applied here

- **Single responsibility.** One reason to change per class and per method. A method that "loads, validates, transforms and saves" is four methods. A class named `...Manager` or `...Helper` doing five things is a smell.
- **Open/closed.** Add behaviour by adding an implementation, not by adding another `if` to a switch (`IAiGateway`, `IEmailSender`, the `Json*`/`Blob*` repositories are the pattern).
- **Liskov.** An implementation honours the interface's contract, including what it does on failure; a decorator (`Cached*`, `CustomerEmailSender`) never changes what the caller can rely on.
- **Interface segregation.** Small, role-shaped interfaces. A consumer must not depend on methods it never calls.
- **Dependency inversion.** Depend on interfaces, take them through the constructor, never `new` a collaborator inside logic. Time, randomness, the file system and the network are collaborators.

## DRY - knowledge, not keystrokes

- Two copies of a *rule* (a threshold, a validation, a formula, a list) must become one. Two lines that merely look alike are not a violation; do not build an abstraction for a pair, do it at the third use.
- The `Json*`/`Blob*` repository twins are duplication of *mechanism* behind one interface, on purpose. Business rules never get duplicated between them.
- A magic number or string that appears twice gets a name.

## Size and shape (the limits the Quality tool enforces)

| Thing | Limit | Do this instead |
|---|---|---|
| Method | 60 lines | Extract each step into a named method; the original reads like a table of contents |
| Branches in a method | 15 decision points | Extract; replace if/else chains with a lookup or small types; return early |
| Nesting | 4 levels | Guard clauses: invert the `if`, return or `continue` early, extract the inner block |
| Parameters | 7 | Group into a record or options object |
| File | 700 lines | One type per file, split by responsibility; endpoints grouped per area, never one giant `Program.cs` |
| Razor `@code` | 400 lines | Code-behind partial class, then move real logic into services |

**Never make something over its limit bigger.** If a change would add to a method or file that is over its limit, extract first, then add. "Match the surrounding code" does not override this: new and touched
code meets the standard even when its neighbours do not. Anything on the baseline that you touch is left better than you found it.

## Errors

Exceptions are exceptional, so when one happens it is documented: logged, with what was being done and to what. The owner's rule, which is the standard here:

- **A try/catch with logging sits at the top of every code path** - the entry points: event handlers, endpoint handlers, Razor event callbacks, hosted-service loops, timer and queue callbacks, `Main`. It logs the exception with enough context to act on (the operation, the ids involved) and then does the right thing for that entry point (a response, a retry, a message to the user, a clean stop).
- **Below the entry point, let exceptions bubble.** Methods do not wrap their bodies in try/catch "to be safe": a catch is not free (code, cost, and a place for an error to be lost), and one place that logs gives uniform records. The code underneath stays simple and says what it does.
- **A catch lower down is allowed where it earns its place**: translating an exception into a domain meaning, recovering and carrying on (one bad item in a loop must not stop the rest), cleaning up a resource, or a deliberate best-effort step. It catches the specific exceptions it expects (never a blanket `Exception` that hides programming errors) and it logs.
- **Never an empty catch.** At the very least a log line (debug is fine for a deliberate best-effort step) saying what failed and why it is safe to carry on. "Nothing to do" is itself a thing to record.
- No `async void` (except event handlers, which are entry points and so catch and log), no `.Wait()` / `.Result` / `.GetAwaiter().GetResult()`. Async all the way, with `CancellationToken` where the caller can cancel.
- Validate at the edge (input, config, outside responses); inside, trust the types. Fail fast with a message a person can act on.

## State and side effects

- No new mutable static state. Immutable `record`s for data. Side effects (I/O, time, randomness, email) at the edges, behind interfaces.
- **Time and HTTP:** take a `TimeProvider` (the caches in `WebCore.Caching` accept one) instead of reading `DateTime.UtcNow`, and an `IHttpClientFactory` or typed client instead of `new HttpClient()`. The `DirectClock` and `DirectHttpClient` smells flag the rest; app edges and command-line tools are exempt in `quality/quality.json`.
- **Duplicate code is a smell:** the `DuplicateCode` check names where the same block appears twice. Extract the shared part so one place knows each rule.
- Anything cached follows the cache policy in CLAUDE.md and docs/performance.md.

## Tests

- Every behaviour change ships with a test that fails without it. A bug fix ships with the test that would have caught it.
- A refactor is behaviour-preserving: run the tests before and after each step, one kind of change at a time, commit between steps so any step can be reverted.
- UI and browser-side behaviour is checked in the real browser too (see CLAUDE.md).

## Comments and names

- Names do the explaining; a comment says *why* (a constraint, a trade-off, a gotcha), never *what* the next line does. Short. No narration of the change history.
- Public surface gets a one-line summary when the name alone does not say it.

## Enforcement

- `dotnet run --project tools/CodeQuality/src/Quality` (and the pre-push check) compares against `quality/baseline.json`. New or worse smells fail the push. The baseline only ever shrinks: update it to lock in an improvement. Accepting
  a new finding on purpose is the owner's call, made explicitly, with the reason in the commit.
- `.NET analyzers` at Recommended level run in every build (Directory.Build.props) and are treated as errors once the code is clean for them; the few rules that do not fit this project are switched off in
  `.editorconfig` with a reason beside each.
- Roslyn findings are defects, not suggestions: fix them in the change that causes them, and work the baseline down on every pass through a file.

## Storage and startup (how the code is laid out now)

- **Storage:** a repository is written once over `IDocumentStore` (JSON documents) or `IBinaryStore` (raw files); `FileDocumentStore`/`FileBinaryStore` serve development and `BlobJsonStore` serves production.
  Rules (soft delete, id checks, stamping) live in the repository, never in a store. A new kind of data is a `StoreSpec` in `StorageBackend.cs` plus one line in `StorageRegistration.cs`.
- **Startup:** `Program.cs` only calls the modules in `src/<App>.Web/Startup/` (one short list of named steps per area: site, email, access, observability...).
- **Empty catches:** `dotnet run --project tools/CodeQuality/src/Refactor -- log-empty-catches [--apply]` gives every empty catch in a type with a logger a debug line (owner's rule). Core services take an optional
  `ILogger<T>? logger = null`, so tests stay simple and production logs.
- **Parameters:** more than seven means a small record (`DashboardOptions`, `SweepOptions`, `AccountEmailOptions`, ...), not a longer list.
