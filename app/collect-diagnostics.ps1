<#
.SYNOPSIS
  DSH Guardian diagnostic collector.
.DESCRIPTION
  Packs everything needed to diagnose a problem into one text file, in Chinese.

  Read-only: changes no config, starts/stops nothing, touches no registry.
  The only file written is the report on the desktop.

  WHY THIS FILE IS PURE ASCII
    Windows PowerShell 5.1 reads BOM-less .ps1 using the system ANSI code page,
    so a Chinese literal in the source would be corrupted and break parsing.
    All Chinese text below is therefore stored as UTF-8 hex and decoded at
    runtime with function T. Keep it that way when editing.

  Run it by double-clicking launch-diagnostics.cmd (or the Chinese shortcut).
#>
[CmdletBinding()]
param(
    [string]$DataDir = '',
    [string]$OutDir = '',
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Continue'

# UTF-8 hex table for every Chinese string this script prints.
$ZH = @{
  L_report = '44534820477561726469616e20e8af8ae696ade68aa5e5918a'
  L_gen = 'e7949fe68890e697b6e997b4'
  L_send = 'e8afb7e68a8ae695b4e4b8aae69687e4bbb6e58f91e7bb99e68891e58db3e58fafe38082e9878ce99da2e4b88de590abe4bbbbe4bd95e5af86e7a081e68896e5af86e992a5e38082'
  L_locs = '312e20e7a88be5ba8fe4b88ee695b0e68daee4bd8de7bdae'
  L_sym = 'e7aca6e58fb7'
  L_prog = 'e7a88be5ba8fe79baee5bd95'
  L_data = 'e695b0e68daee79baee5bd95'
  L_exe = 'e4b8bbe7a88be5ba8fe5ad98e59ca8'
  L_exesz = 'e4b8bbe7a88be5ba8fe5a4a7e5b08f'
  L_subsys = 'e4b8bbe7a88be5ba8fe7b1bbe59e8b'
  L_sub2 = 'e697a0e68ea7e588b6e58fb0efbc88e4b88de4bc9ae5bcb9e7aa97efbc89'
  L_sub3 = 'e68ea7e588b6e58fb0e7a88be5ba8fefbc88e4bc9ae5bcb9e7aa97efbc81efbc89'
  L_subsysbad = 'e8afbbe58f96e5a4b1e8b4a5'
  L_wdmtime = 'e79b91e8a786e8849ae69cace4bfaee694b9e697b6e997b4'
  L_wdnonascii = 'e79b91e8a786e8849ae69cace99d9e4153434949e5ad97e88a82e695b0efbc88e5bf85e9a1bbe4b8ba30efbc89'
  L_state = '322e20e5bd93e5898de78ab6e68081'
  L_mode = 'e887aae58aa8e6a380e69fa5e6a8a1e5bc8f'
  L_modeno = 'e6b2a1e69c89206d6f64652e6a736f6eefbc88e4bb8ee69caae5bc80e590afe8bf87efbc89'
  L_modebad = 'e8afbbe58f96e5a4b1e8b4a5'
  L_wpid = 'e79b91e8a786e599a8e8bf9be7a88be58fb7'
  L_walive = 'e5ad98e6b4bb'
  L_wnone = 'e6b2a1e69c892072756e74696d652e70696420e28094e2809420e5bd93e5898de6b2a1e69c89e59ca8e79b91e8a786'
  L_shortcut = 'e6a18ce99da2e5bfabe68db7e696b9e5bc8f'
  L_scmiss = 'e5bfabe68db7e696b9e5bc8fe4b88de5ad98e59ca8'
  L_scbad = 'e8afbbe58f96e5a4b1e8b4a5'
  L_auto = 'e8aea1e58892e4bbbbe58aa1e4b88ee5bc80e69cbae590afe58aa8efbc88e983bde5ba94e8afa5e6b2a1e69c89efbc89'
  L_task = 'e8aea1e58892e4bbbbe58aa1204453482d477561726469616e2d5761746368646f67'
  L_taskhas = 'e5ad98e59ca8efbc88e4b88de5ba94e8afa5e5ad98e59ca8efbc81efbc89'
  L_tasknone = 'e697a0efbc88e6ada3e7a1aeefbc89'
  L_taskq = 'e69fa5e8afa2e5a4b1e8b4a5'
  L_startup = 'e590afe58aa8e69687e4bbb6e5a4b9e9878ce79a8420477561726469616e20e9a1b9efbc88e5ba94e4b8ba2030efbc89'
  L_procs = 'e79bb8e585b3e8bf9be7a88b'
  L_noproc = 'e6b2a1e69c89e79c8be997a8e78b97e8bf9be7a88be59ca8e8bf90e8a18c'
  L_procq = 'e8bf9be7a88be69fa5e8afa2e5a4b1e8b4a5'
  L_dsh = '332e2044534820e78ab6e68081'
  L_profdir = '70726f66696c6520e79baee5bd95'
  L_missing = 'e4b88de5ad98e59ca8'
  L_bytes = 'e5ad97e88a82'
  L_modified = 'e4bfaee694b9e4ba8e'
  L_bundles = 'e5b7b2e8a385e68f92e4bbb62f62756e646c6573'
  L_pkgbad = '7061636b6167652e6a736f6e20e8afbbe58f96e5a4b1e8b4a5'
  L_port = 'e7abafe58fa3e68ea2e6b58befbc88e58faae8afbbefbc89'
  L_portauto = 'e58099e98089e7abafe58fa3efbc88e887aae58aa8e68ea2e6b58befbc89'
  L_listen = 'e59ca8e79b91e590acefbc8844534820e6b4bbe79d80efbc89'
  L_nolisten = 'e6b2a1e69c89e4babae59ca8e79b91e590ac'
  L_logs = '342e20e697a5e5bf97efbc88e69cabe5b0bee983a8e58886efbc89'
  L_ev = '352e20e5b4a9e6ba83e8af81e68dae'
  L_evnone = 'e6b2a1e69c89e5b4a9e6ba83e8af81e68daee69687e4bbb620e28094e2809420e8afb4e6988ee8bf98e6b2a1e5b4a9e8bf87'
  L_snaps = '362e20e5bfabe785a7e58897e8a1a8'
  L_snapnone = 'e6b2a1e69c8920736e617073686f747320e79baee5bd95'
  L_out = 'e8af8ae696ade68aa5e5918ae5b7b2e7949fe68890efbc9a'
  L_reportdir = 'e8af8ae696ade68aa5e5918a'
  L_reportname = 'e8af8ae696ade68aa5e5918a2d7b307d2e747874'
  L_outdir = 'e68aa5e5918ae68980e59ca8e69687e4bbb6e5a4b9'
  L_outfile = 'e68aa5e5918ae69687e4bbb6'
  L_openfolder = 'e5b7b2e68993e5bc80e8afa5e69687e4bbb6e5a4b9efbc8ce68aa5e5918ae5b0b1e59ca8e9878ce99da2e38082'
  L_childerr = 'e5ad90e8849ae69cace68aa5e99499e8be93e587ba'
  L_menuerror = 'e88f9ce58d95e99499e8afafe8aeb0e5bd95'
  L_outsend = 'e8afb7e68a8ae8bf99e4b8aae69687e4bbb6e58f91e7bb99e68891efbc88e68b96e8bf9be5afb9e8af9de6a186efbc8ce68896e5a48de588b6e585a8e69687efbc89e38082'
  L_outno = 'e69687e4bbb6e9878ce4b88de590abe5af86e7a081e38081e5af86e992a5e68896e8b4a6e58fb7e4bfa1e681afe38082'
  L_writefail = 'e58699e585a5e5a4b1e8b4a5'
  L_readfail = 'e8afbbe58f96e5a4b1e8b4a5'
  L_filemiss = 'e69687e4bbb6e4b88de5ad98e59ca8'
}

# Decodes one table entry. Kept tiny: this runs only a few dozen times.
function T {
    param([string]$Key)
    $hex = $ZH[$Key]
    if (-not $hex) { return $Key }
    $bytes = New-Object byte[] ($hex.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $bytes[$i] = [Convert]::ToByte($hex.Substring($i * 2, 2), 16)
    }
    return [System.Text.Encoding]::UTF8.GetString($bytes)
}

# $PSScriptRoot is empty for some -File invocations; MyInvocation is reliable.
$here = $PSScriptRoot
if (-not $here) { try { $here = Split-Path -Parent $MyInvocation.MyCommand.Path } catch { } }
if (-not $here) { $here = (Get-Location).Path }

if (-not $DataDir) { $DataDir = Join-Path (Split-Path -Parent $here) 'data' }

# The report is written INSIDE the tool folder, not to the desktop: the user
# sends the whole folder, and the desktop is left alone. One file per run, named
# with a timestamp so consecutive runs do not overwrite each other.
if (-not $OutDir) { $OutDir = Join-Path $DataDir (T 'L_reportdir') }
if (-not $OutFile) {
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $OutFile = Join-Path $OutDir (T 'L_reportname').Replace('{0}', $stamp)
}
try { New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutFile) | Out-Null } catch { }

$sb = New-Object System.Text.StringBuilder
function Add-Line { param([string]$Text = '') [void]$sb.AppendLine($Text) }
function Add-Section {
    param([string]$Title)
    Add-Line ''
    Add-Line ('=' * 62)
    Add-Line ('  ' + $Title)
    Add-Line ('=' * 62)
}
function Add-File {
    param([string]$Path, [int]$Tail = 300)
    Add-Line ('--- ' + $Path + ' ---')
    if (-not (Test-Path -LiteralPath $Path)) {
        Add-Line ('(' + (T 'L_filemiss') + ')')
        return
    }
    # Decode the bytes ourselves instead of using Get-Content.
    #
    # Two reasons. (1) Get-Content without -Encoding uses the system ANSI code
    # page for a BOM-less file, and the logs collected here are written as UTF-8
    # without a BOM, so all the Chinese in PowerShell error messages came out as
    # mojibake in the report. (2) Windows PowerShell 5.1's -Encoding parameter
    # takes a FileSystemCmdletProviderEncoding enum, not a System.Text.Encoding
    # object, so the chosen encoding cannot be passed through.
    try {
        $bytes = [System.IO.File]::ReadAllBytes($Path)
        $enc = $null
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
            $enc = New-Object System.Text.UTF8Encoding($false)          # UTF-8 with BOM
        } elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
            $enc = [System.Text.Encoding]::Unicode                      # UTF-16 LE
        } else {
            # No BOM: strict UTF-8 first, and only fall back to ANSI when the
            # bytes really are not valid UTF-8.
            try {
                $strict = New-Object System.Text.UTF8Encoding($false, $true)
                $null = $strict.GetString($bytes)
                $enc = New-Object System.Text.UTF8Encoding($false)
            } catch {
                $enc = [System.Text.Encoding]::Default
            }
        }
        $text = $enc.GetString($bytes)
        # Decoding does not strip a BOM: it arrives as U+FEFF and would show up
        # as a stray character at the start of the first line. Most of these
        # files are written by PowerShell's Set-Content -Encoding utf8, which
        # DOES emit a BOM, so this matters for nearly every log collected.
        if ($text.Length -gt 0 -and [int][char]$text[0] -eq 0xFEFF) {
            $text = $text.Substring(1)
        }
        $lines = $text -split "`r`n|`n|`r"
        # Trailing newline produces one empty element; drop it so -Tail matches
        # what Get-Content would have returned.
        if ($lines.Count -gt 0 -and $lines[$lines.Count - 1] -eq '') {
            $lines = $lines[0..($lines.Count - 2)]
        }
        if ($Tail -gt 0 -and $lines.Count -gt $Tail) {
            $lines = $lines[($lines.Count - $Tail)..($lines.Count - 1)]
        }
        foreach ($l in $lines) { Add-Line $l }
    } catch {
        Add-Line ('(' + (T 'L_readfail') + ': ' + $_.Exception.Message + ')')
    }
}

Add-Line (T 'L_report')
Add-Line ((T 'L_gen') + ' : ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
Add-Line ''
Add-Line (T 'L_send')

# ------------------------------------------------------------ program layout
Add-Section (T 'L_locs')
$exe = Join-Path $here 'dsh-guardian.exe'
$watchdog = Join-Path $here 'dsh-watchdog.ps1'
Add-Line ((T 'L_prog') + ' : ' + $here)
Add-Line ((T 'L_data') + ' : ' + $DataDir)
Add-Line ((T 'L_exe') + ' : ' + (Test-Path -LiteralPath $exe))
if (Test-Path -LiteralPath $exe) {
    $f = Get-Item -LiteralPath $exe
    Add-Line ((T 'L_exesz') + ' : ' + $f.Length + ' ' + (T 'L_bytes') + ' / ' + $f.LastWriteTime)
    try {
        $b = [System.IO.File]::ReadAllBytes($exe)
        $pe = [BitConverter]::ToInt32($b, 0x3C)
        $sub = [BitConverter]::ToUInt16($b, $pe + 24 + 68)
        $subText = if ($sub -eq 2) { T 'L_sub2' } elseif ($sub -eq 3) { T 'L_sub3' } else { $sub }
        Add-Line ((T 'L_subsys') + ' : ' + $sub + '  ' + $subText)
    } catch { Add-Line ((T 'L_subsys') + ' : ' + (T 'L_subsysbad')) }
}
if (Test-Path -LiteralPath $watchdog) {
    Add-Line ((T 'L_wdmtime') + ' : ' + (Get-Item -LiteralPath $watchdog).LastWriteTime)
    $wb = [System.IO.File]::ReadAllBytes($watchdog)
    $nonAscii = ($wb | Where-Object { $_ -gt 127 }).Count
    Add-Line ((T 'L_wdnonascii') + ' : ' + $nonAscii)
}

# ------------------------------------------------------------------- state
Add-Section (T 'L_state')
$modeFile = Join-Path $DataDir 'mode.json'
if (Test-Path -LiteralPath $modeFile) {
    try { Add-Line ((T 'L_mode') + ' : ' + (Get-Content $modeFile -Raw -Encoding utf8 | ConvertFrom-Json).mode) }
    catch { Add-Line ((T 'L_mode') + ' : ' + (T 'L_modebad')) }
} else {
    Add-Line ((T 'L_mode') + ' : ' + (T 'L_modeno'))
}

$pidFile = Join-Path $DataDir 'runtime.pid'
if (Test-Path -LiteralPath $pidFile) {
    $rp = (Get-Content -LiteralPath $pidFile -Raw -Encoding utf8).Trim()
    $alive = $false
    try { $alive = $null -ne (Get-Process -Id ([int]$rp) -ErrorAction Stop) } catch { }
    Add-Line ((T 'L_wpid') + ' : ' + $rp + '   ' + (T 'L_walive') + ': ' + $alive)
} else {
    Add-Line ((T 'L_wpid') + ' : ' + (T 'L_wnone'))
}

foreach ($n in @('last-tick.json', 'last-known-good.json')) {
    Add-Line ''
    Add-File -Path (Join-Path $DataDir $n) -Tail 20
}

Add-Line ''
Add-Line ('--- ' + (T 'L_shortcut') + ' ---')
try {
    $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'DSH Guardian.lnk'
    if (Test-Path -LiteralPath $lnk) {
        Add-Line ('    ' + (New-Object -ComObject WScript.Shell).CreateShortcut($lnk).TargetPath)
    } else { Add-Line ('    ' + (T 'L_scmiss')) }
} catch { Add-Line ('    ' + (T 'L_scbad')) }

Add-Line ''
Add-Line ('--- ' + (T 'L_auto') + ' ---')
try {
    $t = Get-ScheduledTask -TaskName 'DSH-Guardian-Watchdog' -ErrorAction SilentlyContinue
    Add-Line ('    ' + (T 'L_task') + ' : ' + $(if ($t) { T 'L_taskhas' } else { T 'L_tasknone' }))
} catch { Add-Line ('    ' + (T 'L_task') + ' : ' + (T 'L_taskq')) }
try {
    $su = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
    $n = @(Get-ChildItem -LiteralPath $su -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*uardian*' }).Count
    Add-Line ('    ' + (T 'L_startup') + ' : ' + $n)
} catch { }

Add-Line ''
Add-Line ('--- ' + (T 'L_procs') + ' ---')
try {
    $procs = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*dsh-watchdog*' }
    if (@($procs).Count -eq 0) { Add-Line ('    ' + (T 'L_noproc')) }
    foreach ($p in $procs) {
        Add-Line ('    pid ' + $p.ProcessId + ' : ' + $p.CommandLine.Substring(0, [Math]::Min(300, $p.CommandLine.Length)))
    }
} catch { Add-Line ('    ' + (T 'L_procq')) }

# ------------------------------------------------------------------ DSH state
Add-Section (T 'L_dsh')
$profileDir = Join-Path (Join-Path $env:USERPROFILE '.dsh') 'profiles\desktop'
Add-Line ((T 'L_profdir') + ' : ' + $profileDir)
if (Test-Path -LiteralPath $profileDir) {
    foreach ($n in @('package.json', 'pnpm-lock.yaml', 'cordis.patch.yml')) {
        $p = Join-Path $profileDir $n
        if (Test-Path -LiteralPath $p) {
            $i = Get-Item -LiteralPath $p
            Add-Line ('    {0,-20} {1} {2} / {3}' -f $n, $i.Length, (T 'L_bytes'), $i.LastWriteTime)
        } else {
            Add-Line ('    {0,-20} {1}' -f $n, (T 'L_missing'))
        }
    }
    try {
        $pkg = Get-Content -LiteralPath (Join-Path $profileDir 'package.json') -Raw -Encoding utf8 | ConvertFrom-Json
        Add-Line ('    ' + (T 'L_bundles') + ' : ' + ($pkg.dsh.profile.bundles -join ', '))
    } catch { Add-Line ('    ' + (T 'L_pkgbad')) }
} else {
    Add-Line ('    ' + (T 'L_missing'))
}

Add-Line ''
Add-Line ('--- ' + (T 'L_port') + ' ---')

# Auto-detected, so the report stays useful when DSH runs on a non-default port.
# Same source order as the watchdog: live process, cached data\port.txt, then
# the two known defaults.
function Get-DiagListeningPort {
    $pids = @()
    try {
        $rows = Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
            $_.Name -like '*DeepSeek Harness*' -or $_.CommandLine -like '*dsh*'
        }
        if ($rows) { $pids = @($rows | ForEach-Object { $_.ProcessId }) }
    } catch { }
    if ($pids.Count -eq 0) {
        try { $pids = @(Get-Process -Name 'DeepSeek Harness' -ErrorAction Stop | ForEach-Object { $_.Id }) } catch { }
    }
    if ($pids.Count -eq 0) { return 0 }
    try {
        $conns = Get-NetTCPConnection -State Listen -ErrorAction Stop |
            Where-Object { $pids -contains $_.OwningProcess }
        if ($conns) { return [int](($conns | Measure-Object -Property LocalPort -Minimum).Minimum) }
    } catch { }
    return 0
}

$cachedPort = 0
$cacheFile = Join-Path $DataDir 'port.txt'
if (Test-Path -LiteralPath $cacheFile) {
    try {
        [void][int]::TryParse((Get-Content -LiteralPath $cacheFile -Raw).Trim(), [ref]$cachedPort)
    } catch { }
}

$candidates = @()
$seenPorts = @()
foreach ($src in @(
        @{ Port = (Get-DiagListeningPort); Src = 'live-process' },
        @{ Port = $cachedPort; Src = 'cached' },
        @{ Port = 3080; Src = 'default' },
        @{ Port = 19387; Src = 'default' })) {
    # First writer wins, so a detected port keeps its real source label. Do NOT
    # deduplicate with Sort-Object -Unique: it drops the earlier entry and the
    # report then claims a live port came from 'default'.
    if ($src.Port -gt 0 -and $seenPorts -notcontains $src.Port) {
        $seenPorts += $src.Port
        $candidates += [pscustomobject]@{ Port = $src.Port; Src = $src.Src }
    }
}

Add-Line ('    ' + (T 'L_portauto'))
foreach ($cand in $candidates) {
    $port = $cand.Port
    $c = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $c.BeginConnect('127.0.0.1', $port, $null, $null)
        if ($iar.AsyncWaitHandle.WaitOne(1000, $false)) {
            $c.EndConnect($iar)
            Add-Line ('    127.0.0.1:' + $port + ' ' + (T 'L_listen') + '  [' + $cand.Src + ']')
        } else {
            Add-Line ('    127.0.0.1:' + $port + ' ' + (T 'L_nolisten') + '  [' + $cand.Src + ']')
        }
    } catch { Add-Line ('    127.0.0.1:' + $port + ' ' + (T 'L_nolisten') + '  [' + $cand.Src + ']') }
    finally { $c.Close() }
}

# -------------------------------------------------------------------- logs
Add-Section (T 'L_logs')
Add-File -Path (Join-Path $DataDir 'exe-trace.log') -Tail 60
Add-Line ''
Add-File -Path (Join-Path $DataDir 'watchdog.log') -Tail 200
Add-Line ''
Add-File -Path (Join-Path $DataDir 'events.jsonl') -Tail 100
Add-Line ''
Add-File -Path (Join-Path $DataDir 'dsh-guardian.out.log') -Tail 60
Add-Line ''
Add-Line ('--- ' + (T 'L_childerr') + ' ---')
Add-File -Path (Join-Path $DataDir 'child-stderr.log') -Tail 100
Add-Line ''
Add-Line ('--- ' + (T 'L_menuerror') + ' ---')
Add-File -Path (Join-Path $DataDir 'menu-error.log') -Tail 100

Add-Section (T 'L_ev')
$evDir = @(Get-ChildItem -LiteralPath $DataDir -Filter 'crash-evidence-*.json' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 2)
if ($evDir.Count -eq 0) { Add-Line ('(' + (T 'L_evnone') + ')') }
foreach ($f in $evDir) { Add-Line ''; Add-File -Path $f.FullName -Tail 80 }

Add-Section (T 'L_snaps')
$snapRoot = Join-Path $DataDir 'snapshots'
if (Test-Path -LiteralPath $snapRoot) {
    Get-ChildItem -LiteralPath $snapRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | ForEach-Object {
            Add-Line ('    {0,-42} {1}' -f $_.Name, $_.LastWriteTime)
        }
} else { Add-Line ('(' + (T 'L_snapnone') + ')') }

# -------------------------------------------------------------------- write
try {
    $sb.ToString() | Set-Content -LiteralPath $OutFile -Encoding utf8
    Write-Host ''
    Write-Host (T 'L_out')
    Write-Host ('  ' + $OutFile)
    Write-Host ''
    # Open the report, and also the containing folder, so the file is easy to
    # find and easy to send along with the rest of the tool folder.
    try { Start-Process -FilePath 'notepad.exe' -ArgumentList ('"' + $OutFile + '"') } catch { }
    try {
        Start-Process -FilePath 'explorer.exe' -ArgumentList ('"' + (Split-Path -Parent $OutFile) + '"')
        Write-Host (T 'L_openfolder')
    } catch { }
    Write-Host ''
    Write-Host (T 'L_outsend')
    Write-Host (T 'L_outno')
    Write-Host ''
} catch {
    Write-Host ((T 'L_writefail') + ': ' + $_.Exception.Message)
}