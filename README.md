# DSH Guardian

Automatic rollback for **DeepSeek Harness (DSH)**.

Install a plugin, DSH refuses to start, and you are left editing JSON by hand.
DSH Guardian watches for exactly that: when a newly installed plugin makes DSH
fail to boot, it restores the last known-good configuration, reconciles the
dependencies and relaunches DSH.

> English · [中文](README.zh-CN.md) · [详细中文说明](docs/GUIDE-zh.md) · [Technical reference](docs/TECHNICAL.md)

```
   DSH Guardian  ·  DSH 崩溃自动回退
==========================================================
   1. 查看错误日志      —— 崩了先看这里，含 DSH 原始报错
   2. 回退 / 切换目标   —— 选一个版本：现在回退，或只作以后的目标
   3. 打基线            —— 把当前状态记为一个“好版本”
   4. 自动检查: 关      —— 按此键开关自动回退
   0. 退出
```

## Nothing runs in the background

This is a hard design rule, not a nicety:

| | |
|---|---|
| Scheduled task | **none** |
| Autostart / Run key | **none** |
| Resident process when idle | **none** |
| Started by | the shortcut, and only when you press `4` |
| Stopped by | closing that window (the watcher is bound to it) |

The watcher is launched with `-Resident -ParentPid <launcher pid>` and exits by
itself when that process disappears, so it can never outlive the window you
opened. Close the window and the tool is genuinely gone.

## Install

1. **Download the packaged ZIP** (ready to run: exe, sources and docs) from
   **[Releases](https://github.com/weiming88888/DSH-Guardian/releases/latest)**,
   and extract it somewhere permanent, e.g. `D:\DS\崩溃回退`.
2. Double-click `app\dsh-guardian.exe`. A Chinese menu opens in a console
   window and a desktop shortcut is created automatically.
3. Press `3` once to record the current working state as your first baseline.

There is no installer and no registry write. Deleting the folder removes the
tool completely.

## Use

```
Before installing a plugin : open it, press 4 to arm, KEEP THE WINDOW OPEN
After  installing a plugin : DSH boots fine -> press 3 to re-baseline
                             -> press 4 to disarm -> close the window
```

That is the whole workflow. The window *is* the switch: while it is open the
watcher runs; close it and everything stops.

### What happens when DSH crashes

With the window open and auto-check armed:

1. Probe fails -> relaunch DSH -> fails again, up to `BootRetryBudget` times.
2. Startup crash loop is proven.
3. Crash evidence is captured (the child's stdout+stderr).
4. The broken config is preserved to `data\snapshots\pre-restore-<stamp>\`.
5. The last known-good snapshot is restored.
6. `node_modules` is reconciled with the restored lockfile.
7. DSH is relaunched.

### Safety guards

| Guard | Effect |
|---|---|
| no baseline yet | refuses to roll back, logs `ROLLBACK-IMPOSSIBLE` |
| config unchanged since last rollback | **never rolls back the same configuration twice** |
| rollback did not help | stops relaunching and says so once, instead of looping |
| `-MaxAutoRollbacks` (default 2) | hard cap per crash episode |

Manual rollback is always reversible: the current config is copied aside before
anything is written, and you can roll back to *any* saved snapshot, not just the
newest one.

## Command line

```
dsh-guardian.exe            open the menu
dsh-guardian.exe logs       show the error log
dsh-guardian.exe baseline   mark the current state as the good baseline
dsh-guardian.exe rollback   pick a version, then roll back or set the target
dsh-guardian.exe preview    show the rollback plan, writes nothing
dsh-guardian.exe arm        start watching (runs with this window)
dsh-guardian.exe disarm     stop watching
dsh-guardian.exe shortcut   recreate the desktop shortcut
```

## Layout

```
app\                              program (ASCII file names on purpose)
  dsh-guardian.exe                   launcher + menu (GUI subsystem)
  dsh-watchdog.ps1                   probe / decide / roll back
  dsh-snapshot.ps1                   snapshots, restore, promote
  launch-diagnostics.cmd             diagnostic launcher
  collect-diagnostics.ps1            diagnostic collector
  Guardian.exe.cs                    C# source for the exe
  build.ps1                          rebuild with csc
  dsh-guardian.ico                   icon
docs\
  GUIDE-zh.md                        user guide (Chinese, start here)
  README.md                          this file
data\                              created at runtime, NOT in this repo
```

## Rebuilding

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "app\build.ps1"
```

Requires only the .NET Framework `csc.exe` that ships with Windows. No SDK.

## Requirements

- Windows (uses `AllocConsole`, `MessageBoxW`, `WScript.Shell`)
- Windows PowerShell 5.1 (ships with Windows)
- DeepSeek Harness installed, with a `desktop` profile

## Notes and limits

- **The DSH port is assumed to be 19387.** DSH's own default is 3080. A
  mismatched port makes the probe report a crash that is not happening, so check
  this before arming.
- The watcher only runs while its window is open. That is the price of "nothing
  runs unless I open it"; the two cannot both be satisfied.
- Only the config plane is touched: `package.json`, `pnpm-lock.yaml`,
  `cordis.patch.yml`, `cordis.yml`, `pnpm-workspace.yaml`, `compatibility.json`.
  Your sessions and credentials are never read. `node_modules` is never deleted,
  only reconciled.
- `dsh plugin --profile <name> install` is rejected by the DSH CLI for an
  Electron-managed profile; the app's own bundled pnpm is invoked directly
  instead.

## Two Windows encoding traps

Both cost real debugging time; they are documented in full in
`docs\TECHNICAL.md`, and the short version is:

- **`.ps1` must be pure ASCII** unless it has a UTF-8 BOM. PowerShell 5.1 reads
  BOM-less scripts with the system ANSI code page, so a Chinese literal in the
  source corrupts parsing. Chinese output is stored as UTF-8 hex and decoded at
  runtime.
- **`.cmd` must be pure ASCII *and* CRLF.** `cmd.exe` decodes batch files with
  the OEM code page, and a bare LF can make it read several lines as one.

## License

MIT. See [LICENSE](LICENSE).
