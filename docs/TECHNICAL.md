# DSH Guardian - technical README

Automatic rollback for DeepSeek Harness (DSH): if a newly installed plugin makes
DSH fail to start, the config plane is restored to the last known-good version,
dependencies are reconciled, and DSH is relaunched.

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
| Started by | the desktop shortcut only, and only when you press `4` |
| Stopped by | closing that window (the watcher is bound to it) |

The watcher is started by `dsh-guardian.exe` with `-Resident -ParentPid <own pid>`
and exits by itself when that process disappears, so it can never outlive the
window the user opened.

---

## Files

```
D:\DS\崩溃回退\
  app\                          program (all ASCII names)
    dsh-guardian.exe               launcher + menu (GUI subsystem, winexe)
    dsh-watchdog.ps1               watcher: probe, decide, roll back
    dsh-snapshot.ps1               snapshots, restore, mark-baseline
    launch-diagnostics.cmd         diagnostic launcher (ASCII + CRLF)
    collect-diagnostics.ps1        diagnostic collector (ASCII, hex-encoded Chinese)
    Guardian.exe.cs                C# source for the exe
    build.ps1                      rebuild the exe with csc
    dsh-guardian.ico               icon
  data\                          all runtime state
    mode.json / state.json / last-tick.json / last-known-good.json / runtime.pid
    watchdog.log / events.jsonl / exe-trace.log / dsh-guardian.out.log
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

### 2. `.cmd` must be pure ASCII **and** use CRLF

`cmd.exe` decodes batch files with the **OEM code page** and there is no BOM that
can save you:

- non-ASCII bytes get decoded as garbage and **corrupt the command name**
  (a Chinese byte sequence can even swallow the newline, gluing the next command
  onto a `rem` line)
- bare LF line endings can make `cmd.exe` read several lines as one and split
  words in half

Verify (both must be `0`):

```powershell
$b=[IO.File]::ReadAllBytes('...\launch-diagnostics.cmd')
($b | Where-Object { $_ -gt 127 }).Count                                  # non-ASCII
[regex]::Matches([Text.Encoding]::ASCII.GetString($b),"(?<!`r)`n").Count  # bare LF
```

Chinese names live in the **desktop shortcuts**, which the user sees, so the
ASCII file names are never visible.

### 3. `.cs` and `.md` have no such limit

The C# compiler and editors handle them as UTF-8. The Chinese menu in
`Guardian.exe.cs` is written directly.

---

## How a round works

1. **Probe** - one TCP connect to `127.0.0.1:<Port>` (default 19387).
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
dsh-guardian.exe            open the menu
dsh-guardian.exe logs       show the error log
dsh-guardian.exe baseline   mark the current state as the good baseline
dsh-guardian.exe rollback   roll back now (asks first)
dsh-guardian.exe preview    show the rollback plan, writes nothing
dsh-guardian.exe arm        start watching (runs with this window)
dsh-guardian.exe disarm     stop watching
dsh-guardian.exe shortcut   create the desktop shortcut (manual only)
```

`dsh-snapshot.ps1` also accepts `-Action Create|List|Verify|Restore|Mark-Good`.

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

Double-click the "DSH Guardian 诊断" shortcut, or run `launch-diagnostics.cmd`.
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
- **The port is hard-coded to 19387.** Config default is 3080; a mismatch causes
  false crash detection and repeated relaunches.
- The watcher only runs while its window is open. That is the price of "nothing
  runs unless I open it"; the two cannot both be satisfied.
- Snapshot names are long; the menu trims the `snap-` prefix when displaying.
