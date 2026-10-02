# DSH Guardian

**Automatic rollback for DeepSeek Harness *Desktop* (the Windows app).**

Install a plugin, DSH refuses to start, and you are left editing JSON by hand.
DSH Guardian watches for exactly that: when a newly installed plugin makes DSH
fail to boot, it restores the last known-good configuration, reconciles the
dependencies and relaunches DSH.

> English · [中文](README.zh-CN.md) · [Detailed Chinese guide](docs/GUIDE-zh.md) · [Technical reference](docs/TECHNICAL.md)

![DSH Guardian: click 打基线 before installing a plugin, and it rolls back automatically if the plugin breaks DSH](docs/hero.png)

**Windows** · **DeepSeek Harness Desktop** · **MIT** · **No scheduled task, no autostart**

---

## Quick start — 3 steps

New to DSH and about to install a plugin? This is the whole workflow.

1. **Install DSH Desktop first, and start it once.** Guardian uses it, and it needs a
   first baseline to fall back to. Nothing works before that.
2. **Unzip anywhere permanent and run `app\dsh-guardian.exe`.** No installer, no
   registry, no desktop shortcut unless you ask for one. Keep the folder — if you
   delete it, the tool is gone.
3. **Click 打基线 to record a baseline, then 开关监视 to arm the watcher and leave the
   window open.** Now install your plugin.

Afterwards: DSH boots fine → click 打基线 again (new baseline) → click 开关监视 to
disarm → close the window. That's it.

![The main window](docs/main-window.png)

> **The watcher is a separate process.** Once armed you may close the window and it
> keeps working; click 开关监视 again to stop it. Guardian does not add a scheduled
> task and does not autostart.

| To find out | Click |
|---|---|
| Why DSH crashed, and where | 日志 (the error log — **always look here first**) |
| Roll back now, or pick a future target | 回退 |
| Whether it is actually catching anything | `data\console\`, one log per launch |
| Need to hand over evidence | the 「DSH Guardian 诊断」 desktop shortcut |

### See also

| Question | Key |
|---|---|
| DSH crashed — why, and where? | `1` (error log — always start here) |
| Roll back now, or pick the version to fall back to | `2` |
| Did it actually catch anything? | Look in `data\console\` — one log per launch |

---

## FAQ for first-time users

**Do I need this if I never install plugins?**
No. Guardian only protects against a plugin install that stops DSH from booting. If
you never touch plugins, it does nothing for you.

**Does it work with a command-line DSH install?**
No — DSH Desktop only. The dependency-repair step calls the app's own bundled
`pnpm`, which exists only in the desktop installation. See
[For DSH Desktop](#for-dsh-desktop-the-windows-app) above for the reason.

**What exactly is a "baseline"?**
A copy of the six DSH configuration files at a moment when DSH was working. One
press of `3` adds one; older ones are never overwritten. Auto-rollback uses exactly
one of them — the current target, shown on the 「回退目标」 line.

**What happens if I never click 打基线?**
Guardian refuses to roll back and tells you so. It will not guess. No baseline
means there is nothing to go back to.

**Will it fight with DSH, or slow it down?**
No. Nothing is resident: no scheduled task, no autostart, no background process
when the window is closed. While armed it only does a TCP connect check on an
interval, and relaunches DSH only after a startup crash loop is *proven* — three
launches (default) died within 45 s each, or the relaunch budget was exhausted.

**Is it safe to delete?**
Yes. Delete the folder and nothing is left behind. Your DSH stays as it was —
except for the configuration a rollback actually restored, which is the point.

**Where does it keep things?**
Everything under `data\` next to the program: snapshots, logs, crash evidence,
diagnostic reports. It never reads your sessions or credentials.

---

## For DSH Desktop (the Windows app)

**This tool is for the DeepSeek Harness *desktop application* — the Electron app
you install on Windows — and it targets the `desktop` profile.**

It is *not* a general-purpose DSH tool, and it will not work as one:

| | |
|---|---|
| Target | The `desktop` profile at `%USERPROFILE%\.dsh\profiles\desktop` |
| Platform | Windows only (uses `AllocConsole`, `MessageBoxW`, `WScript.Shell`) |
| Also required | Windows PowerShell 5.1, which ships with Windows |
| Not for | A DSH command-line install, or any profile the desktop app does not manage |

The reason it is desktop-specific is in the dependency step. The DSH CLI
**refuses** to touch a profile that the desktop app manages:

```
dsh plugin --profile desktop install   ->  rejected ("managed exclusively by
                                           the Electron application")
```

So instead of going through the CLI, DSH Guardian calls the **app's own bundled
pnpm** (`resources\runtime\pnpm\dist\pnpm.mjs`, run with the runtime's own
`node.exe`) inside the profile directory. That path only exists in the desktop
installation — which is exactly why this tool is built around it.

If you use a different DSH layout, the configuration plane it protects
(`package.json`, `pnpm-lock.yaml`, `cordis.patch.yml`, `cordis.yml`,
`pnpm-workspace.yaml`, `compatibility.json`) is still the right idea, but the
dependency reconciliation will need its own command.

---

## The problem

Installing a DSH plugin edits configuration files directly. If the plugin is
broken, DSH can **fail at startup**, and DSH has no rollback of its own: you are
left reading `package.json` and `pnpm-lock.yaml` and guessing which plugin did it.

DSH Guardian covers that gap. While you arm it, it watches DSH and on a proven
startup crash loop it automatically:

1. captures the crash evidence (DSH's own stderr),
2. preserves your broken configuration (nothing is lost),
3. restores the last known-good version,
4. reconciles the dependencies,
5. relaunches DSH.

Normally you just see DSH come back up.

## Nothing runs in the background

This is a hard design rule, not an aspiration:

| | |
|---|---|
| Scheduled task | **none** |
| Autostart / Run key | **none** |
| Resident process when idle | **none, zero memory** |
| Started by | opening it, and only when you click 开关监视 |
| Stopped by | clicking 开关监视 again, or closing the window |

The watcher is launched with `-Resident -ParentPid <launcher pid>` and exits by
itself when that process disappears, so it cannot outlive the window that asked
for it.

## The window

The program is a Windows GUI, not a console menu. Eight buttons, one row:

```
当前状态   ● 监视中 · 上次检查 4 分钟前 · 回退目标 20260930-124301-known-good
操作       打基线 | 开关监视 | 回退 | 日志 | 目录 | 刷新 | 放大结果 | 退出
执行结果   (command output, scrolls, resizable)
```

| Button | What it does |
|---|---|
| 打基线 | record the current state as a good version |
| 开关监视 | toggle watching on / off |
| 回退 | pick a version: roll back now, or just set the target |
| 日志 | the error log — **start here after a crash** |
| 目录 | browse `data\`: snapshots, logs, reports |
| 刷新 | re-read the state files |
| 放大结果 | hide the top zones so the output gets the whole window |
| 退出 | close (it asks what to do about the watcher first) |

![The main window](docs/main-window.png)

(The UI itself is Chinese; this is a translation of the labels.)

### Keys

| Key | Name | What it does |
|:---:|---|---|
| `1` | Error log | Why did it crash, where. **Check this first** |
| `2` | Roll back / set target | Lists every kept version; then asks whether to roll back now or only set the future target |
| `3` | Baseline | Records the current state as a good version (adds one, never overwrites) |
| `4` | Auto-check | On/off. Only watches while **on** |
| `0` | Quit | Exits (watching stops too) |

## Install

1. **Download the packaged ZIP** (ready to run: exe, sources and docs) from
   **[Releases](https://github.com/weiming88888/DSH-Guardian/releases/latest)**.
   It extracts to a single `DSH-Guardian-<version>\` folder - put that folder
   somewhere permanent, e.g. `D:\DS\`.
2. Double-click `app\dsh-guardian.exe`. A Chinese menu opens in a console
   window.
3. Click 打基线 once to record the current working state as your first baseline.

There is no installer, no registry write and **no shortcut is created for you** -
the program never touches your desktop on its own. If you want one, run
`dsh-guardian.exe shortcut` once. Delete the folder and the tool is gone
completely.

## Use

```
Before installing a plugin : open it, click 开关监视, KEEP THE WINDOW OPEN
After  installing a plugin : DSH boots fine -> click 打基线 to re-baseline
                             -> click 开关监视 to disarm -> close the window
```

That is the whole workflow. The rest of the time there is nothing to do, because
nothing is running.

> **The window *is* the switch.** Window open = watching; window closed = stopped.
> The watcher is started with `-ParentPid <this window's pid>` and exits by itself
> when the window closes, so nothing can keep running behind your back. That is
> expected, not a freeze.

### What happens when DSH crashes

With the window open and auto-check armed:

1. The port probe fails. DSH is relaunched.
2. If it keeps failing, a startup crash loop is *proven* - by either of two
   independent signals: `-CrashLoopThreshold` launches (default 3) died within
   `-StartGraceSeconds` (default 45 s), **or** the relaunch budget
   `-BootRetryBudget` (default 3) is exhausted and DSH never answered.
3. Crash evidence is captured: the child's stdout+stderr, per launch, under
   `data\console\`.
4. Your broken configuration is preserved to
   `data\snapshots\pre-restore-<stamp>\`.
5. The last known-good snapshot is restored.
6. `node_modules` is reconciled with the restored lockfile.
7. DSH is relaunched.

### Safety guards

| Guard | Effect |
|---|---|
| No baseline yet | Refuses to roll back and says so explicitly, instead of guessing |
| Same configuration already rolled back | **Never rolls back the same configuration twice** |
| Rollback did not help | Stops relaunching and says so once, rather than looping |
| `-MaxAutoRollbacks` (default 2) | Hard cap per crash episode |

**Timing, measured in a sandbox** (this is the part that surprises people): a launch
is not counted as short-lived until the whole `-BootWindowSeconds` window (default
90 s) has passed, because a slow-but-fine boot must not be mistaken for a crash.
So a DSH that dies instantly is still reported as `DSH starting` for that window,
and auto-rollback needs one round *after* the window to fire. The watcher now logs
"still inside the boot window: no verdict yet" every 30 s so it never looks hung.
If that wait is too long for you, lower `-BootWindowSeconds` (e.g. `10`) — it is
safe as long as your DSH never legitimately takes that long to bind its port.

Every message is deduplicated by signature, so a watcher that runs for hours does
not drown the log in repeats.

## Baselines

**You can keep many.** Each press of `3` adds a snapshot; older ones are never
deleted.

**But automatic rollback uses exactly one of them** - the *current target*, shown
on the 「回退目标」 line of the menu. It normally points at the newest baseline.

**When would you change it?** Say you installed plugin A, then plugin B, and B
broke DSH:

| Target points at | Rolling back gives you |
|---|---|
| Newest baseline (before B) | **A stays** - you only wanted B gone |
| An older baseline (before A) | **A and B are both gone** |

Pressing `2` lists them all and marks the active one, then asks what to do:

| Button | Effect |
|---|---|
| 【Yes】 | Roll back to it **now** |
| 【No】 | Don't roll back; just make it the target for **future** crashes |
| 【Cancel】 | Do nothing |

### Three ways to cancel

| Where | How | Result |
|---|---|---|
| Version list | Type `0`, or just press Enter | Nothing happens |
| Action dialog | Click 【Cancel】 | Nothing happens |
| Action dialog | Click 【No】 | No rollback; only sets the future target |

**Manual rollback is always reversible**: before anything is written, the current
configuration is copied to `data\snapshots\pre-restore-<stamp>\`, and you can roll
back to it later like any other snapshot.

### The `-2` suffix

Snapshot names have one-second resolution (`snap-20260930-134944-known-good`). If
you take two baselines **inside the same second**, the second becomes
`...-known-good-2`. This is deliberate: an existing snapshot is **never
overwritten**. The same applies to `pre-restore-` backups. Normal use (more than a
second apart) never produces a suffix.

## Command line

```
dsh-guardian.exe            open the menu
dsh-guardian.exe logs       show the error log
dsh-guardian.exe baseline   mark the current state as the good baseline
dsh-guardian.exe rollback   pick a version, then roll back or set the target
dsh-guardian.exe preview    show the rollback plan, writes nothing
dsh-guardian.exe arm        start watching (runs with this window)
dsh-guardian.exe disarm     stop watching
dsh-guardian.exe on | off   same as arm | disarm
dsh-guardian.exe shortcut   create the desktop shortcut (manual only)
```

`dsh-snapshot.ps1` additionally accepts
`-Action Create | List | Verify | Restore | Mark-Good | Promote`, plus `-DryRun`
and `-Force`. **`-Action` defaults to `Create`**, so a bare run takes a snapshot
rather than listing them; pass `-Action List` to look without writing.
`Restore` without `-Force` only prints what it would do.

## Reporting a problem

Double-click the **「DSH Guardian 诊断」** desktop shortcut (it runs `dsh-guardian.exe diagnostics`). It collects
everything needed to diagnose a fault and writes a Chinese report to
`data\诊断报告\诊断报告-<date>-<time>.txt`, then opens both the report and its
folder.

The report contains: program type (PE subsystem), script encoding check, mode,
watcher liveness, scheduled-task and autostart presence, DSH profile state, port
probes, all logs, crash evidence and the snapshot list. **It contains no
passwords, keys or account information.** Attach it to an
[issue](https://github.com/weiming88888/DSH-Guardian/issues).

## Layout

```
app\                               program (ASCII file names on purpose)
  dsh-guardian.exe                   launcher + menu (GUI subsystem)
  dsh-watchdog.ps1                   probe / decide / roll back
  dsh-snapshot.ps1                   snapshots, restore, promote
  launch-diagnostics.cmd             diagnostic launcher
  collect-diagnostics.ps1            diagnostic collector
  Guardian.exe.cs                    C# source for the exe
  build.ps1                          rebuild with csc
  dsh-guardian-app.ico                   icon
docs\
  GUIDE-zh.md                        full user guide (Chinese)
  TECHNICAL.md                       technical reference (English)
data\                              created at runtime, NOT in this repo
  mode.json / state.json / last-tick.json / last-known-good.json / runtime.pid
  watchdog.log / events.jsonl / exe-trace.log / child-stderr.log / menu-error.log
  launch-captured-<stamp>-<pid>.cmd / .vbs   generated hidden-launch wrappers
  console\                           captured stdout+stderr, one log per launch
  snapshots\                         snap-<stamp>-<label>\ and pre-restore-<stamp>\
  诊断报告\                           generated diagnostic reports
_archive\<date>-pre-qc\            local backup taken before a quality pass
```

Only the files listed for `app\` and `docs\` ship in the release ZIP. `data\`,
`_archive\` and `DSH-Guardian-<version>.zip` are working-tree only — `data\` holds
your local paths and plugin list, which is why `.gitignore` excludes it.
`data\console\` stays empty until a launch actually goes through the wrapper, so
an empty folder there is normal on a healthy install.

Three kinds of file are deliberately left out of the ZIP because they only serve the
GitHub page: `docs\hero.png` and `docs\social-preview.png` (promotional art) with their
`.html` render sources, and `docs\ASSETS.md` (repo maintenance notes). Excluding them
takes the archive from 951 KB to 417 KB.

## Rebuilding

Only the .NET Framework `csc.exe` that ships with Windows is needed - **no SDK**:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "app\build.ps1"
```

## Requirements

- **DeepSeek Harness Desktop** (the Windows app) installed, with a `desktop`
  profile - not a CLI-only installation
- Windows (uses `AllocConsole`, `MessageBoxW`, `WScript.Shell`)
- Windows PowerShell 5.1 (ships with Windows)

## Notes and limits

- **The DSH port is detected automatically** — it is the port the running DSH
  process is actually listening on, not a guess. DSH's own default is 3080, but
  the port really in use is set outside the config files this tool is allowed to
  read, so assuming one made the probe report a crash that was not happening (and
  then "recover" a perfectly healthy DSH). Detection order: `-Port` if you pass
  it, else the live listening port, else the one cached in `data\port.txt` from
  the last successful detection (this is what carries the watcher across a crash),
  else 3080. If you pin `-Port`, detection is skipped entirely.
- The watcher only runs while its window is open. That is the price of "nothing
  runs unless I open it"; the two cannot both be satisfied.
- The probe is a **TCP connect** to `127.0.0.1:<port>` (default timeout 3 s), not
  an HTTP request. Granularity is therefore "is the port answering", not
  in-process transient errors.
- Only these six configuration files are ever touched: `package.json`,
  `pnpm-lock.yaml`, `cordis.patch.yml`, `cordis.yml`, `pnpm-workspace.yaml`,
  `compatibility.json`. Your sessions and credentials are never read.
  `node_modules` is never deleted, only reconciled.
- `dsh plugin --profile <name> install` is rejected by the DSH CLI for an
  Electron-managed profile, so the app's own bundled pnpm is invoked directly
  instead (`resources\runtime\pnpm\dist\pnpm.mjs` with the runtime's
  `node.exe`), running `install --no-frozen-lockfile` in the profile directory.
- The resident watcher has a lifetime cap (`-MaxResidentMinutes`, default 240) so
  a forgotten window cannot watch forever.

## Two Windows encoding traps

Both cost real debugging time. They are documented in full in
[docs/TECHNICAL.md](docs/TECHNICAL.md); the short version:

- **`.ps1` must be pure ASCII** unless it has a UTF-8 BOM. Windows PowerShell 5.1
  reads BOM-less scripts with the system ANSI code page, so a Chinese literal in
  the source corrupts parsing. Chinese output is stored as UTF-8 hex and decoded
  at runtime.
- **Generated `.cmd` and `.vbs` files must be written in the encoding their reader
  honours** — *not* ASCII. The relaunch wrapper embeds your `data\` path, so
  writing it as ASCII turns `D:\DS\崩溃回退\data` into `D:\DS\????\data`: `cmd.exe`
  logs to a directory nobody reads and `wscript.exe` reports "cannot find the
  path", while the empty `data\console\` is supposed to hold the crash evidence.
  The `.cmd` therefore gets the console code page (ANSI normally, UTF-8+BOM under
  `chcp 65001`; both cases measured), and the `.vbs` is written as UTF-16 LE with
  a BOM, which WScript honours whatever the code page is. Both generators now
  refuse to run rather than build a wrapper from a path containing `?`.

## License

MIT. See [LICENSE](LICENSE).
