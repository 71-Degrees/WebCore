# The deploy tool

Every 71 Degrees app deploys with the same engine (`src/WebCore.Deploy`). An app has a launcher project, `tools/Deploy`, whose `Program.cs` is one line:

```csharp
return await WebCore.Deploy.DeployApp.RunAsync(args);   // or with new DeployExtensions { VerifyChecks = ... } for checks only this app needs
```

and a settings file, `deploy/deploy.json`. Run it from the app's repository root as `dotnet run --project tools/Deploy -- <command>`.

## Commands

| Command | What it does |
|---|---|
| `status <stage\|prod>` | The running build, when it started, and whether anything live (a game, a session) would be interrupted by a restart |
| `stage` / `prod` | `stage` builds (tests first) and deploys to staging; `prod` promotes exactly the build that is on staging. `--wait [minutes]` waits for live sessions to finish, `--interrupt N` deploys over N of them, `--offline-page` shows visitors a "being updated" page meanwhile, `--whatif` does everything but the upload |
| `rollback <stage\|prod>` | Redeploys the previous build kept on this machine |
| `verify <stage\|prod>` | A read-only smoke test of a live site: version, configured pages, static pages, version stamps, closed pages, the app's own checks, errors since the deploy. It also runs after every deploy |
| `perf <stage\|prod>` | Times the key pages, checks static-file caching, compares with the previous build. Also runs after every deploy |
| `report prod` | Production telemetry per build (needs a Log Analytics workspace) |
| `notice` / `notice-clear` | A downtime banner (and an optional email) through the app's maintenance endpoint |
| `setting <stage\|prod> Name[=Value]` | Reads or changes one Azure App Setting; a change restarts the site, so it waits for live sessions, reads the value back and verifies |
| `console` | A local page (http://127.0.0.1:5098) with a link for each command, so nothing needs typing |
| `check [--tests-only]` | The tests, a compile of the web projects, the script checks and the code-smell ratchet: what the pre-push hook runs |
| `install-hooks` | Installs that pre-push hook |
| `notes`, `minify`, `build`, `backfill-tags` | Release notes from git, CSS/JS minification, a build without deploying, tagging old deploys |

State (the builds that went out, for promotion and rollback) is kept per app under `%LOCALAPPDATA%\<App>Deploy`.

A production deploy is a release: it is tagged `release-yyyy-MM-dd-HHmm` in git, which groups the release notes. An app with only a `stage` target (a test site and no production yet) has its staging deploys tagged instead. A staging build that never answers is replaced by the one that was running; production is never changed without a person.

## deploy/deploy.json

Only `app`, `webProject` and `targets` are required. Comments and trailing commas are allowed.

```jsonc
{
  "app": "Harbor",                          // names the state folder
  "webProject": "src/Harbor.Web",           // published; release-notes.json is written beside it
  "runtime": "win-x86",
  "tests": [ "tests/Harbor.Core.Tests" ],   // default: every folder under tests/ with a project
  "changedTests": "dotnet run --project tools/CodeQuality/src/Quality -- quick --since origin/master --no-smells --no-open",   // optional: the push check runs just the tests about the change
  "buildRunsAllTests": true,                // optional: a build still runs every project in "tests" (for an app with no nightly or background run)
  "compile": [ "src/Harbor.Web", "src/Harbor.Worker" ],   // default: the web project
  "scriptTypeCheck": "npx --no-install tsc -p src/Harbor.Web/tsconfig.json",
  "scriptLint": "npx --no-install eslint src/Harbor.Web/wwwroot/js",
  "indexNow": { "appSettings": "src/Harbor.Web/appsettings.json", "key": "Seo:IndexNowKey" },
  "endpoints": { "version": "/version", "health": "/healthz", "status": "/api/admin/status", "maintenance": "/api/admin/maintenance" },
  "targets": {
    "prod":  { "app": "harbor", "group": "rg-harbor", "url": "https://www.harbor.example", "keyEnv": "HARBOR_IMPORT_KEY", "workspace": "<log analytics workspace id>" },
    "stage": { "app": "harbor-stage", "group": "rg-harbor", "url": "https://staging.harbor.example", "keyEnv": "HARBOR_IMPORT_KEY" }
  },
  "perfPages": [ "/", "/healthz" ],
  "alertQuiet": { "targets": [ "prod" ], "rules": [ "alert-harbor-prod-down" ], "quietMinutes": 25, "settleMinutes": 11 },   // optional: no "site is down" email for a restart the tool caused
  "liveSessions": true,                     // false for an app with nothing live: no status endpoint needed, deploys never wait
  "verify": {
    "pages": [ { "path": "/", "name": "home page", "contains": [ "Harbor" ] }, { "path": "/about" } ],
    "staticPages": [ "/", "/terms", "/privacy" ],       // must render with no live connection
    "interactivePages": [ "/app" ],                     // must still render for one
    "closedPages": [ "/admin/users" ],                  // a logged-out visitor is redirected or asked to log in
    "webSocketPath": "/api/connection-test/ws"
  },
  "consoleActions": [                                   // extra buttons; {target} and {live} are filled in per target
    { "id": "{target}-flag-on", "group": "Flags", "label": "Turn the flag on for {target}", "args": [ "setting", "{target}", "Feature__Flag=On" ], "confirm": "Turn the flag on for {live}?", "targets": [ "stage", "prod" ] }
  ]
}
```

The console page can only ask for a button by its id, so what a browser tab can make the machine run is exactly the buttons listed here and the standard ones. Nothing is typed into it.

## What the app must provide

- `GET /version` returning `{ "version": "..." }` (the build's commit), and `GET /healthz`.
- Optionally `GET /api/admin/status` (API-key protected) returning `{ version, environment, startedAt, games: [ { sessionCode, quizTitle, phase, players, isTest } ] }` so deploys never restart a server with people in a live session; an app with nothing live returns an empty list.
  `WebCore.Operations.ServerStatus` is that shape. Without the endpoint, deploys need `--no-status-check`.
- Optionally the maintenance endpoint for `notice`.
- The API key in the environment variable each target's `keyEnv` names (user scope on Windows).

## Changing the engine

Work in the WebCore repository, keep it app-agnostic (anything specific to one app belongs in its `deploy.json` or its launcher's extensions), keep the wire formats deployed apps already use unchanged, and move each app's submodule pointer when it is ready for the change.

## Quieting the "site is down" alert during a deploy

A deploy restarts the site on purpose, which trips the uptime alert and emails the owner. With `alertQuiet` in `deploy.json`, `stage`/`prod`, `rollback` and `setting` create an Azure Monitor **alert processing rule** (`<App>-deploy-quiet`, scoped to the target's
resource group and the alert rules you name) that mutes those alerts' notifications for `quietMinutes`. Once the new build is steady the window is cut to `settleMinutes` (long enough for the availability checks that failed during the restart
to leave the alert's evaluation window); if the deploy fails the rule is removed at once so a real outage alerts. The window always ends by itself, so a tool that dies halfway cannot leave monitoring off. `alerts quiet|resume <target>` does it by hand.
It needs `az login` with permission to write alert processing rules, and it is off until the setting exists. Alerts about a real outage during the quiet window are muted too, which is why the window is short.
