# AGENTS.md

## Project identity

This repository is `notipet`.

notipet is a Windows tray daemon that makes a noise when an AI coding agent needs the user. Claude Code and Codex CLI call it through hooks; it plays a level-appropriate sound and shows a tray notification. It exists because the moment an agent blocks on a permission prompt — or finishes a forty-minute run — is easy to miss while doing something else.

It is a **public repository** (MIT). Documentation is Korean prose with English identifiers. Never commit anything from a maintainer machine: tokens, local absolute paths, user names, or other projects' names. Examples and test fixtures use neutral paths such as `C:\src\notipet`.

## Scope boundaries

- **Local only, for now.** Phases 1 and 2 contain no outbound network code at all: `app/` has no `HttpClient` anywhere, and the CLI's is hard-pinned to `127.0.0.1`. Two exceptions, both approved by the maintainer. **Updates** (`app/Update/Updater.cs`, Velopack against this repository's GitHub Releases): a check runs only when the user clicks, or once a day if they turned on `updates.autoCheck` (off by default, settable only in the settings UI); downloading and installing is always a separate click. **Mobile push** (phase 3) is gated behind `channels.mobile_push.outboundNetworkApproved`, likewise settable only from the settings UI, never over the API. Do not add other outbound calls without explicit maintainer approval.
- **Do not merge this into [`quota-scope`](https://github.com/EvanHexX/quota-scope).** It is a separate product with tagged releases; adding a listening socket and third-party egress to it would change its threat model. Code was copied from it, not linked. See `docs/PROJECT_MAP.md`.
- Do not perform UI framework rewrites, namespace changes, or channel expansions as part of an unrelated fix.

## Current implementation

Three projects:

- Core: `core/Notipet.Core.csproj` — plain `net10.0`, **no Windows**. The wire contract (it compiles `shared/`), rules, dispatcher, history, the loopback HTTP API, settings, paths, and the alarm loop / sound resolution. Platform parts come in through seams: `ISoundEngine`, `ISoundCatalog` (`SoundResolver.Catalog`), `INotificationChannel`, and delegates on `ApiContext` (alarm registry, engine info, presence, focus assist). CA1416 is an **error** in this project: a Windows-only API in core fails the build. Its checks are listed once in `core/CoreSelfTests.cs` and every front end runs them.
- Daemon: `app/Notipet.App.csproj` — .NET 10, WinUI 3 / Windows App SDK 1.8, unpackaged, self-contained, x64. Everything Windows: sound engines (MediaPlayer, winmm), the registry sound catalog, tray, windows, presence (idle/lock/Focus Assist), autostart, URL-scheme lookup. References core.
- CLI: `cli/Notipet.Cli.csproj` — .NET 10 console, NativeAOT. Deliberately a separate exe: hooks sit on the agent's critical path, and every hook invocation pays the startup. Measured on this machine (7 runs, `Start-Process -Wait`, so both figures include ~30 ms of harness overhead): CLI `--help` 48 ms median, daemon `--help` 68 ms median — and the daemon's `--help` returns before any WindowsAppSDK initialisation, so that is the best case for it. The console and exit-code behaviour is the other half of the argument: a `WinExe` has neither.
- Entry point: `app/Program.cs` — hand-written `Main` (`DISABLE_XAML_GENERATED_MAIN`) so `--self-test` and `--test-sound` run before any XAML initialisation.
- Lifecycle: `app/TrayController.cs` owns the tray icon, the HTTP server, the sound engines and `runtime.json`.
- Installer and updates: Velopack (`scripts/pack.ps1`, `app/Update/`). Package id `NotipetApp`, installed to `%LOCALAPPDATA%\NotipetApp\current` - a different folder from the app data in `%LOCALAPPDATA%\notipet`, so uninstalling keeps settings. `VelopackApp...Run()` is the first thing in `Main`. See `docs/modules/installer.md`.
- Codex plugin: `integrations/codex/plugin` (hooks + the Codex skill), listed by `.agents/plugins/marketplace.json`. Its `version` follows the app's; its `SKILL.md` is generated; `scripts/check-codex-plugin.ps1` (run by `pack.ps1`) enforces both. **Read `docs/modules/codex_plugin.md` before touching it.**

### Shared sources

`shared/*.cs` is compiled into the **core** (and through it the daemon) and into the **CLI**, via `Compile Include` in those two csproj files. Editing one of those files changes both executables. The CLI links the files rather than referencing core so it stays a dependency-free NativeAOT exe.

Where new code goes: if it does not need Windows, it goes in `core/`. If it does, put an interface or delegate in core and the implementation in `app/`.

- Wire contract: `shared/Wire.cs`
- Hook payload mapping: `shared/PayloadMapper.cs`, `shared/AgentEvents.cs`
- Levels: `shared/NotificationLevel.cs`
- JSON source-gen context: `shared/NotipetJson.cs` — **mandatory**, not an optimisation. The CLI is NativeAOT, where reflection-based `System.Text.Json` trim-warns and fails at runtime.

## Required workflow

1. Read this file and `docs/PROJECT_MAP.md`.
2. Read the module doc for the area you are touching (`docs/modules/`).
3. Inspect the specific source files.
4. Summarise the intended change and keep it narrowly scoped.

Prefer small, reviewable diffs.

## Build and verification

```powershell
dotnet build notipet.slnx
.\app\bin\Debug\net10.0-windows10.0.19041.0\win-x64\NotipetTray.exe --self-test
.\cli\bin\Debug\net10.0\win-x64\notipet.exe --self-test
```

The daemon's `--self-test` runs the core's checks (`CoreSelfTests.All`) plus the Windows ones. `dotnet build core\Notipet.Core.csproj` on its own is the quick way to confirm core is still platform-neutral.

Run the executables directly rather than through `dotnet run`. The daemon is a `WinExe`; under `dotnet run` in some shells its console output is swallowed, and the self-test report is the point.

When the maintainer has the daemon running it locks `app\bin\`. Build to a scratch output instead of killing it:

```powershell
dotnet build app\Notipet.App.csproj -p:BaseOutputPath=$env:TEMP/notipet-build/
```

`--self-test` must stay **silent and headless**: no tray icon, no window, no sound. Audible verification is `--test-sound [level]`. The HTTP check inside it binds a real ephemeral port — that is deliberate, it is the highest-value check in the suite.

If you run inside an agent sandbox, check the daemon (`runtime.json`, `notipet ping`, `scripts/*.ps1`, `publish.ps1 -Restart`) from a shell outside it. A sandboxed shell can see a stale copy of `%LOCALAPPDATA%
otipet`, and a CLI run there may auto-launch a daemon whose `runtime.json` the user's hooks never see.

For tray, sound, or hook changes, report a manual smoke checklist (`docs/regression.md`, automated through step 8 by `scripts/smoke.ps1`) and do not launch the GUI yourself unless asked. If a command fails, report the exact failure. Do not claim verification that was not actually run.

## Invariants worth not breaking

These each exist because of a specific failure, and each is commented at its site.

- Nothing creates, plays or disposes a `MediaPlayer` on the UI thread. Done there, its waits pump messages, a re-entrant `WM_DEVICECHANGE` built a second player inside the first one's `Dispose`, and the UI thread hung for good while the API kept answering. Device changes go through `SoundService.RequestProbe`; UI-thread callers use `Task.Run`; `UiWatchdog` restarts the daemon if the UI thread stops answering for a minute.
- The daemon keeps `runtime.json` naming itself: every 30 s it checks both runtime files and rewrites them if they are missing or name another instance (logged to `daemon.log`). It once ran for hours while the file named a dead predecessor and every hook thought it was down. On shutdown, stop that timer before deleting the files.
- `TrayIconHost._wndProc` is an instance field on purpose. It is a GC root for a native callback; without it the process dies by `FailFast`.
- Balloons must set `NIIF_NOSOUND`. Otherwise the shell plays its own chime on top of the sound channel and every notification double-sounds.
- The alarm duration cap is enforced in code (`AppSettings.AbsoluteMaxAlarmSeconds`), independently of settings, so a misconfigured hook firing `until_ack` in a loop cannot invent an endless alarm. The one exception is `sound.maxDurationSec = 0` (`UnlimitedAlarmSeconds`), which the user picks in Settings and which applies to `until_ack` alarms only: the cap was silencing the alarm while they were away, which is the case it exists for. Requests still cannot raise a duration, and at-desk shortening still cuts it to 30 s.
- Suppression returns **HTTP 200** with `accepted:false`, never a 4xx. Quiet hours are not a client error, and a hook that sees a non-2xx may change the agent's behaviour.
- The CLI exits **0 even when delivery fails**, unless `--strict`. A notification daemon being down must never alter what Claude Code or Codex does.
- Local channels are dispatched before remote ones, so a slow or failing push service structurally cannot delay the sound.
- `PayloadMapper` never throws and never rejects. Agent payload shapes are external contracts; an unknown event becomes an `info` notification carrying the raw event name. (`UserPromptSubmit` is the one known event that notifies nothing: it only ends what is over.)
- A notification's `open` link is a URI handed to the shell, never a command line, and only http(s) to loopback or a scheme in `settings.links.allowedSchemes`, which the API cannot change; `OpenLinks` refuses file, ms-*, search-ms, shell and script schemes even when listed. A refused link is dropped with a warning; the notification still goes.
- The Codex plugin's `hooks/hooks.json` does not change lightly. Codex trusts each hook by a hash of its definition, so any edit - even a status message - silences notipet for every user until they trust the hooks again. Logic that may change goes in `scripts/notipet-hook.cmd`, which the hash does not cover. The manifest stays at `.codex-plugin/plugin.json`: a root `plugin.json` switches Codex to a format that loads no hooks.
- `/v1/resolve` stops only what it names. No selector, or an agent alone, is a 400 - never "all", which is `/v1/ack`. Hooks end only moments that are over by definition (a turn's permission prompt once the turn has stopped); what an agent sent through the skill is ended only by the agent. Resolving what the user already stopped is a 200 with zeros.

## Safety

Never commit secrets, tokens (`runtime.json` holds one), local absolute paths from a maintainer machine, build outputs, or telemetry/analytics/remote logging.
