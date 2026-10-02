# DSH Guardian

**Automatic rollback for DeepSeek Harness *Desktop* — the Windows app.**

Install a plugin, DSH stops booting, and you are left editing JSON by hand.
DSH Guardian watches for exactly that: when a plugin breaks the boot, it puts the
last known-good configuration back, reconciles the dependencies and relaunches DSH.

> English · [中文](README.zh-CN.md) · [Full guide (Chinese)](docs/GUIDE-zh.md) · [The window, explained](docs/GUI.md) · [How to read the logs](docs/LOG.md) · [Technical reference](docs/TECHNICAL.md)

![DSH Guardian: click 打基线 before installing a plugin; if the plugin breaks DSH it rolls back automatically](docs/hero.png)

**Windows** · **DeepSeek Harness Desktop** · **MIT** · **no scheduled task, no autostart**

---

## Is this for you?

| | |
|---|---|
| **You need it if** | you install DSH plugins and a bad one could leave DSH unable to start |
| **You don't** | if you never install plugins — it only covers that one failure |
| **Not for** | a command-line DSH install. Desktop app only, `desktop` profile |

DSH has no rollback of its own. A plugin edits `package.json` and `pnpm-lock.yaml`
directly, and when it goes wrong you cannot even reach the UI.

## Quick start — 3 steps

1. **Install DSH Desktop and start it once.** Guardian needs a working state to copy.
2. **Unzip somewhere permanent and run `app\dsh-guardian.exe`.**
   No installer, no registry writes, no shortcut is created for you.
3. **Click 打基线** to record a baseline, then **开关监视** to start watching —
   **and leave the window open.** Now install your plugin.

Done with the plugin and DSH still boots? Click 打基线 once more (a fresh baseline),
click 开关监视 to stop watching, close the window. That is the whole workflow.

![The main window](docs/main-window.png)

## The buttons

Eight of them, in one row. The interface is Chinese; this is what each one means.

| Button | What it does |
|---|---|
| 打基线 | record the current state as a good version |
| 开关监视 | start / stop watching |
| 回退 | pick a version: roll back now, or only set the future target |
| 日志 | the error log — **start here after a crash** |
| 目录 | browse `data\`: snapshots, logs, diagnostic reports |
| 刷新 | re-read the status files |
| 放大结果 | collapse the top zones so the output fills the window |
| 退出 | exit (it warns you first if watching is still on) |

The status line is the one to read: `○ 未监视` not watching · `● 监视中` watching ·
`◐ 已开启，监视器启动中…` armed, watcher starting.

## The window is the switch

A hard design rule, not a preference:

| | |
|---|---|
| Scheduled task | **none** |
| Autostart / registry Run key | **none** |
| Background process once the window is closed | **none** |
| Watching starts | when you click 开关监视 |
| Watching stops | when you click 开关监视 again, or close the window |

The watcher is started with `-Resident -ParentPid <this window's pid>`: when the
window's process goes away the watcher exits too, and the log records
`resident: exiting (launcher closed)`.

**So closing the window stops monitoring** — it never keeps running behind your back.
Worth knowing before you close it and walk away to install a plugin.

## What happens when a plugin breaks DSH

With the window open and watching on:

1. The port probe fails; DSH is relaunched.
2. If it keeps failing, a **startup crash loop is proven** — by either of two
   independent signals: `-CrashLoopThreshold` launches (default 3) each died within
   `-StartGraceSeconds` (default 45 s), **or** the relaunch budget `-BootRetryBudget`
   (default 3) is exhausted and DSH never answered.
3. Crash evidence is captured: each launch's stdout + stderr, under `data\console\`.
4. Your broken configuration is preserved to `data\snapshots\pre-restore-<stamp>\` —
   nothing is lost.
5. The last known-good snapshot is restored.
6. `node_modules` is reconciled with the restored lockfile.
7. DSH is relaunched.

Normally you just see DSH come back up.

## It refuses more often than it acts

| Guard | Effect |
|---|---|
| No baseline yet | refuses to roll back and says so, instead of guessing |
| Same configuration as a previous rollback | **never rolls back the same configuration twice** |
| The rollback did not help | stops relaunching and reports it once, instead of looping |
| `-MaxAutoRollbacks` (default 2) | hard cap per crash episode |

## Timing: why it waits about 90 seconds

A launch is not counted as short-lived until the whole `-BootWindowSeconds` window
(default 90 s) has passed — a slow but healthy boot must not be mistaken for a crash.
So a DSH that dies instantly still logs `DSH starting` for that window, and the
rollback fires one round after it. The watcher writes
`still inside the boot window: no verdict yet` every 30 s, so it never looks hung.

Lower `-BootWindowSeconds` (e.g. `10`) if that is too slow for you. It is safe as
long as your DSH never legitimately needs longer than that to bind its port.

## Baselines

A baseline is a copy of the six DSH configuration files — `package.json`,
`pnpm-lock.yaml`, `cordis.patch.yml`, `cordis.yml`, `pnpm-workspace.yaml`,
`compatibility.json` — taken while DSH was working.

**Keep as many as you like.** Each click of 打基线 adds one, and older ones are never
overwritten. **Automatic rollback uses exactly one of them**, the *current target*
shown on the 回退目标 line. Where it points decides how far back a crash takes you:

| Target points at | A rollback leaves you with |
|---|---|
| Newest baseline (before plugin B) | **A stays** — you only wanted B gone |
| An older baseline (before plugin A) | **A and B are both gone** |

Click 回退 to list them all and pick one:

![The version picker](docs/versions.png)

The list marks the active target, and you then choose 现在就回退 (roll back now),
只设为以后的目标 (only set the future target) or 取消 (do nothing). Before anything
is written the current configuration is copied to
`data\snapshots\pre-restore-<stamp>\`, so a manual rollback is always reversible.

> Snapshot names have one-second resolution; two baselines inside the same second
> give the second one a `-2` suffix. Existing snapshots are never overwritten.

## Install and requirements

1. Download the ZIP from **[Releases](https://github.com/weiming88888/DSH-Guardian/releases/latest)**
   (exe + sources + docs) and extract it somewhere permanent.
2. Run `app\dsh-guardian.exe`.

| | |
|---|---|
| Requires | **DeepSeek Harness Desktop** installed, with a `desktop` profile — not a CLI-only install |
| Platform | **Windows** |
| Also | Windows PowerShell 5.1 (ships with Windows). No SDK, no installer, no registry writes |
| Shortcut | **not created automatically.** Run `dsh-guardian.exe shortcut` once if you want one |
| Uninstall | delete the folder |

## Command line (optional)

The verbs live in `dsh-guardian-console.exe`; `dsh-guardian.exe` forwards them to it.

```
logs         show the error log
baseline     mark the current state as a good baseline
rollback     pick a version, then roll back or set the target
preview      show the rollback plan, writes nothing
arm / on     start watching          disarm / off   stop watching
shortcut     create the desktop shortcut (manual only)
diagnostics  collect a diagnostic report
probe        probe the DSH port and report
help         usage
```

`dsh-snapshot.ps1` takes
`-Action Create | List | Verify | Restore | Mark-Good | Promote | Delete`, plus
`-DryRun` and `-Force`. `-Action` defaults to `Create`; `Restore` without `-Force`
only prints what it would do.

## Something went wrong?

1. **日志** in the window — the error log, including DSH's own output where possible.
2. **Diagnostics** — the 「DSH Guardian 诊断」 desktop shortcut, or
   `app\dsh-guardian.exe diagnostics`.

![Browsing data\](docs/browser.png)

The report goes to `data\诊断报告\诊断报告-<date>-<time>.txt` and covers program
state, watcher liveness, scheduled-task and autostart presence, DSH profile state,
port probes, all logs, crash evidence and the snapshot list.
**It contains no passwords, keys or account information.** Attach it to an
[issue](https://github.com/weiming88888/DSH-Guardian/issues).

## What ships

```
app\                            the program (ASCII file names on purpose)
  dsh-guardian.exe                GUI — double-click this
  dsh-guardian-console.exe        the command-line verbs above
  dsh-watchdog.ps1                probe / decide / roll back
  dsh-snapshot.ps1                snapshots, restore, promote
  collect-diagnostics.ps1         diagnostic collector
  make-shortcut.ps1               writes the desktop shortcut
  launch-diagnostics.cmd          diagnostic launcher
  Guardian.manifest               DPI awareness + asInvoker
  Guardian.Win.cs / Guardian.exe.cs   sources
  build.ps1                       rebuild with csc
  dsh-guardian-app.ico            icon
docs\                           user guides and screenshots
data\                           created at runtime, never shipped
  snapshots\   console\   诊断报告\   plus the state and log files
```

The release ZIP (418 KB) contains `app\` and `docs\` only. `data\` holds your local
paths and plugin list, which is why it is git-ignored. Promotional art and its HTML
render sources stay in the repository but are left out of the ZIP.

## Rebuilding

Only the .NET Framework `csc.exe` that ships with Windows — no SDK:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "app\build.ps1"
```

## Notes and limits

- **The DSH port is detected, not assumed.** Order: `-Port` → the port the live DSH
  process is actually listening on → the one cached in `data\port.txt` from the last
  successful detection (this is what carries the watcher across a crash) → 3080.
  Assuming a port made the probe report a crash that was not happening, and then
  "recover" a perfectly healthy DSH.
- **Watching only runs while the window is open.** That is the price of "nothing runs
  unless I open it"; the two cannot both be satisfied.
- The probe is a **TCP connect** to `127.0.0.1:<port>` (default 3 s), not an HTTP
  request. The granularity is "is the port answering", not in-process transient errors.
- Only the six configuration files above are ever written. Your sessions and
  credentials are never read, and `node_modules` is never deleted — only reconciled.
- `dsh plugin --profile <name> install` is rejected for an Electron-managed profile,
  so the app's own bundled pnpm is called directly
  (`resources\runtime\pnpm\dist\pnpm.mjs` with the runtime's `node.exe`), running
  `install --no-frozen-lockfile` in the profile directory.
- The resident watcher has a lifetime cap (`-MaxResidentMinutes`, default 240), so a
  forgotten window cannot watch forever.
- **Contributors:** `.ps1` files must stay pure ASCII (or carry a UTF-8 BOM).
  PowerShell 5.1 reads BOM-less scripts with the ANSI code page, so a non-ASCII
  literal corrupts the parse; Chinese output is stored as escaped hex and decoded at
  runtime. See [docs/TECHNICAL.md](docs/TECHNICAL.md).

## License

MIT. See [LICENSE](LICENSE).
