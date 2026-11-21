# 2026-11-21 Jarvis: WebCore adversarial review and its fixes

## What was done (pushed, master 3275e86)

- Coverage measured: src/WebCore 83.6% -> 89.2% line coverage, all src 51.2% -> 53.3%.
- 486 adversarial test cases (null, empty, boundary, extreme, hostile input) for every src/WebCore file and the Hosting edge middleware; write-up and raw logs in `docs/testing/adversarial-review-2026-11-21.md` and `docs/testing/logs/`.
- 15 findings judged by Opus; 14 fixed by Haiku in 8 slices, one dropped (F-AU2, tested misuse). The resolution table is at the end of the review doc. The highest: a stored hash with an empty key verified any password (F-A2); parallel code guesses beat the five-try limit (F-E4); a flood of keys reset rate limits (F-R1, F-E2); password reset revealed which accounts exist (F-E1).
- Owner's decision (F-AU1): forgetting a person removes their own entries; their actions on other owners' items keep the name as text, with id `forgotten`.
- The push needed a rebase over another Jarvis session's Better Trivia account hardening, which had fixed F-E4 and F-E1 its own way. Its versions were kept, F-E3 and single-use codes re-applied, and `LimiterTable` now evicts least-used keys (commit 3275e86).

## Next

- Not probed: the Hosting endpoint files, WebCore.Deploy, WebCore.UI.
- When the apps next bump their WebCore submodule, run the CrateDigger and TriviaNight tests: ManualTimeProvider now never fires infinite timers and repeats periodic ones.
- Small follow-up: `LimiterTable` (per-key limits) and `KeyedRateLimiter` share one eviction rule in two places.
- The quality tool's 8 new findings (iTunes and Spotify clients, from the other session) were left alone on the owner's word.

## Session

About 09:00 to 11:28 EST (2 h 25 min). Gauge: week 82% -> 88% (123 -> 132 of 150 SP); 5-hour 29% -> 73%. Haiku built, Opus judged; 3 corrections.

## Later the same session (to about 12:40 EST)

- Second adversarial pass over the Hosting endpoints: 178 cases over a TestServer (26f806d); 8 findings F-W1..F-W8.
- F-W3..F-W8 fixed and pushed (c75049b): health publish checks, links on the return-URL rule, admin id guard, display names refuse control characters.
- Owner's decision: a forgotten web actor keeps the email as their name (docs/audit-trail.md, a87bbde).
- Open: F-W1 (chunked report over 2 MB answers 500, not 413), F-W2 (a masquerading admin is audited as the viewed account); Deploy and UI not probed; owner to decide whether registration may say "already registered" and whether a disable form with no field re-enables.
