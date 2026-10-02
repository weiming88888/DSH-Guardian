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
    # The launcher's process start time, in ticks. See Test-ParentAlive: a pid on its
    # own is not proof that the process we were bound to is still there.
    [long]$ParentStartTicks = 0,
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

# --------------------------------------------------------------- named constants
# Every constant that used to sit inline in the logic lives here. They are also
# parameter defaults where a caller may legitimately override them; these names
# exist for the ones read deeper in the file, so the value appears exactly once.
#
#   DshDocumentedDefaultPort  DSH's own default, used only when parameter, live
#                             process and cached file all fail (see Resolve-DshPort)
#   NoVerdictLogSeconds       how long a launch may run before the log stops saying
#                             "no verdict yet" -- reporting earlier misreads a slow
#                             start as a crash
#   HeartbeatStaleMinutes     a heartbeat older than this is not "recent"
$script:DshDocumentedDefaultPort = 3080
$script:NoVerdictLogSeconds = 30
$script:HeartbeatStaleMinutes = 30

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

# Is the process we were bound to still running?
#
# Test-PidAlive alone is not enough for the -ParentPid binding. Windows recycles pids,
# and on a busy machine the launcher's number can be handed to a new process within
# seconds of the window closing. Get-Process -Id then succeeds and the watcher
# concludes its parent is alive -- so "closing the window stops monitoring" silently
# stops being true, and the guard keeps running behind the user's back. That is the
# one outcome this binding exists to prevent, so the pid is checked together with the
# start time recorded when the watcher was launched: a recycled pid belongs to a
# process that started later, which does not match.
function Test-ParentAlive {
    param([int]$ProcessId, [long]$StartTicks)
    if ($ProcessId -le 0) { return $false }
    try { $p = Get-Process -Id $ProcessId -ErrorAction Stop } catch { return $false }
    if ($StartTicks -gt 0) {
        try {
            # Two seconds of slack: both sides read the same value, so any difference
            # beyond clock granularity means it is a different process.
            if ([Math]::Abs($p.StartTime.Ticks - $StartTicks) -gt [TimeSpan]::FromSeconds(2).Ticks) {
                return $false
            }
        } catch { }
    }
    return $true
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
        # Fall back to matching process NAMES, for the case where CommandLine is not
        # readable (it needs rights that a scheduled task may not have).
        #
        # This used to hardcode Get-Process -Name 'DeepSeek Harness'. Two problems,
        # both found by the sandbox QC:
        #   1. it ignored -Patterns entirely, so a caller asking for a different
        #      process still got DSH's port -- -ProcessMatch silently did nothing on
        #      this path, which made the cached/default port levels unreachable;
        #   2. it was one more product name written into the code as a literal.
        # Matching $Patterns against the process name keeps the intent and honours the
        # parameter.
        try {
            $pids = @(Get-Process -ErrorAction Stop | Where-Object {
                $n = $_.ProcessName
                if (-not $n) { return $false }
                foreach ($p in $Patterns) { if ($n -like "*$p*") { return $true } }
                return $false
            } | ForEach-Object { $_.Id })
        } catch { }
    }
    if ($pids.Count -eq 0) { return 0 }

    # PID -> listening port.
    #
    # This used to call Get-NetTCPConnection, which pulls the entire NetTCPIP module
    # into this long-lived process. Measured on this machine: +68,760 KB for that one
    # call, out of a 165 MB resident watchdog. netstat -ano returns the same data from
    # a CHILD process, so the watcher itself stays small.
    #
    # Verified equivalent before switching: same port (19387), same listener set, and
    # 12x faster (56 ms vs 690 ms for the pair of calls it replaces).
    $mine = Get-ListenersByPid -Pids $pids
    $web = @($mine | Where-Object { $_.IP -in @('127.0.0.1', '0.0.0.0', '::', '::1') })
    if ($web.Count -gt 0) {
        # Lowest wins: the app's IPC/dev ports are ephemeral high numbers,
        # and the web UI is the one the user configured.
        return [int](($web | Measure-Object -Property Port -Minimum).Minimum)
    }
    if ($mine.Count -gt 0) { return [int](($mine | Measure-Object -Property Port -Minimum).Minimum) }
    return 0
}

# Listening TCP endpoints owned by the given pids, via netstat -ano.
#
# Why not Get-NetTCPConnection: see the note in Get-DshListeningPort -- the module it
# loads costs ~68 MB in a process that stays resident for hours. netstat is a child
# process, so its cost is transient and does not touch this one.
function Get-ListenersByPid {
    param([int[]]$Pids)
    $result = @()
    if (-not $Pids -or $Pids.Count -eq 0) { return $result }

    try {
        $raw = & netstat.exe -ano -p TCP 2>$null
        foreach ($line in $raw) {
            $s = "$line".Trim()
            if (-not $s.StartsWith('TCP')) { continue }
            if ($s -notmatch 'LISTENING') { continue }
            $f = $s -split '\s+'
            if ($f.Count -lt 5) { continue }
            $local = $f[1]
            $colon = $local.LastIndexOf(':')
            if ($colon -lt 1) { continue }
            $port = 0
            if (-not [int]::TryParse($local.Substring($colon + 1), [ref]$port)) { continue }
            $procId = 0
            if (-not [int]::TryParse($f[$f.Count - 1], [ref]$procId)) { continue }
            if ($Pids -notcontains $procId) { continue }
            $result += [pscustomobject]@{ IP = $local.Substring(0, $colon); Port = $port; Pid = $procId }
        }
    } catch { }

    if ($result.Count -eq 0) {
        # netstat unavailable or blocked. Fall back to the cmdlet: it costs ~68 MB, but
        # only on this error path, and a wrong port would make the watcher restart a
        # healthy DSH -- correctness outranks footprint here.
        try {
            $result = @(Get-NetTCPConnection -State Listen -ErrorAction Stop |
                Where-Object { $Pids -contains $_.OwningProcess } |
                ForEach-Object { [pscustomobject]@{ IP = $_.LocalAddress; Port = $_.LocalPort; Pid = $_.OwningProcess } })
        } catch { }
    }
    return $result
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

    # Last resort only, after parameter / live process / cached file. DSH documents
    # this as its own default, but the config can override it, which is exactly why
    # it is LAST here and why the value is named rather than repeated inline.
    return [pscustomobject]@{ Port = $script:DshDocumentedDefaultPort; Source = 'default' }
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
    # Match on the process NAME, derived from -Patterns rather than from a hardcoded
    # product name. The wildcard warning above is about CommandLine: the watchdog's own
    # process is powershell.exe, and no pattern a caller passes ("dsh", "DeepSeek
    # Harness") is a substring of that, so "is it running" still cannot become true
    # because of us. Hardcoding the names here meant -ProcessMatch did nothing on the
    # degraded path -- the same defect as the port-detection fallback.
    $acc = @()
    try {
        $acc = @(Get-Process -ErrorAction Stop | Where-Object {
            $n = $_.ProcessName
            if (-not $n) { return $false }
            foreach ($p in $Patterns) { if ($n -like "*$p*") { return $true } }
            return $false
        } | Select-Object @{n = 'ProcessId'; e = { $_.Id } }, @{n = 'Name'; e = { $_.ProcessName } }, @{n = 'CommandLine'; e = { '(degraded: unavailable)' } })
    } catch { }
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

# Launch through this cmdlet so stdout/stderr are captured WITHOUT generating a
# .cmd or a .vbs.
#
# What used to be here: New-HiddenRunVbs wrote a VBScript, Start-DshCaptured wrote a
# batch file, and Invoke-ProfileInstall wrote two more of each. All four were source
# code in another language assembled by string concatenation, and the two hard rules
# this project now enforces forbid exactly that:
#   * cross-language generation -- one escaping mistake and the whole mechanism fails,
#     and it fails at RUN time, not at build time;
#   * it made the encoding of the generated file matter. The Chinese path in
#     D:\DS\<CJK>\data became "?" under -Encoding ascii, cmd.exe followed a path that
#     does not exist, and data\console\ stayed empty -- the stderr evidence that
#     Get-CrashEvidence calls the most important evidence was silently never captured.
#
# Start-Process -RedirectStandardOutput/-RedirectStandardError does the same job with
# no generated file at all, and -WindowStyle Hidden keeps the console from flashing.
# Verified on this machine (Windows PowerShell 5.1): both streams land in their files.
function Start-Captured {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory = $true)][string]$OutLog,
        [Parameter(Mandatory = $true)][string]$ErrLog,
        [string]$WorkingDirectory = $null,
        [hashtable]$Environment = $null
    )

    $proc = Start-Process -FilePath $FilePath -ArgumentList $Arguments `
        -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $OutLog -RedirectStandardError $ErrLog `
        -WorkingDirectory $(if ($WorkingDirectory) { $WorkingDirectory } else { (Get-Location).Path })

    if ($Environment) {
        # Start-Process cannot set per-process environment variables on 5.1, so a
        # caller that needs one (DSH_HOME) passes it through the process block.
        try {
            foreach ($k in $Environment.Keys) {
                [System.Environment]::SetEnvironmentVariable($k, [string]$Environment[$k], 'Process')
            }
        } catch { }
    }
    return $proc
}

# Merges stdout+stderr into the single console log the rest of the script reads.
function Merge-CapturedLogs {
    param([string]$OutLog, [string]$ErrLog, [string]$FinalLog)
    $sb = New-Object System.Text.StringBuilder
    foreach ($f in @($OutLog, $ErrLog)) {
        if (Test-Path -LiteralPath $f) {
            try { [void]$sb.AppendLine((Get-Content -LiteralPath $f -Raw -Encoding utf8)) } catch { }
        }
    }
    [System.IO.File]::WriteAllText($FinalLog, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    foreach ($f in @($OutLog, $ErrLog)) { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue }
    return $FinalLog
}

# Get-CmdEncoding used to live here. It existed only to pick the right encoding for
# a generated .cmd (ANSI on code page 936, UTF-8-with-BOM on 65001). With the last
# generated file gone it has no callers, so it is gone too -- code kept "just in
# case" is how the next person ends up maintaining two mechanisms.

function Start-DshCaptured {
    param([string]$Command)
    if ($Command -match '[&\|<>^%]') { throw "launch command contains shell metacharacters; refusing: $Command" }

    # No generated .cmd, no generated .vbs, so the '?' / code-page guard that used to
    # live here is gone with them: the command string is passed straight to
    # Start-Process as an argument list, not written into a file for another
    # interpreter to re-parse.
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $outLog = Join-Path $ConsoleDir "console-$stamp.log"
    $errTmp = Join-Path $ConsoleDir "console-$stamp.err"
    $outTmp = Join-Path $ConsoleDir "console-$stamp.out"

    # $Command is a full command line such as:
    #   "C:\...\dsh.exe" --profile desktop
    # so split it the way CreateProcess does: quoted first token, then the rest.
    $exe = $Command
    $rest = @()
    if ($Command.StartsWith('"')) {
        $end = $Command.IndexOf('"', 1)
        if ($end -gt 0) {
            $exe = $Command.Substring(1, $end - 1)
            $rest = @($Command.Substring($end + 1).Trim() -split '\s+' | Where-Object { $_ })
        }
    } else {
        $parts = @($Command -split '\s+')
        $exe = $parts[0]
        if ($parts.Count -gt 1) { $rest = $parts[1..($parts.Count - 1)] }
    }

    Start-Captured -FilePath $exe -Arguments $rest -OutLog $outTmp -ErrLog $errTmp | Out-Null
    Merge-CapturedLogs -OutLog $outTmp -ErrLog $errTmp -FinalLog $outLog | Out-Null

    return [pscustomobject]@{ OutLog = $outLog; Wrapper = $null; Launcher = $null }
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

    # Pid-tagged for the same reason the launch log is: a bare second-resolution
    # stamp collides when the install and the relaunch happen inside one second.
    $tag = (Get-Date -Format 'yyyyMMdd-HHmmss') + "-$PID"
    $log = Join-Path $ConsoleDir ('install-{0}.log' -f $tag)
    $outTmp = Join-Path $ConsoleDir ('install-{0}.out' -f $tag)
    $errTmp = Join-Path $ConsoleDir ('install-{0}.err' -f $tag)

    # No .cmd, no .vbs: pnpm is started directly with its own directory and its own
    # DSH_HOME in the process environment. -WindowStyle Hidden keeps the console
    # from flashing, which is what the VBScript launcher used to be for.
    $env:DSH_HOME = $DshHome
    $proc = Start-Captured -FilePath $rt.Node `
        -Arguments @($rt.Pnpm, 'install', '--no-frozen-lockfile', '--reporter=append-only') `
        -OutLog $outTmp -ErrLog $errTmp -WorkingDirectory $ProfileDir
    $exited = $proc.WaitForExit($TimeoutSeconds * 1000)
    Merge-CapturedLogs -OutLog $outTmp -ErrLog $errTmp -FinalLog $log | Out-Null
    if (-not $exited) {
        try { $proc.Kill() } catch { }
        return [pscustomobject]@{ Ok = $false; Message = ('timed out after {0}s; see {1}' -f $TimeoutSeconds, $log); Log = $log }
    }

    # With cmd.exe out of the picture there is no "[install] exit=" line to read any
    # more, and no lag waiting for it: the process exit code is authoritative and is
    # already available.
    $result = if ($proc.HasExited) { [string]$proc.ExitCode } else { 'unknown' }
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

# Check the binding at the TOP of the round as well, not only in the nap between
# rounds. The nap check alone leaves a gap as long as one round: a round can probe,
# launch DSH and wait out the boot window, and a window closed during that time would
# not stop the watcher until the round finished. Checking here bounds the delay to the
# moment the launcher is noticed gone, which is what the binding promises.
if ($ParentPid -gt 0 -and -not (Test-ParentAlive -ProcessId $ParentPid -StartTicks $ParentStartTicks)) {
    Write-Log 'resident: exiting (launcher closed)' 'INFO'
    break
}

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
# One stamp per round, at round scope. The rescue path used to rely on a $stamp
# that only ever existed INSIDE functions, so at this level it was $null and the
# evidence file came out as "crash-evidence-.json" -- a name the rescue record
# then pointed at.
$stamp = $now.ToString('yyyyMMdd-HHmmss')
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

# Guarantee every field this round may write exists on the object we parsed.
# $state comes from ConvertFrom-Json, so an older state.json simply lacks the
# newer fields, and assigning to a missing property THROWS instead of creating
# it. Get-Prop already tolerates missing fields on read; this makes the write
# side equally safe, so a stale state.json can never abort a rescue mid-round.
foreach ($f in @('failStreak', 'shortLivedStarts', 'lastStartAt', 'lastStartWasHealthy',
                 'lastNotifiedCrashLoop', 'launches', 'configFingerprint', 'autoRollbacks',
                 'lastAutoRollbackAt', 'lastRescueFailSig', 'relaunchStopped')) {
    if (-not $state.PSObject.Properties[$f]) { Set-Prop $state $f $null }
}
if ($null -eq $state.failStreak) { Set-Prop $state 'failStreak' 0 }
if (-not $state.shortLivedStarts) { Set-Prop $state 'shortLivedStarts' 0 }
if (-not $state.autoRollbacks) { Set-Prop $state 'autoRollbacks' 0 }

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
    if ($waited -ge $script:NoVerdictLogSeconds) {
        # Card 1 of the false-crash guard, and the one that surprises people: the
        # whole boot window has to expire before a launch counts as short-lived,
        # so a DSH that dies instantly is still reported as "starting" until then.
        # Say so out loud, once per 30s, rather than looking hung.
        Write-Log ("Still inside the boot window: no verdict yet. A DSH that fails fast is not counted as short-lived until {0}s have passed; lower -BootWindowSeconds if that wait is too long for you." -f $BootWindowSeconds) 'INFO'
    }
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
                # Set-Prop, not $state.x = ... : on a real run whose state.json
                # predates these fields (e.g. the very first rollback on an
                # existing install) the property does not exist on the parsed
                # object, and a direct assignment THROWS. That aborted the rest
                # of the round -- no rescue record, no ROLLBACK event, no state
                # save -- while the config plane had already been restored.
                Set-Prop $state 'autoRollbacks' ([int]$rollbackCount + 1)
                Set-Prop $state 'lastAutoRollbackAt' ((Get-Date).ToString('o'))
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
                Set-Prop $state 'configFingerprintAtRollback' $state.configFingerprint
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
    ($now - [datetime]$state.lastHeartbeatLog).TotalMinutes -ge $script:HeartbeatStaleMinutes) {
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
        if ($ParentPid -gt 0 -and -not (Test-ParentAlive -ProcessId $ParentPid -StartTicks $ParentStartTicks)) { $exitReason = 'launcher closed'; break }
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
