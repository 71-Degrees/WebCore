# Quality cheat sheet: write it right the first time

One page, read before writing C# (and pasted into every brief to a helper). The quality tool enforces these at push; this is how to not meet it there.
Evidence: the rules that fire most are, in order, `LongMethod`, `EmptyCatch`, `Complexity`, `ManyParameters`, `BroadCatch`, `LongFile`, then `NonAsciiLiteral` and `DuplicateCode`.
The limits: method 60 lines, 15 decision points, nesting 4, 7 parameters, file 700 lines, Razor `@code` 400 lines. They are guides, but never make something over its limit bigger.

## Parameters (`ManyParameters`, limit 7)

- **Count before you type the signature.** Past 5 parameters, stop and decide which of these it is:
  - **A constructor with more than 7 dependencies is two classes.** Split by responsibility and let each take its own dependencies. Do not bundle the parameters into a record: that hides the smell and keeps the class doing two jobs.
  - **A method with more than 7 values is a request.** Take one `record XxxRequest(...)` (immutable, named members); callers use object initializers or `with`.
  - **An endpoint handler that needs more than 5 injected services is doing the service's job.** Make it one line that calls a service, and let the service own the dependencies.
  - **A flag parameter (`bool`) usually means two methods.** Write two.
- Never add the 8th parameter "just this once": adding it is the moment to restructure.

## Method size and branching (`LongMethod` 60 lines, `Complexity` 15, `DeepNesting` 4)

- **Extract before you add.** If the method you are about to edit is over its limit, extract the step you are touching into a named method first, then change that.
- **Guard clauses over nesting:** `if (bad) return/continue/throw;` at the top, then the straight path.
- **A `switch` or an `if/else if` chain on a type or kind becomes a lookup table or a small class per case.**
- **Endpoint registration is not a place for logic:** `MapX("/path", HandlerMethod)` with a named static method; never an inline lambda over about 10 lines. This is how `MapAccountEndpoints` reached 290 lines.
- **Razor `@code` over about 100 lines moves to a service or a `.razor.cs` code-behind class.**
- **A service-registration method (`AddCatalog`, `AddX`) is a list of calls, one `AddY()` per concern, each under 60 lines.** A new feature adds its own `AddFeature()` and one call to it; it never grows the list's method past the limit.

## Errors (`EmptyCatch`, `BroadCatch`)

- **Exceptions are exceptional.** Do not catch below the top of the code path unless you translate, recover or clean up.
- **Never an empty `catch`.** At minimum a log line that says what failed and why carrying on is safe.
- **Catch only what you expect** (`catch (HttpRequestException)`), never bare `Exception`, except at the top of a path where it is logged.
- **No `_ = SomethingAsync();` (`DroppedTask`).** A started task is awaited, handed to a hosted worker, or logs its own failure; a dropped one fails unseen.

## Text and encoding (`NonAsciiLiteral`)

- **Typographic characters never go into a string literal** (curly quotes, dashes, ellipsis, accented letters, `ø`/`ß`): write the escape (`’`, `ø`). Text a user sees comes from a resource or the page, not a typed character in C#.
- If one slipped in: `dotnet run --project tools/CodeQuality/src/Refactor -- escape-non-ascii --path <folder> --apply --allow-dirty`.

## Duplication (`DuplicateCode`)

- **The second copy is the signal.** When you are about to paste a block, extract it first and call it twice. For async enumeration use `ToListAsync`/`await foreach` helpers that already exist before writing a loop.
- **Look for the existing piece before you write plumbing.** Parallel helpers each invented their own copy of the same four shapes (2026-11-17, 39 findings, nearly all `DuplicateCode`); reuse or extract a shared base in the same change:
  - a hosted background worker with a bounded, persisted, de-duplicated pending list (one `PersistedWorkQueue`-style base, not a queue per feature);
  - a JSON document store over the keyed-document store (get one, save one, list by prefix: one helper, not a copy per store);
  - a `Meter` plus an in-memory rolling summary (one helper; each metric class only names its counters);
  - two endpoints that return the same fragment or file (one private method both call).
- **The tool flags 8 or more repeated lines.** Anything over about 10 lines that exists twice is extracted before the third use.

## Files (`LongFile` 700)

- A file near 600 lines gets split by responsibility before you add to it (endpoint groups out of `Program.cs`, one class per file).

## Web assets (a different guard: `OperationsTests`)

- Every script and stylesheet is referenced through `@Assets["..."]`, including `_content/<library>/...` files. A bare path is never fingerprinted and phones keep a stale copy.

## Before you hand work to a helper

Paste this sheet's rules (or its path) into the brief. A helper does not read `CLAUDE.md`; it builds from habit, and the habit is what the gate catches.
