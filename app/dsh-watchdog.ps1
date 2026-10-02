<#
.SYNOPSIS
  DSH Guardian watchdog - out-of-process health monitor. Watch only; never rolls back.

.DESCRIPTION
  Why out of process: DSH ships no process-level watchdog. Its own source states that
  unhandled signals, SIGKILL, fatal OOM, native crashes and power loss
  "need an external supervisor". DSH does not restart itself either: installFailLoud
  writes one labelled diagnostic to stderr and calls exit(1).

  So the real value here is capturing that stderr. When DSH is started from a desktop
  shortcut, stderr has no receiver and the crash evidence simply evaporates. This
  watchdog launches DSH through a generated .cmd wrapper that redirects stdout+stderr
  to a file, which is the only reliable source of crash evidence.

  Grounded facts (verified on this machine, not assumed):
    - The real profile is named 'desktop': %USERPROFILE%\.dsh\profiles\desktop
    - Config plane files: package.json, pnpm-lock.yaml, cordis.patch.yml,
      cordis.yml, pnpm-workspace.yaml, compatibility.json
    - Plugin operation logs live in <profile>\.plugin-manager\logs\<op>\pnpm.log
      (~/.dsh/logs does not exist)
    - The web port comes from cordis.yml: port: ctx.webStartup.port ?? 3080
      so the default of 3080 in config is NOT necessarily the running port.
    - Get-CimInstance Win32_Process is denied on this machine; this script
      degrades to Get-Process name matching for forensics.

  NOTE: this file is deliberately pure ASCII. Windows PowerShell 5.1 reads
  BOM-less .ps1 files using the system ANSI code page, which corrupts non-ASCII
  literals and breaks parsing. Keep it ASCII.

.PARAMETER Resident
  Loop inside one process instead of relying on a per-minute scheduled task.
  Started by dsh-guardian.exe when the user arms the guard from the menu, and
  tied to that window: it stops when the window closes. There is no autostart of
  any kind, by design.
#>
[CmdletBinding()]
param(
    # 0 = auto-detect the port DSH actually listens on (recommended, and the
    # default). Pass a number to pin it and skip detection entirely.
    [int]$Port = 0,
    # Where state, logs and snapshots live. The launcher always passes this
    # explicitly; the default only matters when the script is run by hand.
    # It is resolved after the param block below, because $PSScriptRoot is EMPTY
    # for some -File invocations under Windows PowerShell 5.1 and an empty value
    # here once made the whole script exit silently during parameter binding.
    [string]$DataDir = 'data',
    [string]$DshHome = (Join-Path $env:USERPROFILE '.dsh'),
    [string]$ProfileName = 'desktop',
    [string]$LaunchCommand = '',
    [string[]]$ProcessMatch = @('dsh', 'DeepSeek Harness'),
    [int]$ProbeTimeoutMs = 3000,
    [int]$StartGraceSeconds = 45,
    # How long a freshly launched DSH is allowed to take before it must answer.
    # DSH can take tens of seconds to bind its port; without this window the
    # watchdog would read "still starting" as "crashed" and keep spawning
    # duplicate instances.
    [int]$BootWindowSeconds = 90,
    # How many launches to allow within one failure episode before declaring
    # that DSH cannot come up at all (failStreak also acts as this counter).
    [int]$BootRetryBudget = 3,
    [int]$CrashLoopThreshold = 3,
    [int]$TailLines = 60,
    # Auto-rollback: when a startup crash loop is proven (a launch actually died
    # within StartGraceSeconds, repeatedly) AND the profile config plane differs
    # from the last known-good snapshot, restore that snapshot and relaunch.
    # Requires a pointer file written by dsh-snapshot.ps1 -Action Create.
    [switch]$AutoRollback,
    # 'paused' means: do nothing except record that the round ran. The guardian
    # is armed only while a plugin is being installed, not around the clock.
    # Persisted in the mode file, which -Pause / -Resume / -Mode write.
    [ValidateSet('', 'auto', 'paused')]
    [string]$Mode = '',
    [switch]$Pause,
    [switch]$Resume,
    # Hard cap on automatic rescues per crash episode. After this many the
    # watchdog stops acting and only reports, so it can never loop.
    [int]$MaxAutoRollbacks = 2,
    # After a rollback the dependency tree is reconciled with the restored
    # lockfile. 0 disables that step.
    [int]$InstallTimeoutSeconds = 240,
    [switch]$NoStart,
    # Unattended runs (started by the launcher, which is a GUI-subsystem program
    # with no console) must not write to a console. Send everything to the log.
    [switch]$Silent,
    # Resident mode: keep ONE long-lived process and loop inside it, instead of
    # paying a PowerShell cold start (~180 ms) per round. Started on demand by
    # dsh-guardian.exe when the user arms the guard, and bound to that process
    # via -ParentPid, so it cannot outlive the window that asked for it.
    [switch]$Resident,
    # The launcher's pid. When it exits (the user closes the window) the resident
    # watcher stops too, so nothing keeps running behind the user's back.
    [int]$ParentPid = 0,
    [int]$IntervalSeconds = 55,
    [int]$MaxResidentMinutes = 240
)

$ErrorActionPreference = 'Stop'

# $MyInvocation.MyCommand.Path is reliable where $PSScriptRoot is not, so the
# script's own directory is derived from it.
$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    try { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path } catch { }
}
if (-not $scriptDir) { $scriptDir = (Get-Location).Path }
$BaseDir = $scriptDir

# A bare name is made absolute beside this script's parent (the data folder).
if (-not [System.IO.Path]::IsPathRooted($DataDir)) {
    $DataDir = Join-Path (Split-Path -Parent $scriptDir) $DataDir
}

# Recorded so process matching can exclude this watchdog's own invocation.
$script:SelfPath = $PSCommandPath

$ProfileDir = Join-Path (Join-Path $DshHome 'profiles') $ProfileName
$ConsoleDir = Join-Path $DataDir 'console'
$modeFile = Join-Path $DataDir 'mode.json'
$runtimeFile = Join-Path $DataDir 'runtime.pid'
$portFile = Join-Path $DataDir 'port.txt'

# ------------------------------------------------------- single instance guard
# Only a resident runtime can pile up, so one pid file keeps it to a single
# process: a live pid means a watcher is already running and the caller exits.
function Test-PidAlive {
    param([int]$ProcessId)
    if ($ProcessId -le 0) { return $false }
    try { return $null -ne (Get-Process -Id $ProcessId -ErrorAction Stop) } catch { return $false }
}

function Get-RunningRuntimePid {
    if (-not (Test-Path -LiteralPath $runtimeFile)) { return 0 }
    try {
        $val = (Get-Content -LiteralPath $runtimeFile -Raw -Encoding utf8).Trim()
        $pidValue = 0
        if (-not [int]::TryParse($val, [ref]$pidValue)) { return 0 }
        if (Test-PidAlive -ProcessId $pidValue) { return $pidValue }
    } catch { }
    return 0
}

# The config plane, defined exactly once. dsh-snapshot.ps1 uses the same list;
# this script previously repeated it inline in three places.
$ConfigPlane = @('package.json', 'pnpm-lock.yaml', 'cordis.patch.yml',
                 'cordis.yml', 'pnpm-workspace.yaml', 'compatibility.json')

# ---------------------------------------------------------------- arm / disarm
# Done before any directory creation or probing so that pausing is instant and
# a paused round costs one file read and one 60-byte write.
function Write-Mode {
    param([string]$State)
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    $rec = [ordered]@{ mode = $State; changedAt = (Get-Date).ToString('o') }
    ($rec | ConvertTo-Json) | Set-Content -LiteralPath $modeFile -Encoding utf8
}

function Read-Mode {
    if (Test-Path -LiteralPath $modeFile) {
        try { return (Get-Content -LiteralPath $modeFile -Raw -Encoding utf8 | ConvertFrom-Json).mode } catch { }
    }
    return $null
}

if ($Pause) {
    Write-Mode -State 'paused'
    Write-Host 'DSH Guardian: PAUSED. Rounds will do nothing until resumed.'
    return
}
if ($Resume) {
    Write-Mode -State 'auto'
    Write-Host 'DSH Guardian: ARMED. Auto-rollback is active.'
    return
}

$effectiveMode = if ($Mode) { $Mode } elseif ($env:DSH_GUARDIAN_MODE) { $env:DSH_GUARDIAN_MODE } else { Read-Mode }
if (-not $effectiveMode) { $effectiveMode = 'auto' }   # unset: behave as before

# An explicit -Mode is authoritative and must be persisted: the launcher passes
# it so the watcher arms itself on its first round, which removes the race where
# a separately written mode file was not yet visible to this process.
if ($Mode -and $Mode -ne (Read-Mode)) { Write-Mode -State $Mode }

if ($effectiveMode -eq 'paused' -and -not $Resident) {
    # Deliberately still writes the tick: it is the only proof the task fires,
    # and the pause/resume state must stay observable.
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    $tick = '{{"at":"{0}","paused":true,"pid":{1}}}' -f (Get-Date).ToString('o'), $PID
    Set-Content -LiteralPath (Join-Path $DataDir 'last-tick.json') -Value $tick -Encoding utf8
    return
}

New-Item -ItemType Directory -Force -Path $DataDir, $ConsoleDir | Out-Null
$stateFile = Join-Path $DataDir 'state.json'
$eventLog = Join-Path $DataDir 'events.jsonl'
$reportLog = Join-Path $DataDir 'watchdog.log'
$pointerFile = Join-Path $DataDir 'last-known-good.json'
$snapshotScript = Join-Path $PSScriptRoot 'dsh-snapshot.ps1'

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $line = '{0} [{1}] {2}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $Level, $Message
    Add-Content -LiteralPath $reportLog -Value $line -Encoding utf8
    # Unattended rounds have no console; writing to one would block or throw.
    if (-not $Silent) { Write-Host $line }
}

# Reads a property that may not exist on a state object deserialized from an
# older state.json, where a missing key would otherwise throw.
function Get-Prop {
    param($State, [string]$Name, $Default = $null)
    if ($State -and $State.PSObject.Properties[$Name]) { return $State.$Name }
    return $Default
}

# Writes a property whether or not it already exists.
function Set-Prop {
    param($State, [string]$Name, $Value)
    $State | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
}

function Write-Event {
    param([string]$Kind, [hashtable]$Fields)
    $rec = [ordered]@{ ts = (Get-Date).ToString('o'); kind = $Kind }
    foreach ($k in $Fields.Keys) { $rec[$k] = $Fields[$k] }
    ($rec | ConvertTo-Json -Compress -Depth 8) | Add-Content -LiteralPath $eventLog -Encoding utf8
}

function Get-GuardState {
    if (Test-Path -LiteralPath $stateFile) {
        try { return (Get-Content -LiteralPath $stateFile -Raw -Encoding utf8 | ConvertFrom-Json) } catch { }
    }
    return $null
}

function Save-GuardState {
    param($State)
    ($State | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $stateFile -Encoding utf8
}

# TCP connect probe: lighter than HTTP and cheap enough to repeat every round.
function Test-PortAlive {
    param([string]$TargetHost, [int]$TargetPort, [int]$TimeoutMs)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $client.BeginConnect($TargetHost, $TargetPort, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne($TimeoutMs, $false)) { return $false }
        $client.EndConnect($iar)
        return $true
    } catch {
        return $false
    } finally {
        $client.Close()
    }
}

# ------------------------------------------------------------- port detection
# Why auto-detect: DSH's own default is 3080, but the port actually in use is
# whatever the running app chose -- this machine runs 19387, set outside the
# five config files we are allowed to read (the only trace of it in the profile
# is a plugin's webUrl). A wrong port makes every probe report a crash that is
# not happening, and then the "crash loop" logic restarts a perfectly healthy
# DSH. So the port is discovered from the process that owns it, not assumed.
#
# Order matters:
#   1. -Port, when the user pinned one.
#   2. The live listening port of a running DSH -- authoritative, because a
#      socket that is accepting connections cannot be wrong.
#   3. data\port.txt, written on every successful detection: after a crash DSH
#      is gone, so 2 is unavailable exactly when the watcher needs it most.
#   4. 3080, DSH's documented default.
function Get-DshListeningPort {
    param([string[]]$Patterns)
    $pids = @()
    try {
        $rows = Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
            $cl = $_.CommandLine
            if (-not $cl) { return $false }
            if ($cl -like "*$script:SelfPath*") { return $false }
            if ($cl -like '*dsh-watchdog.ps1*') { return $false }
            if ($cl -like '*dsh-snapshot.ps1*') { return $false }
            foreach ($p in $Patterns) { if ($cl -like "*$p*") { return $true } }
            return $false
        }
        if ($rows) { $pids = @($rows | ForEach-Object { $_.ProcessId }) }
    } catch { }

    if ($pids.Count -eq 0) {
        try { $pids = @(Get-Process -Name 'DeepSeek Harness' -ErrorAction Stop | ForEach-Object { $_.Id }) } catch { }
    }
    if ($pids.Count -eq 0) { return 0 }

    try {
        $conns = Get-NetTCPConnection -State Listen -ErrorAction Stop |
            Where-Object { $pids -contains $_.OwningProcess -and $_.LocalAddress -in @('127.0.0.1', '0.0.0.0', '::', '::1') }
        if ($conns) {
            # Lowest wins: the app's IPC/dev ports are ephemeral high numbers,
            # and the web UI is the one the user configured.
            return [int](($conns | Measure-Object -Property LocalPort -Minimum).Minimum)
        }
    } catch { }

    try {
        $conns = Get-NetTCPConnection -State Listen -ErrorAction Stop |
            Where-Object { $pids -contains $_.OwningProcess }
        if ($conns) { return [int](($conns | Measure-Object -Property LocalPort -Minimum).Minimum) }
    } catch { }
    return 0
}

function Resolve-DshPort {
    param([int]$Explicit, [string[]]$Patterns)

    if ($Explicit -gt 0) {
        return [pscustomobject]@{ Port = $Explicit; Source = 'parameter' }
    }

    $live = Get-DshListeningPort -Patterns $Patterns
    if ($live -gt 0) {
        try { Set-Content -LiteralPath $portFile -Value $live -Encoding ascii } catch { }
        return [pscustomobject]@{ Port = $live; Source = 'live-process' }
    }

    if (Test-Path -LiteralPath $portFile) {
        try {
            $cached = 0
            if ([int]::TryParse((Get-Content -LiteralPath $portFile -Raw).Trim(), [ref]$cached) -and $cached -gt 0) {
                return [pscustomobject]@{ Port = $cached; Source = 'cached' }
            }
        } catch { }
    }

    return [pscustomobject]@{ Port = 3080; Source = 'default' }
}

# Process forensics. Win32_Process CommandLine is denied on this machine, so this
# degrades to Get-Process name matching. Forensics only; liveness is the port probe.
# The returned object carries a Degraded flag: in degraded mode the process count
# cannot be trusted (the watchdog's own powershell command line contains "dsh"),
# so callers must not use it as a liveness signal.
function Get-DshProcesses {
    param([string[]]$Patterns)
    try {
        $rows = Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
            $cl = $_.CommandLine
            if (-not $cl) { return $false }
            # Never count this watchdog's own process, or the launcher that
            # started it: their command lines contain this script's path, so a
            # naive substring match would report a live DSH that does not exist.
            if ($cl -like "*$script:SelfPath*") { return $false }
            if ($cl -like '*dsh-watchdog.ps1*') { return $false }
            if ($cl -like '*dsh-snapshot.ps1*') { return $false }
            $hit = $false
            foreach ($p in $Patterns) { if ($cl -like "*$p*") { $hit = $true; break } }
            return $hit
        }
        $list = @()
        if ($rows) {
            $list = @($rows | Select-Object @{n = 'ProcessId'; e = { $_.ProcessId } }, Name, @{n = 'CommandLine'; e = { $_.CommandLine } })
        }
        return [pscustomobject]@{ Processes = $list; Degraded = $false }
    } catch {
        Write-Log ("Win32_Process unreadable ({0}); degrading to name-only matching" -f $_.Exception.Message.Trim()) 'WARN'
    }
    # Exact name match only. A wildcard such as *dsh* would match this watchdog's
    # own powershell.exe invocation and make "is it running" always true.
    $acc = @()
    $names = @('dsh', 'DeepSeek Harness', 'deepseek-harness')
    foreach ($n in $names) {
        $acc += Get-Process -Name $n -ErrorAction SilentlyContinue |
            Select-Object @{n = 'ProcessId'; e = { $_.Id } }, @{n = 'Name'; e = { $_.ProcessName } }, @{n = 'CommandLine'; e = { '(degraded: unavailable)' } }
    }
    return [pscustomobject]@{ Processes = @($acc); Degraded = $true }
}

# Config-plane fingerprint: existence + size + mtime only. Never reads content,
# never modifies anything.
function Get-ConfigFingerprint {
    $items = @()
    foreach ($n in $ConfigPlane) {
        $p = Join-Path $ProfileDir $n
        # Reading length/mtime can still fail after Test-Path passed: the file can
        # be replaced in between, or be locked by the installer while it writes.
        # $ErrorActionPreference is 'Stop', so an unguarded throw here would kill
        # the whole round -- the watcher would silently stop watching.
        try {
            if (Test-Path -LiteralPath $p) {
                $f = Get-Item -LiteralPath $p -ErrorAction Stop
                $items += [ordered]@{ file = $n; bytes = $f.Length; mtime = $f.LastWriteTime.ToString('o') }
            } else {
                $items += [ordered]@{ file = $n; bytes = $null; mtime = $null }
            }
        } catch {
            $items += [ordered]@{ file = $n; bytes = $null; mtime = $null }
        }
    }
    return $items
}

function Get-EvidenceTail {
    param([int]$Lines, [int]$SinceSeconds = 600)
    $cut = (Get-Date).AddSeconds(-1 * $SinceSeconds)
    $found = @()

    $pmLogs = Join-Path $ProfileDir '.plugin-manager\logs'
    if (Test-Path -LiteralPath $pmLogs) {
        $recent = Get-ChildItem -LiteralPath $pmLogs -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -gt $cut } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 3
        foreach ($f in $recent) {
            $found += [ordered]@{
                path = $f.FullName
                tail = ((Get-Content -LiteralPath $f.FullName -Tail $Lines -ErrorAction SilentlyContinue) -join [Environment]::NewLine)
            }
        }
    }

    # The wrapper-captured stderr log is the most important evidence.
    $consoleLog = Get-ChildItem -LiteralPath $ConsoleDir -File -Filter 'console-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($consoleLog) {
        $found += [ordered]@{
            path  = $consoleLog.FullName
            bytes = $consoleLog.Length
            tail  = ((Get-Content -LiteralPath $consoleLog.FullName -Tail $Lines -ErrorAction SilentlyContinue) -join [Environment]::NewLine)
        }
    }

    return $found
}

function Resolve-LaunchCommand {
    param([string]$Explicit)
    if ($Explicit) { return [pscustomobject]@{ Command = $Explicit; Source = 'parameter' } }

    $sh = New-Object -ComObject WScript.Shell
    $dirs = @(
        (Join-Path $env:USERPROFILE 'Desktop'),
        'C:\Users\Public\Desktop',
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
        'C:\ProgramData\Microsoft\Windows\Start Menu\Programs'
    )
    foreach ($d in $dirs) {
        if (-not (Test-Path -LiteralPath $d)) { continue }
        foreach ($lnk in (Get-ChildItem -LiteralPath $d -Recurse -Filter *.lnk -ErrorAction SilentlyContinue)) {
            try {
                $s = $sh.CreateShortcut($lnk.FullName)
                # Only accept shortcuts pointing at DSH itself; avoid matching
                # unrelated products whose path merely contains 'deepseek'.
                $target = '{0} {1}' -f $s.TargetPath, $s.Arguments
                if ($target -match 'DeepSeek Harness|\\dsh\\|dsh\.exe|dsh\.cmd|dsh\.bat') {
                    $cmd = if ($s.Arguments) { '"{0}" {1}' -f $s.TargetPath, $s.Arguments } else { '"{0}"' -f $s.TargetPath }
                    return [pscustomobject]@{ Command = $cmd; Source = "shortcut:$($lnk.FullName)"; WorkDir = $s.WorkingDirectory }
                }
            } catch { }
        }
    }

    $c = Get-Command dsh -ErrorAction SilentlyContinue
    if ($c) { return [pscustomobject]@{ Command = ('"{0}" --profile {1}' -f $c.Source, $ProfileName); Source = "path:$($c.Source)" } }
    return [pscustomobject]@{ Command = $null; Source = 'unresolved' }
}

# Launch through a generated .cmd wrapper so stdout/stderr are captured.
# Hidden launch helper shared by the relauncher and the post-rollback install.
# cmd.exe / powershell.exe started with Start-Process -WindowStyle Hidden still
# allocate a console in an interactive session and the window flashes before it
# is hidden. WScript.Shell.Run(cmd, 0, ...) never creates a visible console.
function New-HiddenRunVbs {
    param([string]$VbsFile, [string]$TargetCommand)
    $body = @(
        "' Auto-generated by dsh-watchdog.ps1.",
        "' Written as UTF-16 LE with a BOM: the command below carries a path that",
        "' may contain Chinese, and BOM-less ANSI would turn it into '?'. WScript",
        "' honours the UTF-16 BOM, so the path survives whatever the code page is.",
        'Option Explicit',
        'CreateObject("WScript.Shell").Run _',
        ('  "{0}", 0, False' -f ($TargetCommand -replace '"', '""'))
    ) -join "`r`n"
    [System.IO.File]::WriteAllText($VbsFile, $body, (New-Object System.Text.UnicodeEncoding($false, $true)))
    return $VbsFile
}

# Which encoding a generated .cmd must be written in depends on the console code
# page that cmd.exe will decode it with -- NOT on the system ANSI code page.
#
#   chcp 936 (the normal Chinese Windows default): ANSI, no BOM. Measured: works.
#   chcp 65001 (UTF-8, opt-in, or a parent shell that set it): raw GBK bytes are
#     NOT valid UTF-8, so the path breaks and the wrapper fails outright.
#     Measured: ANSI-written .cmd under 65001 fails. UTF-8 *with BOM* works there.
#
# The catch is that cmd.exe fast-paths BOM-less UTF-8 only when the code page is
# already 65001, so the file and the code page must be chosen together. Passing
# /d skips any AutoRun command that might change the code page behind our back.
function Get-CmdEncoding {
    $cp = 936
    $cpOut = cmd.exe /d /c 'chcp.com' 2>$null
    if ("$cpOut" -match '(\d{3,5})') { $cp = [int]$Matches[1] }
    if ($cp -eq 65001) {
        return [pscustomobject]@{ Encoding = (New-Object System.Text.UTF8Encoding($true)); CodePagePrefix = 'chcp 65001 >nul & '; CodePage = $cp }
    }
    return [pscustomobject]@{ Encoding = [System.Text.Encoding]::Default; CodePagePrefix = ''; CodePage = $cp }
}

function Start-DshCaptured {
    param([string]$Command)
    if ($Command -match '[&\|<>^%]') { throw "launch command contains shell metacharacters; refusing: $Command" }

    # Fail loudly instead of writing a wrapper that points somewhere else.
    # Any '?' in these paths means a character the ANSI code page cannot encode;
    # Set-Content would happily write it, cmd.exe would follow the wrong path,
    # and the failure would be silent -- which is how the bug below hid.
    if ("$DataDir$ConsoleDir$Command" -match '\?') {
        throw "path contains '?' (unencodable); refusing to build a wrapper from it: DataDir=$DataDir"
    }
    # Every file generated below carries the DataDir path. All of them must be
    # written in an encoding the *consumer* honours -- see Get-CmdEncoding for the
    # .cmd, and New-HiddenRunVbs for the .vbs. Writing them as ASCII is what broke
    # this: '?' replaced the Chinese in D:\DS\<Chinese>\data, cmd.exe/wscript then
    # followed a path that does not exist, and data\console\ stayed empty.

    # Every launcher file name carries our pid.
    #
    # A seconds-resolution stamp alone is NOT unique: two launches inside the
    # same second (relaunch after a rescue, or the install step racing the
    # relaunch) produced the same file name, and the second Set-Content hit a
    # file the first wscript.exe still had open --
    #   "Launch failed: The process cannot access the file ... launch-captured.vbs
    #    because it is being used by another process."
    # The pid is unique for the life of this process, so collisions are gone.
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $tag = "$stamp-$PID"
    $outLog = Join-Path $ConsoleDir "console-$stamp.log"
    $cmdFile = Join-Path $DataDir "launch-captured-$tag.cmd"
    $vbsFile = Join-Path $DataDir "launch-captured-$tag.vbs"

    # stderr is the only place installFailLoud writes its diagnostic, so it must
    # be redirected; the cmd wrapper stays, but is started with no console.
    $enc = Get-CmdEncoding
    $cmdBody = @(
        '@echo off',
        ('{0}{1} >> "{2}" 2>&1' -f $enc.CodePagePrefix, $Command, $outLog),
        ('echo [wrapper] exited with %ERRORLEVEL% >> "{0}"' -f $outLog)
    ) -join "`r`n"
    # The wrapper must be written in the system ANSI code page (GBK/936 on
    # Simplified Chinese Windows) -- NOT ASCII.
    #
    # This file carries the DataDir path, and that path can contain Chinese
    # (e.g. D:\DS\<Chinese>\data\...). -Encoding ascii silently replaces every
    # non-ASCII character with '?', so the redirect target became
    # "D:\DS\????\data\console\console-<stamp>.log": cmd.exe created that
    # directory instead, and data\console\ stayed empty -- which is exactly the
    # stderr evidence Get-CrashEvidence treats as "the most important evidence".
    # Nobody noticed, because writing the wasted log never failed.
    #
    # The old rule was "the .cmd must be pure ASCII". That is only true when the
    # paths inside it are ASCII; it cannot hold when the tool lives under a
    # Chinese directory. cmd.exe decodes batch files with the OEM code page, and
    # on this system ANSI == OEM == 936, so Default preserves the path and
    # cmd.exe reads it back correctly. CRLF stays mandatory.
    [System.IO.File]::WriteAllText($cmdFile, $cmdBody, $enc.Encoding)

    New-HiddenRunVbs -VbsFile $vbsFile -TargetCommand ('cmd.exe /c "{0}"' -f $cmdFile) | Out-Null
    Start-Process -FilePath 'wscript.exe' -ArgumentList ('"{0}"' -f $vbsFile) -WindowStyle Hidden

    return [pscustomobject]@{ OutLog = $outLog; Wrapper = $cmdFile; Launcher = $vbsFile }
}

# Snapshot pointer written by dsh-snapshot.ps1 -Action Create.
function Get-LastKnownGood {
    if (-not (Test-Path -LiteralPath $pointerFile)) { return $null }
    try {
        $p = Get-Content -LiteralPath $pointerFile -Raw -Encoding utf8 | ConvertFrom-Json
        if (-not $p.snapshot) { return $null }
        $dir = $p.snapshotRoot
        if (-not $dir) {
            # Legacy pointers may omit snapshotRoot: try the current data layout,
            # then the pre-reorganisation one, instead of guessing wrong.
            $here = Join-Path $DataDir 'snapshots'
            $legacy = Join-Path (Split-Path -Parent (Split-Path -Parent $DataDir)) 'dsh-guardian-data\snapshots'
            if (Test-Path -LiteralPath (Join-Path $here $p.snapshot)) { $dir = $here }
            elseif (Test-Path -LiteralPath (Join-Path $legacy $p.snapshot)) { $dir = $legacy }
            else { return $null }
        }
        if (-not (Test-Path -LiteralPath (Join-Path $dir $p.snapshot))) { return $null }
        return $p
    } catch {
        return $null
    }
}

function Write-RescueRecord {
    param([hashtable]$Fields)
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $file = Join-Path $DataDir ("rescue-$stamp.json")
    ($Fields | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $file -Encoding utf8
    return $file
}

# Locates the node + pnpm that DSH itself uses. The profile is reconciled with
# the app's own bundled pnpm, NOT the one under ~/.dsh, because the plugin
# manager writes its logs with that exact binary.
function Find-AppRuntime {
    $roots = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\DeepSeek Harness\resources\runtime'),
        'C:\Program Files\DeepSeek Harness\resources\runtime'
    )
    $pnpm = $null
    foreach ($r in $roots) {
        $cand = Join-Path $r 'pnpm\dist\pnpm.mjs'
        if (Test-Path -LiteralPath $cand) { $pnpm = $cand; break }
    }
    if (-not $pnpm) { return $null }

    $nodeCandidates = @()
    foreach ($r in $roots) {
        $nodeCandidates += (Join-Path $r 'primary-runtime\dependencies\node\bin\node.exe')
        $nodeCandidates += (Join-Path $r 'node\node.exe')
        $nodeCandidates += (Join-Path $r 'node\bin\node.exe')
    }
    $nodeCandidates += (Join-Path $env:USERPROFILE '.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\node\bin\node.exe')
    $node = $null
    foreach ($c in $nodeCandidates) { if ($c -and (Test-Path -LiteralPath $c)) { $node = $c; break } }
    if (-not $node) { return $null }

    return [pscustomobject]@{ Node = $node; Pnpm = $pnpm }
}

# Post-rollback dependency reconciliation: makes node_modules match the restored
# pnpm-lock.yaml. Without it, a rollback can land right back in the crash it just
# recovered from, because node_modules still holds the broken plugin.
#
# NOTE: 'dsh plugin --profile desktop install' is NOT usable: the CLI refuses the
# desktop profile outright ("managed exclusively by the Electron application").
# So the app's own pnpm is invoked directly in the profile directory, which is
# exactly what the plugin manager does. --no-frozen-lockfile is used because it
# repairs drift instead of failing on it.
function Invoke-ProfileInstall {
    param([int]$TimeoutSeconds)

    if ($TimeoutSeconds -le 0) {
        return [pscustomobject]@{ Ok = $false; Message = 'disabled by -InstallTimeoutSeconds 0'; Log = $null }
    }

    $rt = Find-AppRuntime
    if (-not $rt) {
        return [pscustomobject]@{ Ok = $false
            Message = ('node/pnpm runtime not found; reconcile manually: pnpm install in {0}' -f $ProfileDir); Log = $null }
    }
    if (-not (Test-Path -LiteralPath $ProfileDir)) {
        return [pscustomobject]@{ Ok = $false; Message = ('profile dir missing: {0}' -f $ProfileDir); Log = $null }
    }

    # Pid-tagged for the same reason as the launch files above: a bare second
    # stamp collides when the install and the relaunch happen inside one second.
    $tag = (Get-Date -Format 'yyyyMMdd-HHmmss') + "-$PID"
    $log = Join-Path $ConsoleDir ('install-{0}.log' -f $tag)
    $cmdFile = Join-Path $DataDir "post-rollback-install-$tag.cmd"
    if ("$DshHome$ProfileDir$log" -match '\?') {
        return [pscustomobject]@{ Ok = $false
            Message = "path contains '?' (unencodable in the console code page); refusing to build the install wrapper"; Log = $null }
    }
    $enc = Get-CmdEncoding
    $body = @(
        '@echo off',
        ('{0}set "DSH_HOME={1}"' -f $enc.CodePagePrefix, $DshHome),
        ('cd /d "{0}"' -f $ProfileDir),
        ('"{0}" "{1}" install --no-frozen-lockfile --reporter=append-only >> "{2}" 2>&1' -f $rt.Node, $rt.Pnpm, $log),
        ('echo [install] exit=%ERRORLEVEL% >> "{0}"' -f $log)
    ) -join "`r`n"
    # Written in the console code page for the same reason as the launch wrapper:
    # this body contains $DshHome and $ProfileDir, and a '?' -mangled path here
    # would make the post-rollback install silently reconcile the wrong folder.
    [System.IO.File]::WriteAllText($cmdFile, $body, $enc.Encoding)

    $vbsFile = Join-Path $DataDir "post-rollback-install-$tag.vbs"
    New-HiddenRunVbs -VbsFile $vbsFile -TargetCommand ('cmd.exe /c "{0}"' -f $cmdFile) | Out-Null

    $proc = Start-Process -FilePath 'wscript.exe' -ArgumentList ('"{0}"' -f $vbsFile) -WindowStyle Hidden -PassThru
    $exited = $proc.WaitForExit($TimeoutSeconds * 1000)
    if (-not $exited) {
        try { $proc.Kill() } catch { }
        return [pscustomobject]@{ Ok = $false; Message = ('timed out after {0}s; see {1}' -f $TimeoutSeconds, $log); Log = $log }
    }

    # The exit line is appended by cmd.exe after pnpm returns; allow a few reads
    # for the write to become visible, otherwise a successful install is
    # misreported as unknown.
    $result = 'unknown'
    for ($i = 0; $i -lt 10; $i++) {
        if (Test-Path -LiteralPath $log) {
            $m = Select-String -LiteralPath $log -Pattern '\[install\] exit=(\d+)' -ErrorAction SilentlyContinue | Select-Object -Last 1
            if ($m) { $result = $m.Matches[0].Groups[1].Value; break }
        }
        Start-Sleep -Milliseconds 300
    }
    if ($result -eq '0') {
        return [pscustomobject]@{ Ok = $true; Message = ('node_modules now matches the restored lockfile ({0})' -f $log); Log = $log }
    }
    return [pscustomobject]@{ Ok = $false
        Message = ('exit={0}; reconcile manually: pnpm install in {1}  (see {2})' -f $result, $ProfileDir, $log); Log = $log }
}

# Starts DSH and records it. Shared by the normal round and by the rollback,
# which previously carried two near-identical copies of this sequence. The state
# fields are set here so both callers agree on what "a launch" means.
function Start-DshAndRecord {
    param($State, [datetime]$Now)

    if ($NoStart) {
        return [pscustomobject]@{ Launched = $false; OutLog = $null; Error = $null; Reason = 'disabled' }
    }

    $launch = Resolve-LaunchCommand -Explicit $LaunchCommand
    if (-not $launch.Command) {
        Write-Log 'Could not resolve a launch command. Pass -LaunchCommand.' 'WARN'
        Write-Event -Kind 'START-UNRESOLVED' -Fields @{ note = 'set -LaunchCommand' }
        return [pscustomobject]@{ Launched = $false; OutLog = $null; Error = $null; Reason = 'unresolved' }
    }

    try {
        $cap = Start-DshCaptured -Command $launch.Command
        $State.launches = [int]$State.launches + 1
        $State.lastStartAt = $Now.ToString('o')
        $State.lastStartWasHealthy = $false
        Write-Log ("Launched (stderr captured to {0}), source {1}" -f $cap.OutLog, $launch.Source)
        Write-Event -Kind 'START' -Fields @{
            command = $launch.Command; source = $launch.Source
            launchIndex = $State.launches; consoleLog = $cap.OutLog
        }
        return [pscustomobject]@{ Launched = $true; OutLog = $cap.OutLog; Error = $null; Reason = 'ok' }
    } catch {
        Write-Log ("Launch failed: {0}" -f $_.Exception.Message) 'ERROR'
        Write-Event -Kind 'START-FAILED' -Fields @{ error = $_.Exception.Message }
        return [pscustomobject]@{ Launched = $false; OutLog = $null; Error = $_.Exception.Message; Reason = 'failed' }
    }
}

# The rescue: pre-save current state, invoke the snapshot restore, relaunch.
# This is the ONLY path in which the watchdog writes anything outside its own
# data directory, and it runs only when -AutoRollback is set and the guards pass.
function Invoke-AutoRollback {
    param($Pointer, $State, [string]$Reason)

    $target = Join-Path $Pointer.snapshotRoot $Pointer.snapshot

    # NOTE: the broken config is preserved by the snapshot script itself, which
    # copies the current state to pre-restore-<stamp> before writing anything.
    # This function used to make its OWN copy first, so every automatic rollback
    # left TWO identical pre-restore directories for the same broken config.
    $preStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $preDir = $null
    # Which backups already exist, so the one THIS rollback creates can be
    # identified exactly. Picking "the newest" would be wrong when a manual
    # restore runs close by, or on a clock adjustment: it could report - and
    # later log - a backup belonging to a different operation.
    $preBefore = @()
    try {
        $preBefore = @(Get-ChildItem -LiteralPath $Pointer.snapshotRoot -Directory -Filter 'pre-restore-*' -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Name })
    } catch { }

    $restored = @()
    $deleted = @()
    $err = $null

    Write-Log ("ROLLBACK: restoring snapshot {0} (reason: {1})" -f $Pointer.snapshot, $Reason) 'ALERT'

    if (Test-Path -LiteralPath $snapshotScript) {
        try {
            # Delegate to the tested restore implementation.
            & $snapshotScript -Action Restore -Force -Snapshot $Pointer.snapshot -SnapshotRoot $Pointer.snapshotRoot -ProfileName $ProfileName -DshHome $DshHome *>&1 |
                ForEach-Object { Write-Log ("  restore: {0}" -f $_) }
            $restored = @($ConfigPlane | Where-Object { Test-Path -LiteralPath (Join-Path $ProfileDir $_) })
            # The backup this rollback just made is the one that was not there
            # before. Fall back to the newest only if that difference is empty,
            # which would mean the script did not create one at all.
            $preAfter = @(Get-ChildItem -LiteralPath $Pointer.snapshotRoot -Directory -Filter 'pre-restore-*' -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending)
            $mine = @($preAfter | Where-Object { $preBefore -notcontains $_.Name })
            if ($mine.Count -gt 0) { $preDir = $mine[0].FullName }
            elseif ($preAfter.Count -gt 0) { $preDir = $preAfter[0].FullName }
        } catch {
            $err = $_.Exception.Message
        }
    } else {
        # Fallback: no snapshot script, so do the backup here and restore the
        # plane directly.
        Write-Log '  snapshot script missing; restoring directly' 'WARN'
        $preDir = Join-Path $Pointer.snapshotRoot "pre-restore-$preStamp"
        New-Item -ItemType Directory -Force -Path $preDir | Out-Null
        foreach ($n in @($ConfigPlane | Where-Object { Test-Path -LiteralPath (Join-Path $ProfileDir $_) })) {
            Copy-Item -LiteralPath (Join-Path $ProfileDir $n) -Destination (Join-Path $preDir $n) -Force
        }
        foreach ($n in $ConfigPlane) {
            $src = Join-Path $target $n
            $dst = Join-Path $ProfileDir $n
            try {
                if (Test-Path -LiteralPath $src) {
                    Copy-Item -LiteralPath $src -Destination $dst -Force
                    $restored += $n
                } elseif (Test-Path -LiteralPath $dst) {
                    Remove-Item -LiteralPath $dst -Force
                    $deleted += $n
                }
            } catch {
                $err = $_.Exception.Message
            }
        }
    }

    # --- mandatory post-rollback step, automated -------------------------------
    # The config plane is back to the good version, but node_modules still
    # matches the BROKEN lockfile. Leaving that mismatch is exactly what makes a
    # rollback reland in the crash it just recovered from, so the dependency tree
    # is reconciled here, before DSH is relaunched.
    $install = Invoke-ProfileInstall -TimeoutSeconds $InstallTimeoutSeconds
    Write-Log ("  install: {0}" -f $install.Message) $(if ($install.Ok) { 'INFO' } else { 'WARN' })

    $relaunched = $false
    $launchInfo = $null
    # This call also resets lastStartAt/lastStartWasHealthy, which matters: the
    # relaunch must look like a fresh launch so the boot window applies again.
    $relaunch = Start-DshAndRecord -State $State -Now (Get-Date)
    $relaunched = $relaunch.Launched
    $launchInfo = $relaunch.OutLog
    if (-not $err) { $err = $relaunch.Error }

    # Crash diagnostics, written after the dry work and before the record. The
    # restore only touches config files, so evidence gathered here is intact.
    $evidenceFile = Join-Path $DataDir "crash-evidence-$stamp.json"
    (Get-EvidenceTail -Lines $TailLines) | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $evidenceFile -Encoding utf8

    $record = @{
        rolledBack          = $true
        at                  = (Get-Date).ToString('o')
        reason              = $Reason
        snapshot            = $Pointer.snapshot
        snapshotRoot        = $Pointer.snapshotRoot
        snapshotWasHealthy  = $Pointer.observedHealthy
        snapshotBundles     = $Pointer.bundles
        preRestoreDir       = $preDir
        evidenceFile        = $evidenceFile
        restoredFiles       = $restored
        deletedFiles        = $deleted
        installOk           = $install.Ok
        installMessage      = $install.Message
        installLog          = $install.Log
        relaunched          = $relaunched
        relaunchConsoleLog  = $launchInfo
        error               = $err
    }
    $recordFile = Write-RescueRecord -Fields $record

    Write-Event -Kind 'ROLLBACK' -Fields $record
    Write-Log ("  restored: {0}" -f ($restored -join ', ')) 'ALERT'
    if ($preDir) { Write-Log ("  broken config preserved: {0}" -f $preDir) 'ALERT' }
    Write-Log ("  crash evidence: {0}" -f $evidenceFile) 'ALERT'
    Write-Log ("  rescue record: {0}" -f $recordFile) 'ALERT'
    Write-Log ("  relaunched: {0}" -f $relaunched) 'ALERT'
    if (-not $install.Ok) {
        Write-Log ("  ACTION NEEDED: {0}" -f $install.Message) 'ALERT'
    }

    return $record
}

# There is deliberately no scheduled-task support any more. A per-minute task
# spawns a process whether or not anything is happening, which is the opposite of
# "minimum memory, run only when I open it". The watcher is started by the menu
# and lives only as long as that window.

# ------------------------------------------------------------------ single round
# Resident gate. -Resident turns this script into the long-lived watcher. It is
# only honoured when armed AND when a live parent asked for it, so nothing can be
# left running after the window that started it is gone.
$residentWanted = ($Resident -and $effectiveMode -eq 'auto' -and -not $Pause -and -not $Resume)
if ($Resident -and -not $residentWanted) {
    Write-Log 'resident: not armed, staying single-round' 'INFO'
}
if ($residentWanted) {
    $existing = Get-RunningRuntimePid
    if ($existing -gt 0) {
        Write-Log ("resident: pid {0} already watching; this invocation exits" -f $existing) 'INFO'
        return
    }
    # Claim the slot before the first (slow) round so two starts cannot race.
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    Set-Content -LiteralPath $runtimeFile -Value $PID -Encoding ascii
    $resStart = Get-Date
    $deadline = $resStart.AddMinutes($MaxResidentMinutes)
    $parentNote = if ($ParentPid -gt 0) { "parent $ParentPid" } else { 'no parent binding' }
    Write-Log ("resident: started (pid {0}, {1}, interval {2}s, max {3} min)" -f $PID, $parentNote, $IntervalSeconds, $MaxResidentMinutes) 'INFO'
}

$roundIndex = 0
# Log the detection result only when it changes; a heartbeat every 55s would
# bury everything else in the log.
$lastPortLogged = ''
do {
$roundIndex++

# Re-resolved every round on purpose: DSH may restart on a different port, and
# an explicit -Port still short-circuits this to a fixed value.
$resolved = Resolve-DshPort -Explicit $Port -Patterns $ProcessMatch
$Port = $resolved.Port
$portSource = $resolved.Source
if ("$Port|$portSource" -ne $lastPortLogged) {
    Write-Log ("Probe port {0} (source: {1})" -f $Port, $portSource) 'INFO'
    $lastPortLogged = "$Port|$portSource"
}

$now = Get-Date
$alive = Test-PortAlive -TargetHost '127.0.0.1' -TargetPort $Port -TimeoutMs $ProbeTimeoutMs
$procResult = Get-DshProcesses -Patterns $ProcessMatch
$procs = @($procResult.Processes)
$procsDegraded = [bool]$procResult.Degraded
$state = Get-GuardState
$fingerprint = Get-ConfigFingerprint

if (-not $state) {
    $state = [ordered]@{
        firstSeen             = $now.ToString('o')
        lastHealthy           = $null
        lastFail              = $null
        failStreak            = 0
        shortLivedStarts      = 0
        lastStartAt           = $null
        lastStartWasHealthy   = $false
        lastNotifiedCrashLoop = $false
        launches              = 0
        configFingerprint     = $null
        autoRollbacks         = 0
        lastAutoRollbackAt    = $null
        lastRescueFailSig     = $null
        relaunchStopped       = $false
    }
}

$fpJson = ($fingerprint | ForEach-Object { $_ | ConvertTo-Json -Compress }) -join '|'

# Cross-round guard: if the config plane has not moved since the last rollback,
# we already rolled back for this exact configuration and it did not help.
# Never roll back the same configuration twice.
$lastRollbackAt = $null
if ($state.PSObject.Properties['lastAutoRollbackAt'] -and $state.lastAutoRollbackAt) {
    $lastRollbackAt = [datetime]$state.lastAutoRollbackAt
}
$configMovedSinceRollback = $true
if ($lastRollbackAt -and $state.PSObject.Properties['configFingerprintAtRollback'] -and $state.configFingerprintAtRollback) {
    $configMovedSinceRollback = ($state.configFingerprintAtRollback -ne $fpJson)
}

$rollbackCount = 0
if ($state.PSObject.Properties['autoRollbacks']) { $rollbackCount = [int]$state.autoRollbacks }
$canAutoRollback = ($AutoRollback -and $configMovedSinceRollback -and $rollbackCount -lt $MaxAutoRollbacks)
# Both of these describe a standing condition, so they are reported once per
# configuration instead of on every round of a long-lived resident runtime.
if ($AutoRollback -and $lastRollbackAt -and $shortLived -and (Get-Prop $state 'lastSuppressSig') -ne "unchanged|$fpJson") {
    Set-Prop $state 'lastSuppressSig' "unchanged|$fpJson"
    Write-Log ("Auto-rollback suppressed: config plane unchanged since the rollback at {0}; repeating it would redo a rescue that already failed." -f $lastRollbackAt) 'WARN'
}
if ($AutoRollback -and $rollbackCount -ge $MaxAutoRollbacks -and (Get-Prop $state 'lastCapSig') -ne "cap|$rollbackCount") {
    Set-Prop $state 'lastCapSig' "cap|$rollbackCount"
    Write-Log ("Auto-rollback suppressed: already rolled back {0} time(s) in this episode (cap {1}). Human review required." -f $rollbackCount, $MaxAutoRollbacks) 'WARN'
}

if (-not $state.configFingerprint) {
    # First observation (or a state file written before this field existed).
    # Record it without reporting a change: there is nothing to compare against,
    # and announcing "a plugin was installed" on the very first round would be
    # wrong.
    #
    # This assignment must stay OUTSIDE the comparison below. When it lived
    # inside it, a null fingerprint made the condition false, so the value was
    # never stored and the comparison stayed false forever -- the "a plugin was
    # installed" notice was permanently dead, and so was the input to the
    # never-roll-back-the-same-configuration guard.
    $state.configFingerprint = $fpJson
    Write-Log 'Config plane fingerprint recorded (first observation)' 'INFO'
} elseif ($state.configFingerprint -ne $fpJson) {
    Write-Event -Kind 'CONFIG-CHANGED' -Fields @{ previous = $state.configFingerprint; current = $fpJson }
    Write-Log 'Config plane changed (a plugin was most likely installed or removed)' 'INFO'
    $snapScript = Join-Path $PSScriptRoot 'dsh-snapshot.ps1'
    Write-Log ('  take a snapshot once health is confirmed: powershell -File "{0}" -Action Create' -f $snapScript) 'INFO'
    $state.configFingerprint = $fpJson
}

if ($alive) {
    if (-not $state.lastHealthy -or $state.lastFail) {
        Write-Log ("DSH healthy (127.0.0.1:{0} listening, {1} matching process(es))" -f $Port, $procs.Count)
    }
    $state.lastHealthy = $now.ToString('o')
    $state.failStreak = 0
    # Clear the crash-loop notice only when a launch WE performed has survived.
    # A bare healthy probe is not enough: while a crashed instance is still
    # shutting down the port can answer for a round, and resetting here made the
    # watchdog announce the same crash loop on every round instead of once per
    # episode. shortLivedStarts is handled further down, only when the launch
    # survived past the grace window.
    if ($state.lastStartWasHealthy) { $state.lastNotifiedCrashLoop = $false }
    $state.lastStartWasHealthy = $true
} elseif ($state.lastStartAt -and
          -not $state.lastStartWasHealthy -and
          -not $state.lastHealthy -and
          ($now - [datetime]$state.lastStartAt).TotalSeconds -lt $BootWindowSeconds) {
    # Inside the startup window of a launch we performed. DSH can take tens of
    # seconds to bind its port, so this is NOT a crash and must not trigger a
    # relaunch (that would spawn duplicate instances every round).
    # This test is deliberately time-only. A "is a DSH process alive" test is
    # unreliable: command-line matching cannot tell the instance we launched
    # from any other instance that happens to be running, so a process match
    # would keep the watchdog in "starting" forever.
    $waited = [int]($now - [datetime]$state.lastStartAt).TotalSeconds
    $procNote = if ($procsDegraded) { 'unavailable (degraded)' } else { "$($procs.Count)" }
    Write-Log ("DSH starting: {0}s since launch, process count {1}, boot window {2}s" -f $waited, $procNote, $BootWindowSeconds)
    Write-Log ("  (if a healthy boot takes longer than {0}s, raise -BootWindowSeconds; if a failing start takes longer than {1}s to die, raise -StartGraceSeconds)" -f $BootWindowSeconds, $StartGraceSeconds) 'INFO'
    Write-Event -Kind 'STARTING' -Fields @{ waitedSeconds = $waited; bootWindowSeconds = $BootWindowSeconds; processCount = $procs.Count }
} else {
    $state.failStreak = [int]$state.failStreak + 1
    $state.lastFail = $now.ToString('o')

    $evidence = Get-EvidenceTail -Lines $TailLines
    $fields = @{
        failStreak   = $state.failStreak
        port         = $Port
        processCount = $procs.Count
        processIds   = @($procs | ForEach-Object { $_.ProcessId })
        evidence     = $evidence
    }

    $shortLived = $false
    if ($state.lastStartAt) {
        $aliveSec = ($now - [datetime]$state.lastStartAt).TotalSeconds
        if ($aliveSec -lt $StartGraceSeconds) {
            # A launch we made died again almost immediately.
            $shortLived = $true
            $fields['aliveSeconds'] = [int]$aliveSec
        } else {
            # The process survived past the grace window, so the previous launch
            # cannot be called a crash loop. If DSH was healthy before that
            # launch, this is a fresh episode and the counter restarts.
            if ($state.lastStartWasHealthy) { $state.shortLivedStarts = 0 }
        }
    }

    if ($state.lastStartAt -and $shortLived) {
        $state.shortLivedStarts = [int]$state.shortLivedStarts + 1
    } elseif (-not $state.lastStartAt) {
        $state.shortLivedStarts = 0
    }

    # Two independently sufficient signals that a startup crash loop is real:
    #  (a) launches repeatedly died within StartGraceSeconds, or
    #  (b) we exhausted the relaunch budget and DSH still never answered.
    $budgetExhausted = ([int]$state.failStreak -ge $BootRetryBudget)
    $crashLoopProven = (($state.shortLivedStarts -ge $CrashLoopThreshold) -or $budgetExhausted)

    # A rollback was already tried for this exact configuration and DSH is still
    # down, so this configuration is known-bad. Reported once per configuration
    # (see the signature check below), not once per round.
    $rescueFailed = ($lastRollbackAt -and -not $configMovedSinceRollback)

    # A moved config plane means a NEW plugin was installed since the last
    # rollback, i.e. this is a fresh crash episode. lastNotifiedCrashLoop must
    # not carry over from the previous episode, or the second and every later
    # plugin-induced crash would be silently ignored and never rolled back.
    #
    # $lastRollbackAt is required here. configMovedSinceRollback defaults to true
    # for the very first episode (there is no rollback to compare against yet),
    # so without it this reset fired on every round of a crash loop that had
    # never rolled back -- the notice re-armed itself and the log filled with
    # one identical "crash loop proven" / "no known-good snapshot" pair per round.
    if ($lastRollbackAt -and $configMovedSinceRollback) {
        $state.lastNotifiedCrashLoop = $false
        Set-Prop $state 'failNotified' $false
    }

    # Report a failed rescue once per configuration, not once per round. The
    # resident runtime makes rounds frequent (every ~55s for hours), so repeating
    # this block would drown the log while adding nothing.
    $rescueSignature = "$lastRollbackAt|$fpJson"
    if ($rescueFailed -and $budgetExhausted -and (Get-Prop $state 'lastRescueFailSig') -ne $rescueSignature) {
        Set-Prop $state 'lastRescueFailSig' $rescueSignature
        Write-Event -Kind 'RESCUE-FAILED' -Fields $fields
        Write-Log 'AUTO-ROLLBACK DID NOT RESOLVE THE CRASH LOOP.' 'ALERT'
        Write-Log ("  A configuration identical to the one rolled back at {0} still fails to boot." -f $lastRollbackAt) 'ALERT'
        Write-Log '  The rolled-back plugin was probably not the cause. Manual work needed:' 'ALERT'
        Write-Log ("    1) dsh --profile {0} --dump-config" -f $ProfileName) 'ALERT'
        Write-Log ("    2) powershell -File ""{0}"" -Action List" -f $snapshotScript) 'ALERT'
        Write-Log ("    3) inspect {0}" -f $ConsoleDir) 'ALERT'
    }

    if ($crashLoopProven -and -not $state.lastNotifiedCrashLoop) {
        $signal = if ($state.shortLivedStarts -ge $CrashLoopThreshold) {
            '{0} consecutive launches died within {1}s' -f $state.shortLivedStarts, $StartGraceSeconds
        } else {
            '{0} failed launch attempts and DSH still does not answer' -f $state.failStreak
        }
        Write-Event -Kind 'PLUGIN-SUSPECT' -Fields $fields
        # This block runs once per episode; the rescue-failure notice above is
        # what reports a standing bad state, and it is deduped by signature.
        $logLevel = if ($rescueFailed) { 'INFO' } else { 'ALERT' }
        Write-Log ("Startup crash loop proven: {0}. The most recently installed plugin is the prime suspect." -f $signal) $logLevel
        Write-Log ("  crash evidence: {0}" -f $eventLog) $logLevel
        Write-Log ("  captured stderr: {0}" -f $ConsoleDir) $logLevel
        Write-Log '  Investigation commands (run whichever applies):' $logLevel
        Write-Log ("    1) dsh --profile {0} --dump-config" -f $ProfileName) $logLevel
        Write-Log ("    2) dsh plugin --profile {0} remove <suspect-package>" -f $ProfileName) $logLevel
        Write-Log ('    3) powershell -File "{0}" -Action Restore -DryRun' -f $snapshotScript) $logLevel
        $state.lastNotifiedCrashLoop = $true

        if ($canAutoRollback) {
            $pointer = Get-LastKnownGood
            if (-not $pointer) {
                Write-Log '  Auto-rollback requested but no known-good snapshot pointer exists.' 'ALERT'
                Write-Log '  Create one BEFORE installing plugins: dsh-snapshot.ps1 -Action Create -Label known-good' 'ALERT'
                Write-Event -Kind 'ROLLBACK-IMPOSSIBLE' -Fields @{ reason = 'no known-good snapshot pointer'; pointerFile = $pointerFile }
            } else {
                # Crash diagnostics first: the restore may replace config files.
                # The return value is deliberately not captured: the function
                # already logs every field, appends a ROLLBACK event and writes
                # the rescue record file itself, so a caller-side copy was a
                # write-only variable.
                Invoke-AutoRollback -Pointer $pointer -State $state -Reason 'startup crash loop' | Out-Null
                $state.autoRollbacks = [int]$rollbackCount + 1
                $state.lastAutoRollbackAt = (Get-Date).ToString('o')
                # The restored plane becomes the new baseline.
                $state.configFingerprint = ((Get-ConfigFingerprint | ForEach-Object { $_ | ConvertTo-Json -Compress }) -join '|')
                # Record the configuration this attempt RESULTED in, so the next
                # round can tell "DSH is still down on the config we already
                # tried" from "the user changed something since".
                #
                # This must be read AFTER the restore. Storing the pre-restore
                # fingerprint here compared two different moments (before vs
                # after the restore) and so was never equal, which silently
                # disabled the "never roll back the same configuration twice"
                # guard -- only the MaxAutoRollbacks cap was left holding.
                $state | Add-Member -NotePropertyName configFingerprintAtRollback -NotePropertyValue $state.configFingerprint -Force
                $canAutoRollback = $false
            }
        } elseif ($AutoRollback -and -not $canAutoRollback) {
            # Say this once per configuration, not once per round.
            $suppressSig = "suppressed|$fpJson"
            if (-not $rescueFailed -and (Get-Prop $state 'lastSuppressSig') -ne $suppressSig) {
                Set-Prop $state 'lastSuppressSig' $suppressSig
                Write-Log '  Automatic rollback is not available for this round.' 'WARN'
            }
        }
    } else {
        # The first failure of an episode is the interesting one; the resident
        # runtime would otherwise print this every interval for hours.
        if (-not $state.lastNotifiedCrashLoop -or -not (Get-Prop $state 'failNotified')) {
            Write-Event -Kind 'FAIL' -Fields $fields
            Write-Log ("DSH unhealthy (failure #{0}): 127.0.0.1:{1} refused connection" -f $state.failStreak, $Port) 'WARN'
            Set-Prop $state 'failNotified' $true
        }
    }

    # Relaunch budget. Once a rescue has failed and the configuration has not
    # moved, restarting DSH again cannot succeed - it would just spin. Stop, and
    # say so once, instead of relaunching forever.
    $launchBudgetExhausted = ($rescueFailed -and (Get-Prop $state 'lastRescueFailSig') -eq $rescueSignature)
    if ($launchBudgetExhausted) {
        if (-not (Get-Prop $state 'relaunchStopped')) {
            Set-Prop $state 'relaunchStopped' $true
            Write-Log '  Relaunching stopped: the rollback did not help, so another restart cannot either.' 'ALERT'
            Write-Log ("  Fix the cause, then re-arm with: {0} arm" -f (Join-Path $BaseDir 'dsh-guardian.exe')) 'ALERT'
        }
    } else {
        Set-Prop $state 'relaunchStopped' $false
        Start-DshAndRecord -State $state -Now $now | Out-Null
    }
}

Save-GuardState -State $state

# Heartbeat. Healthy rounds are otherwise silent (the log only records state
# changes), so without this there would be no way to tell a working watcher from
# a dead one. Written every round; the log line is throttled to HeartbeatMinutes.
$tickFile = Join-Path $DataDir 'last-tick.json'
if (-not $state.PSObject.Properties['configFingerprintAtRollback']) {
    $state | Add-Member -NotePropertyName configFingerprintAtRollback -NotePropertyValue $null
}
$tick = [ordered]@{
    at      = $now.ToString('o')
    alive   = $alive
    port    = $Port
    pid     = $PID
    rss     = 0
    healthy = $alive
    configMovedSinceRollback = $configMovedSinceRollback
    lastAutoRollbackAt       = if ($state.PSObject.Properties['lastAutoRollbackAt']) { $state.lastAutoRollbackAt } else { $null }
}
($tick | ConvertTo-Json) | Set-Content -LiteralPath $tickFile -Encoding utf8

# One human-readable line every HeartbeatMinutes so the log has a visible pulse too.
if (-not $state.PSObject.Properties['lastHeartbeatLog']) {
    $state | Add-Member -NotePropertyName lastHeartbeatLog -NotePropertyValue $null
}
if (-not $state.lastHeartbeatLog -or
    ($now - [datetime]$state.lastHeartbeatLog).TotalMinutes -ge 30) {
    Write-Log ("tick: alive={0} port={1} pid={2}" -f $alive, $Port, $PID)
    $state.lastHeartbeatLog = $now.ToString('o')
    Save-GuardState -State $state
}

if (Test-Path -LiteralPath $reportLog) {
    # Guarded for the same reason as the fingerprint: the file can disappear
    # between the Test-Path and the read, and a throw here would end the round.
    try {
        if ((Get-Item -LiteralPath $reportLog -ErrorAction Stop).Length -gt 5MB) {
            Move-Item -LiteralPath $reportLog -Destination "$reportLog.1" -Force -ErrorAction Stop
        }
    } catch { }
}

    if (-not $residentWanted) { break }

    # Sleep between rounds, checking every few seconds so disarming, closing the
    # window, or a kill takes effect quickly instead of after a full interval.
    # A parent process exiting cannot be observed by WaitForExit here, so the
    # launcher is polled: memory stays flat and the check is nearly free.
    $woke = Get-Date
    $nap = [int]$IntervalSeconds
    $exitReason = $null
    while (((Get-Date) - $woke).TotalSeconds -lt $nap) {
        Start-Sleep -Seconds 3
        if ((Read-Mode) -eq 'paused') { $exitReason = 'disarmed'; break }
        if ($ParentPid -gt 0 -and -not (Test-PidAlive -ProcessId $ParentPid)) { $exitReason = 'launcher closed'; break }
    }

    # Exit conditions, in order of importance:
    #  - disarmed or launcher gone (above)
    #  - lifetime cap reached, so a forgotten runtime cannot live forever
    #  - the pid file no longer names us (another instance took over)
    if ($exitReason) { Write-Log ("resident: exiting ({0})" -f $exitReason) 'INFO'; break }
    if ((Read-Mode) -eq 'paused') { break }
    if ((Get-Date) -ge $deadline) { Write-Log 'resident: lifetime cap reached, exiting' 'INFO'; break }
    if ((Get-RunningRuntimePid) -ne $PID) { Write-Log 'resident: lost the pid slot, exiting' 'INFO'; break }
} while ($true)

if ($residentWanted) {
    # Only clear the slot if it still belongs to this process.
    try {
        if ((Test-Path -LiteralPath $runtimeFile) -and
            ((Get-Content -LiteralPath $runtimeFile -Raw -Encoding utf8).Trim() -eq "$PID")) {
            Remove-Item -LiteralPath $runtimeFile -Force -ErrorAction SilentlyContinue
        }
    } catch { }
    Write-Log ("resident: stopped after {0} round(s)" -f $roundIndex) 'INFO'
}
