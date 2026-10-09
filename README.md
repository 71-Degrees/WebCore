# WebCore

The shared foundations of the 71 Degrees web apps, kept in one private repository so every app stays in step. Apps use it as the git submodule `tools/WebCore` and reference its projects directly.

| Project | What it holds |
|---|---|
| `src/WebCore` | Caching building blocks (keyed and snapshot caches with hit/miss counts), operations (release notes from git, server status and the deploy guard), and what grows from there |
| `src/WebCore.Deploy` | The deploy tool engine (added next): build, stage, promote, rollback, verify, perf, notices, App Settings, the one-click console |

See `CLAUDE.md` for the engineering standard, and `docs/` for the guides that apply to every app.

## Using it in an app

```
git submodule add https://github.com/71-Degrees/WebCore.git tools/WebCore
git submodule update --init
```

then add `<ProjectReference Include="..\..\tools\WebCore\src\WebCore\WebCore.csproj" />` to the app's Core project. Upgrade with `git submodule update --remote tools/WebCore`, build, test, commit the new pointer.

## Working on it

`dotnet build WebCore.slnx` and `dotnet test WebCore.slnx`. Keep it app-agnostic: anything specific to one product stays in that product. A change here reaches an app only when the app moves its submodule pointer.
