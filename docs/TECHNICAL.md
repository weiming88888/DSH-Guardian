# DSH Guardian - technical README

Automatic rollback for **DeepSeek Harness Desktop** (the Windows Electron app):
if a newly installed plugin makes DSH fail to start, the config plane is restored
to the last known-good version, dependencies are reconciled, and DSH is
relaunched.

**Scope:** the `desktop` profile only (`%USERPROFILE%\.dsh\profiles\desktop`),
on Windows. This is not a general-purpose DSH tool. Dependency reconciliation
deliberately bypasses the DSH CLI, which rejects any profile the desktop app
manages:

```
dsh plugin --profile desktop install   ->  rejected ("managed exclusively by
                                           the Electron application")
```

so the app's own bundled pnpm (`resources\runtime\pnpm\dist\pnpm.mjs` with the
runtime's `node.exe`) is invoked inside the profile directory instead. That path
exists only in a desktop installation.

This file is the **technical** reference. The user-facing guide (Chinese) is
`GUIDE-zh.md` in the same folder.

---

## Nothing runs on its own

This is a hard requirement, not a nicety:

| | |
|---|---|
| Scheduled task | **none** (removed; a per-minute task contradicts zero idle cost) |
| Autostart / Run key | **none** |
| Resident process when idle | **none** |
| Started by | the desktop shortcut or `app\dsh-guardian.exe`, and only when you click 开关监视 |
| Stopped by | closing that window (the watcher is bound to it) |

The watcher is started by `dsh-guardian.exe` with `-Resident -ParentPid <own pid>`
and exits by itself when that process disappears, so it can never outlive the
window the user opened.

---

## Files

```
D:\DS\崩溃回退\
  app\                          program (all ASCII names)
    dsh-guardian.exe               GUI build: main window; forwards CLI verbs (winexe)
    dsh-watchdog.ps1               watcher: probe, decide, roll back
    dsh-snapshot.ps1               snapshots, restore, mark-baseline
    launch-diagnostics.cmd         diagnostic launcher (ASCII + CRLF); the desktop shortcut now runs dsh-guardian.exe diagnostics instead
    collect-diagnostics.ps1        diagnostic collector (ASCII, hex-encoded Chinese)
    Guardian.exe.cs                C# source for the exe
    build.ps1                      rebuild the exe with csc
    dsh-guardian-app.ico               icon
  data\                          all runtime state
    mode.json / state.json / last-tick.json / last-known-good.json / runtime.pid
    watchdog.log / events.jsonl / exe-trace.log / gui-clicks.log / gui-window.json
    console\                       captured stdout+stderr from DSH launches
    诊断报告\                       diagnostic reports (one per run, timestamped)
    snapshots\                     snap-<stamp>-<label>\ and pre-restore-<stamp>\
  docs\
    GUIDE-zh.md                    user guide (Chinese)
    README.md                      this file
```

---

## Hard constraints when editing

Two encoding traps have caused real breakage twice. Both are properties of
Windows, not of this project.

### 1. `.ps1` must be pure ASCII unless it has a UTF-8 BOM

Windows PowerShell 5.1 reads a BOM-less `.ps1` using the **system ANSI code page**
(GBK on Simplified Chinese Windows). A Chinese literal in the source is therefore
decoded wrongly and the whole script fails to parse.

Verify (must print `0`):

```powershell
$b=[IO.File]::ReadAllBytes('...\dsh-watchdog.ps1'); ($b | Where-Object { $_ -gt 127 }).Count
```

When Chinese output is needed, `collect-diagnostics.ps1` stores it as UTF-8 hex
in the `$ZH` table and decodes it at runtime with `T`. Keep that pattern.

### 2. Every *generated* file must be written in the encoding its consumer honours

The rule is **not** "generated files must be pure ASCII". That belief caused a
silent data-loss bug, found twice:

The relaunch path generates two files that both carry the `data\` path — a `.cmd`
wrapper and a `.vbs` launcher. Written with `-Encoding ascii` under a Chinese
directory (`D:\DS\<Chinese>\data\`), every non-ASCII character became `?`:

```
.cmd  -> cmd.exe wrote D:\DS\????\data\console\console-<stamp>.log
.vbs  -> wscript.exe reported "The system cannot find the path specified."
```

Real `data\console\` stayed empty, so the stderr evidence `Get-CrashEvidence`
calls *the most important evidence* was never captured. Nothing failed loudly,
because the misdirected log wrote fine.

Measured matrix for the `.cmd` (this is why the encoding is chosen at runtime, not
hard-coded):

| Console code page | ANSI, no BOM | UTF-8 **with** BOM |
|---|---|---|
| 936 (Chinese Windows default) | works | fails (BOM read as part of the name) |
| 65001 (UTF-8) | fails (GBK bytes are not valid UTF-8) | works |

So each generated file gets the encoding its own reader understands:

| File | Consumer | Written as |
|---|---|---|
| `launch-captured-*.cmd`, `post-rollback-install-*.cmd` | `cmd.exe` | console code page (ANSI, or UTF-8+BOM under 65001 — chosen by `Get-CmdEncoding`) |
| `launch-captured-*.vbs` | `wscript.exe` | **UTF-16 LE with BOM** — honoured regardless of code page |
| `mode.json`, `state.json`, `*.jsonl`, reports | PowerShell / the exe | UTF-8 (unchanged, always worked) |
| `runtime.pid` | PowerShell | ASCII (digits only) |

The wrapper is also built with `cmd.exe /d` so an AutoRun command cannot change
the code page behind our back, and both generators **refuse to run** rather than
build a wrapper from a path containing `?`.

**Rule of thumb: a generated file that carries a path must be written in the
encoding the consumer uses to read it, and verified by reading it back.**

Verify a real capture, end to end (not by inspection):

```powershell
# 1. The wrappers decode with the right encoding and no '?' appears:
Get-Content '...\data\launch-captured-*.cmd' -Encoding Default
Get-Content '...\data\launch-captured-*.vbs' -Encoding Unicode
# 2. CRLF only (bare LF must be 0):
$b=[IO.File]::ReadAllBytes('...\app\launch-diagnostics.cmd')
[regex]::Matches([Text.Encoding]::ASCII.GetString($b),"(?<!`r)`n").Count
```

Chinese names live in the **desktop shortcuts**, which the user sees, so the
ASCII *file* names are never visible.

### 3. `.cs` and `.md` have no such limit

The C# compiler and editors handle them as UTF-8. The Chinese strings in
`Guardian.exe.cs` (the console build's interactive screen) are written directly.

---

## How a round works

1. **Probe** - one TCP connect to `127.0.0.1:<Port>`, where `<Port>` is resolved
   every round (`Resolve-DshPort`): explicit `-Port` wins, else the live listening
   port of a running DSH, else `data\port.txt`, else 3080. See below.
2. **Healthy** - record the tick, clear failure state, done. Nothing else is
   touched: no process scan, no config fingerprint, no log reads.
3. **Unhealthy** - fingerprint the config plane, then decide:
   - inside the boot window of a launch we made -> report "starting" and do not
     relaunch (this is what prevents duplicate instances)
   - otherwise count the failure; a crash loop is proven when either
     `-CrashLoopThreshold` launches died within `-StartGraceSeconds`, or the
     relaunch budget `-BootRetryBudget` was exhausted.
4. **Rollback** (only with `-AutoRollback`, and only when the config plane has
   moved since the last rollback): capture evidence, preserve the broken config
   to `pre-restore-<stamp>`, restore the snapshot, reconcile `node_modules`,
   relaunch.

### Guards that stop it making things worse

| Guard | Effect |
|---|---|
| no `last-known-good.json` | refuses to roll back, logs `ROLLBACK-IMPOSSIBLE` |
| config unchanged since last rollback | **never rolls back the same configuration twice** |
| rollback did not help | `relaunchStopped` - stops relaunching, says so once |
| `-MaxAutoRollbacks` (default 2) | hard cap per episode |

Log noise is deduplicated by signature, so a long-running watcher does not fill
the log with the same message every round.

---

## Dependency reconciliation

`dsh plugin --profile desktop install` is **not usable**: the CLI refuses that
profile ("managed exclusively by the Electron application"). The app's own
bundled pnpm is invoked directly instead:

```
<app>\resources\runtime\primary-runtime\dependencies\node\bin\node.exe
<app>\resources\runtime\pnpm\dist\pnpm.mjs  install --no-frozen-lockfile
```

run in the profile directory. Measured at ~0.7 s and a no-op when the lockfile is
already satisfied. `--no-frozen-lockfile` is deliberate: it repairs drift instead
of failing on it.

---

## CLI

```
dsh-guardian.exe            open the main window (no arguments)
dsh-guardian.exe logs       show the error log
dsh-guardian.exe baseline   mark the current state as the good baseline
dsh-guardian.exe rollback   pick a version, then roll back / set target
dsh-guardian.exe preview    show the rollback plan, writes nothing
dsh-guardian.exe arm        start watching (runs with this window)
dsh-guardian.exe disarm     stop watching
dsh-guardian.exe on | off   same as arm | disarm
dsh-guardian.exe shortcut   create the desktop shortcut (manual only)
dsh-guardian.exe diagnostics  collect a diagnostic report (as the shortcut does)
dsh-guardian.exe help | ?   the same text
```

Both builds accept these verbs: the GUI build forwards any recognised verb to
`dsh-guardian-console.exe` next to it, so `dsh-guardian.exe shortcut` works as
documented and there is only one implementation of each verb.

`dsh-snapshot.ps1` also accepts
`-Action Create|List|Verify|Restore|Mark-Good|Promote|Delete`.

---

## Rebuilding

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "app\build.ps1"
```

`/target:winexe` is intentional. `/target:exe` produces a console (CUI) program,
and anything that launches a CUI program - Task Scheduler, a parent process -
allocates a **visible console window**. That was the intermittent terminal popup.

---

## Diagnostics

Double-click the "DSH Guardian 诊断" shortcut (it runs `dsh-guardian.exe diagnostics`),
or run `launch-diagnostics.cmd` directly.
It writes a Chinese report into **`data\诊断报告\诊断报告-<stamp>.txt`** - inside
the tool folder, never onto the desktop - and opens both the report and its
folder. Contents: program type (PE subsystem), script ASCII purity, mode, watcher
liveness, scheduled-task and autostart presence, DSH profile state, port probes,
all logs, crash evidence and the snapshot list. Read-only apart from that report.

Note that the report folder name and file name are Chinese, built at runtime by
decoding `$ZH` entries - the script itself stays ASCII, as required above.

---

## Known limits

- Granularity is "is the port answering", not in-process transient errors.
- **The port is detected, not configured** (`Resolve-DshPort`). Order: explicit
  `-Port`, then the live listening port of a DSH process, then `data\port.txt`,
  then 3080. Detection needs the process to exist; when DSH is down the cached
  file is what keeps the watcher pointed at the right port, so deleting
  `data\port.txt` while DSH is already dead falls back to 3080 and can cause one
  round of false crash detection. `-Port` remains the way to pin it.
- The watcher only runs while its window is open. That is the price of "nothing
  runs unless I open it"; the two cannot both be satisfied.
- Snapshot names are long; the version-selection dialog trims the `snap-` prefix
  when displaying.
