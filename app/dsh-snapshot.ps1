<#
.SYNOPSIS
  DSH profile config-plane snapshot and rollback. Manual, never automatic.

.DESCRIPTION
  Why this exists: DSH has no user-facing snapshot, backup, or --rollback mechanism.
  Its only automatic protection covers the install operation itself failing: an
  install-time restore puts package.json and pnpm-lock.yaml back the way they were.
  It does NOT cover "the plugin installed cleanly and then crashed at runtime".
  This script captures the config plane at a moment you choose, and before any
  rollback it saves the current state aside so the change is reversible.

  Snapshot contents (config only, never node_modules - that is pnpm's job):
    package.json          plugin dependencies + ordered dsh.profile.bundles (the core)
    pnpm-lock.yaml        exact resolution
    cordis.patch.yml      user patch layer (enable/disable and overrides)
    cordis.yml            base composed config
    pnpm-workspace.yaml   workspace definition
    compatibility.json    version gate record, if present

  Safety design:
    - Restore without -Force only prints what it would do and stops.
    - Before writing anything, the current state is copied to pre-restore-<stamp>.
    - Snapshots are never deleted; node_modules is never touched.

  Actions:
    Create      take a new snapshot (adds one; older ones are KEPT)
    Mark-Good   Create + mark healthy + point auto-rollback at it (menu key 3)
    List        show every snapshot and mark the active rollback target
    Promote     point auto-rollback at an EXISTING snapshot (menu key 5).
                Use this to go back further than the newest baseline, e.g.
                past several plugin installs instead of only the last one.
    Verify      check a snapshot's files against the current profile
    Restore     apply a snapshot (defaults to the active target)

  Only ONE snapshot is the active rollback target at a time; it is recorded in
  last-known-good.json next to the snapshots directory. Keeping many snapshots
  costs a few tens of KB each, so there is no pruning.

  NOTE: this file is deliberately pure ASCII. Windows PowerShell 5.1 reads
  BOM-less .ps1 files using the system ANSI code page, which corrupts non-ASCII
  literals and breaks parsing. Keep it ASCII.
#>
[CmdletBinding()]
param(
    [ValidateSet('Create', 'List', 'Verify', 'Restore', 'Mark-Good', 'Promote', 'Delete')]
    [string]$Action = 'Create',
    # -DataDir takes precedence and must be passed explicitly when the caller
    # invokes this with -File: under Windows PowerShell 5.1 $PSScriptRoot can be
    # empty in that case, which would make the default below fail to bind.
    [string]$DataDir = '',
    [string]$SnapshotRoot = '',
    [string]$DshHome = (Join-Path $env:USERPROFILE '.dsh'),
    [string]$ProfileName = 'desktop',
    [string]$Snapshot = '',
    [string]$Label = '',
    [switch]$DryRun,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is empty for some -File invocations under Windows PowerShell
# 5.1; $MyInvocation.MyCommand.Path is not.
$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    try { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path } catch { }
}

if (-not $SnapshotRoot) {
    if ($DataDir) { $SnapshotRoot = Join-Path $DataDir 'snapshots' }
    elseif ($scriptDir) { $SnapshotRoot = Join-Path (Split-Path -Parent $scriptDir) 'data\snapshots' }
}

$ProfileDir = Join-Path (Join-Path $DshHome 'profiles') $ProfileName
$ConfigFiles = @('package.json', 'pnpm-lock.yaml', 'cordis.patch.yml', 'cordis.yml', 'pnpm-workspace.yaml', 'compatibility.json')

if (-not (Test-Path -LiteralPath $ProfileDir)) {
    throw "Profile directory not found: $ProfileDir"
}

function Get-ExistingConfigFiles {
    return ($ConfigFiles | Where-Object { Test-Path -LiteralPath (Join-Path $ProfileDir $_) })
}

# Locates the node + pnpm the DSH app uses. Only used to print an accurate
# reconciliation command, because 'dsh plugin --profile desktop install' is
# rejected by the CLI for this profile. Self-contained on purpose: sourcing the
# watchdog here would make it write tick/mode files as a side effect.
function Get-AppPnpm {
    $r = Join-Path $env:LOCALAPPDATA 'Programs\DeepSeek Harness\resources\runtime'
    $pnpm = Join-Path $r 'pnpm\dist\pnpm.mjs'
    if (-not (Test-Path -LiteralPath $pnpm)) { return $null }
    $cands = @(
        (Join-Path $r 'primary-runtime\dependencies\node\bin\node.exe'),
        (Join-Path $r 'node\node.exe'),
        (Join-Path $env:USERPROFILE '.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\node\bin\node.exe')
    )
    foreach ($c in $cands) { if (Test-Path -LiteralPath $c) { return [pscustomobject]@{ Node = $c; Pnpm = $pnpm } } }
    return $null
}

function Resolve-Snapshot {
    # Turns a snapshot name into its directory, with a message a non-technical
    # user can act on. A raw Get-Item error ("Cannot find path ...") tells them
    # nothing about what to do next.
    param([string]$Name)
    $p = Join-Path $SnapshotRoot $Name
    if (-not (Test-Path -LiteralPath $p -PathType Container)) {
        $avail = @(Get-ChildItem -LiteralPath $SnapshotRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like 'snap-*' } | Sort-Object Name -Descending |
            ForEach-Object { $_.Name })
        if ($avail.Count -eq 0) {
            throw ("No such snapshot: {0}. There are no snapshots at all yet - run -Action Create first." -f $Name)
        }
        throw ("No such snapshot: {0}.`r`nAvailable snapshots (newest first):`r`n  {1}" -f $Name, ($avail -join "`r`n  "))
    }
    return (Get-Item -LiteralPath $p)
}

function Get-LatestSnapshot {
    if (-not (Test-Path -LiteralPath $SnapshotRoot)) { return $null }
    return (Get-ChildItem -LiteralPath $SnapshotRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'snap-*' } |
        Sort-Object Name -Descending | Select-Object -First 1)
}

switch ($Action) {

    'List' {
        $all = @(Get-ChildItem -LiteralPath $SnapshotRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like 'snap-*' } | Sort-Object Name -Descending)
        if ($all.Count -eq 0) {
            Write-Host 'No snapshots yet. Run -Action Create first.'
            return
        }
        # Mark which one auto-rollback would actually use.
        $active = ''
        $pf = Join-Path (Split-Path -Parent $SnapshotRoot) 'last-known-good.json'
        if (Test-Path -LiteralPath $pf) {
            try { $active = (Get-Content -LiteralPath $pf -Raw -Encoding utf8 | ConvertFrom-Json).snapshot } catch { }
        }
        Write-Host "Total snapshots: $($all.Count) (newest first)"
        Write-Host 'Snapshots are KEPT, not overwritten. Only one is the active target.'
        Write-Host ''
        foreach ($s in $all) {
            $m = Join-Path $s.FullName 'manifest.json'
            $mark = if ($s.Name -eq $active) { '   <== active rollback target' } else { '' }
            if (Test-Path -LiteralPath $m) {
                $man = Get-Content -LiteralPath $m -Raw -Encoding utf8 | ConvertFrom-Json
                Write-Host ("{0}{1}" -f $s.Name, $mark)
                Write-Host ("  captured : {0}" -f $man.capturedAt)
                Write-Host ("  label    : {0}" -f $man.label)
                Write-Host ("  bundles  : {0}" -f ($man.bundles -join ', '))
                $deps = @($man.dependencies.PSObject.Properties | ForEach-Object { "$($_.Name)@$($_.Value)" })
                Write-Host ("  deps     : {0}" -f ($deps -join ', '))
                Write-Host ''
            } else {
                Write-Host ("{0}{1}  (manifest.json missing)" -f $s.Name, $mark)
                Write-Host ''
            }
        }
    }

    'Create' {
        # Validate before writing anything. Without this the directory was made
        # first and the run then died reading package.json, leaving an empty
        # orphan snapshot behind that shows up in -Action List and looks like a
        # usable baseline although it holds no files at all.
        if (-not (Test-Path -LiteralPath (Join-Path $ProfileDir 'package.json'))) {
            throw ("No package.json in {0} - there is nothing to snapshot. Is this really a DSH profile?" -f $ProfileDir)
        }

        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $safeLabel = if ($Label) { $Label } else { 'none' }
        $name = if ($Label) { "snap-$stamp-$Label" } else { "snap-$stamp" }
        $dest = Join-Path $SnapshotRoot $name

        # The stamp has one-second resolution, so two baselines taken inside the
        # same second produced the same name and the second silently overwrote
        # the first: two different configurations collapsed into one snapshot and
        # the overwritten one became impossible to roll back to. Snapshots exist
        # to be KEPT, so disambiguate instead of clobbering.
        if ((Test-Path -LiteralPath $dest) -and -not $DryRun) {
            $n = 2
            while (Test-Path -LiteralPath (Join-Path $SnapshotRoot ('{0}-{1}' -f $name, $n))) { $n++ }
            $name = '{0}-{1}' -f $name, $n
            $dest = Join-Path $SnapshotRoot $name
        }

        $files = Get-ExistingConfigFiles

        Write-Host "Snapshot to create: $dest"
        foreach ($f in $files) { Write-Host "  + $f" }

        if ($DryRun) {
            Write-Host ''
            Write-Host '[-DryRun] nothing written.'
            return
        }

        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        foreach ($f in $files) {
            Copy-Item -LiteralPath (Join-Path $ProfileDir $f) -Destination (Join-Path $dest $f) -Force
        }

        $pkg = Get-Content -LiteralPath (Join-Path $ProfileDir 'package.json') -Raw -Encoding utf8 | ConvertFrom-Json
        $manifest = [ordered]@{
            capturedAt   = (Get-Date).ToString('o')
            label        = $safeLabel
            profileName  = $ProfileName
            profileDir   = $ProfileDir
            dshHome      = $DshHome
            bundles      = @($pkg.dsh.profile.bundles)
            dependencies = if ($pkg.dependencies) { $pkg.dependencies } else { @{} }
            files        = $files
        }
        ($manifest | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath (Join-Path $dest 'manifest.json') -Encoding utf8

        # Point the watchdog at this snapshot so -AutoRollback has a target.
        # The pointer sits beside the snapshots, i.e. in the data directory.
        $pointerFile = Join-Path (Split-Path -Parent $SnapshotRoot) 'last-known-good.json'
        $pointer = [ordered]@{
            snapshot        = $name
            snapshotRoot    = $SnapshotRoot
            profileName     = $ProfileName
            dshHome         = $DshHome
            bundles         = @($pkg.dsh.profile.bundles)
            dependencies    = if ($pkg.dependencies) { $pkg.dependencies } else { @{} }
            observedHealthy = $false
            createdAt       = (Get-Date).ToString('o')
        }
        ($pointer | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $pointerFile -Encoding utf8

        Write-Host ''
        Write-Host "Snapshot created: $dest"
        Write-Host "Watchdog auto-rollback target: $name"
        Write-Host ("Roll back with: powershell -File ""{0}"" -Action Restore -Snapshot ""{1}""" -f $PSCommandPath, $name)
    }

    'Verify' {
        $snap = if ($Snapshot) { Resolve-Snapshot -Name $Snapshot } else { Get-LatestSnapshot }
        if (-not $snap) { throw 'No snapshot available. Run -Action Create first.' }
        Write-Host ("Comparing against: {0}" -f $snap.Name)
        Write-Host ''
        $anyDiff = $false
        foreach ($f in $ConfigFiles) {
            $cur = Join-Path $ProfileDir $f
            $old = Join-Path $snap.FullName $f
            $curExists = Test-Path -LiteralPath $cur
            $oldExists = Test-Path -LiteralPath $old
            if (-not $curExists -and -not $oldExists) { continue }
            if ($curExists -ne $oldExists) {
                $anyDiff = $true
                Write-Host ("[existence] {0}: current={1} snapshot={2}" -f $f, $curExists, $oldExists)
                continue
            }
            $ch = (Get-FileHash -LiteralPath $cur -Algorithm SHA256).Hash
            $oh = (Get-FileHash -LiteralPath $old -Algorithm SHA256).Hash
            if ($ch -ne $oh) {
                $anyDiff = $true
                Write-Host ("[changed  ] {0}" -f $f)
                if ($f -eq 'package.json' -or $f -eq 'cordis.patch.yml') {
                    Write-Host '  diff (snapshot vs current):'
                    Compare-Object (Get-Content -LiteralPath $old) (Get-Content -LiteralPath $cur) |
                        ForEach-Object { Write-Host ("    {0} {1}" -f $_.SideIndicator, $_.InputObject) }
                }
            } else {
                Write-Host ("[same     ] {0}" -f $f)
            }
        }
        if (-not $anyDiff) {
            Write-Host ''
            Write-Host 'Config plane matches the snapshot exactly.'
        }
    }

    'Restore' {
        $snap = if ($Snapshot) { Resolve-Snapshot -Name $Snapshot } else { Get-LatestSnapshot }
        if (-not $snap) { throw 'No snapshot available. Run -Action Create first.' }

        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $preDir = Join-Path $SnapshotRoot "pre-restore-$stamp"
        # Same reasoning as the snapshot names above: the stamp has one-second
        # resolution, so a second restore inside the same second would have
        # reused this directory and overwritten the first backup -- destroying
        # the only copy of the configuration that restore was meant to preserve.
        if ((Test-Path -LiteralPath $preDir) -and -not $DryRun) {
            $n = 2
            while (Test-Path -LiteralPath (Join-Path $SnapshotRoot ('pre-restore-{0}-{1}' -f $stamp, $n))) { $n++ }
            $preDir = Join-Path $SnapshotRoot ('pre-restore-{0}-{1}' -f $stamp, $n)
        }

        Write-Host ("Restore source: {0}" -f $snap.Name)
        Write-Host ("Current state will be saved to: {0}" -f $preDir)
        Write-Host ''
        foreach ($f in $ConfigFiles) {
            $inSnap = Test-Path -LiteralPath (Join-Path $snap.FullName $f)
            $inCur = Test-Path -LiteralPath (Join-Path $ProfileDir $f)
            if ($inSnap) {
                Write-Host ("  restore {0}  (snapshot -> profile)" -f $f)
            } elseif ($inCur) {
                Write-Host ("  delete  {0}  (absent in snapshot, present now)" -f $f)
            }
        }

        if ($DryRun) {
            Write-Host ''
            Write-Host '[-DryRun] nothing changed. Re-run with -Force to actually roll back.'
            return
        }
        if (-not $Force) {
            Write-Host ''
            Write-Host 'Safety gate: no writes by default.' -ForegroundColor Yellow
            Write-Host 'Pass -Force to roll back, or -DryRun to preview.' -ForegroundColor Yellow
            return
        }

        New-Item -ItemType Directory -Force -Path $preDir | Out-Null
        foreach ($f in (Get-ExistingConfigFiles)) {
            Copy-Item -LiteralPath (Join-Path $ProfileDir $f) -Destination (Join-Path $preDir $f) -Force
        }
        Write-Host ''
        Write-Host ("Current state saved: {0}" -f $preDir)

        foreach ($f in $ConfigFiles) {
            $src = Join-Path $snap.FullName $f
            $dst = Join-Path $ProfileDir $f
            if (Test-Path -LiteralPath $src) {
                Copy-Item -LiteralPath $src -Destination $dst -Force
                Write-Host ("  restored {0}" -f $f)
            } elseif (Test-Path -LiteralPath $dst) {
                Remove-Item -LiteralPath $dst -Force
                Write-Host ("  deleted {0}" -f $f)
            }
        }

        Write-Host ''
        Write-Host ("Config plane rolled back to {0}." -f $snap.Name)
        Write-Host 'node_modules must now be reconciled with the restored lockfile.'
        Write-Host 'NOTE: "dsh plugin --profile desktop install" does NOT work - the CLI'
        Write-Host '      refuses the desktop profile (managed by the Electron app).'
        $rt = Get-AppPnpm
        if ($rt) {
            Write-Host 'Run this (the same pnpm the app uses):'
            Write-Host ('  cd /d "{0}"' -f $ProfileDir)
            Write-Host ('  "{0}" "{1}" install --no-frozen-lockfile' -f $rt.Node, $rt.Pnpm)
        } else {
            Write-Host 'Run "pnpm install" inside the profile directory.'
        }
    }

    'Mark-Good' {
        # Promote the current state to the auto-rollback baseline: snapshot it,
        # then point the watchdog there. Run this once things are verified working.
        $pointerFile = Join-Path (Split-Path -Parent $SnapshotRoot) 'last-known-good.json'
        $label = if ($Label) { $Label } else { 'known-good' }
        & $PSCommandPath -Action Create -Label $label -SnapshotRoot $SnapshotRoot -DshHome $DshHome -ProfileName $ProfileName
        if (Test-Path -LiteralPath $pointerFile) {
            $p = Get-Content -LiteralPath $pointerFile -Raw -Encoding utf8 | ConvertFrom-Json
            $p.observedHealthy = $true
            ($p | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $pointerFile -Encoding utf8
            Write-Host ''
            Write-Host ("Baseline promoted and marked healthy: {0}" -f $p.snapshot)
            Write-Host 'Auto-rollback will target this snapshot if a later plugin causes a crash loop.'
        }
    }

    'Promote' {
        # Point auto-rollback at an EXISTING snapshot instead of creating a new
        # one. This is what lets you go back further than the newest baseline,
        # e.g. past several plugin installs rather than just the last one.
        if (-not $Snapshot) { throw 'Promote needs -Snapshot <name>. Use -Action List to see the names.' }
        $snap = Resolve-Snapshot -Name $Snapshot

        $pointerFile = Join-Path (Split-Path -Parent $SnapshotRoot) 'last-known-good.json'
        $manFile = Join-Path $snap.FullName 'manifest.json'
        $bundles = @()
        $deps = @{}
        if (Test-Path -LiteralPath $manFile) {
            $man = Get-Content -LiteralPath $manFile -Raw -Encoding utf8 | ConvertFrom-Json
            $bundles = @($man.bundles)
            if ($man.dependencies) { $deps = $man.dependencies }
        }

        $pointer = [ordered]@{
            snapshot        = $snap.Name
            snapshotRoot    = $SnapshotRoot
            profileName     = $ProfileName
            dshHome         = $DshHome
            bundles         = $bundles
            dependencies    = $deps
            observedHealthy = $true
            createdAt       = (Get-Date).ToString('o')
        }
        ($pointer | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $pointerFile -Encoding utf8

        Write-Host ''
        Write-Host ("Rollback target switched to: {0}" -f $snap.Name)
        Write-Host ("  plugins in that snapshot: {0}" -f ($bundles -join ', '))
        Write-Host 'Auto-rollback will now use THIS snapshot.'
        Write-Host 'Run -Action Restore to apply it now, or leave it for the next crash.'
    }
    'Delete' {
        # Removes snapshot directories. Separate from Restore on purpose: Restore
        # changes the live DSH config, Delete only changes what is kept on disk.
        #
        # Guards, because this is the one destructive action here:
        #   - refuses to touch anything that is not a snap-* directory under the
        #     snapshot root (no wildcards out of the tree, no pre-restore folders)
        #   - if the deleted snapshot is the current rollback target, the pointer is
        #     moved to the newest remaining snapshot, or cleared if none is left --
        #     a dangling pointer would make a later crash unrollbackable
        if (-not $Snapshot) { throw 'Delete needs -Snapshot <name> or -All. Use -Action List to see the names.' }

        $pointerFile = Join-Path (Split-Path -Parent $SnapshotRoot) 'last-known-good.json'
        $active = ''
        if (Test-Path -LiteralPath $pointerFile) {
            try { $active = [string](Get-Content -LiteralPath $pointerFile -Raw -Encoding utf8 | ConvertFrom-Json).snapshot } catch { }
        }

        $names = @()
        if ($All) {
            $names = @(Get-ChildItem -LiteralPath $SnapshotRoot -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like 'snap-*' } | Sort-Object Name -Descending | ForEach-Object { $_.Name })
            if ($names.Count -eq 0) { Write-Host 'No snapshots to delete.'; return }
        } else {
            foreach ($n in @($Snapshot -split ',')) {
                $t = $n.Trim()
                if ($t.Length -gt 0) { $names += (Resolve-Snapshot -Name $t).Name }
            }
        }

        if (-not $DryRun) {
            Write-Host ''
            Write-Host ("About to DELETE {0} snapshot(s):" -f $names.Count)
            foreach ($n in $names) { Write-Host ("  - {0}{1}" -f $n, $(if ($n -eq $active) { '   <== current rollback target' } else { '' })) }
            Write-Host 'The files are removed. This is the one action here that cannot be undone.'
        }

        $deleted = @()
        foreach ($n in $names) {
            $dir = Join-Path $SnapshotRoot $n
            $leaf = Split-Path -Leaf $dir
            if (-not ($leaf -like 'snap-*')) { Write-Host ("  skipped (not a snapshot): {0}" -f $leaf); continue }
            if (-not (Test-Path -LiteralPath $dir -PathType Container)) { Write-Host ("  skipped (missing): {0}" -f $leaf); continue }
            if ($DryRun) { Write-Host ("  would delete: {0}" -f $dir); $deleted += $leaf; continue }
            try {
                Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction Stop
                Write-Host ("  deleted: {0}" -f $leaf)
                $deleted += $leaf
            } catch {
                Write-Host ("  FAILED to delete {0}: {1}" -f $leaf, $_.Exception.Message)
            }
        }

        # Keep the pointer honest.
        if (-not $DryRun -and $deleted -contains $active) {
            $rest = @(Get-ChildItem -LiteralPath $SnapshotRoot -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like 'snap-*' } | Sort-Object Name -Descending)
            if ($rest.Count -gt 0) {
                $newest = $rest[0]
                $manFile = Join-Path $newest.FullName 'manifest.json'
                $bundles = @(); $deps = @{}
                if (Test-Path -LiteralPath $manFile) {
                    $man = Get-Content -LiteralPath $manFile -Raw -Encoding utf8 | ConvertFrom-Json
                    $bundles = @($man.bundles)
                    if ($man.dependencies) { $deps = $man.dependencies }
                }
                $pointer = [ordered]@{
                    snapshot        = $newest.Name
                    snapshotRoot    = $SnapshotRoot
                    profileName     = $ProfileName
                    dshHome         = $DshHome
                    bundles         = $bundles
                    dependencies    = $deps
                    observedHealthy = $true
                    createdAt       = (Get-Date).ToString('o')
                    note            = 'target moved here because the previous target was deleted'
                }
                ($pointer | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $pointerFile -Encoding utf8
                Write-Host ''
                Write-Host ("Rollback target was the deleted snapshot; it is now: {0}" -f $newest.Name)
            } else {
                Remove-Item -LiteralPath $pointerFile -Force -ErrorAction SilentlyContinue
                Write-Host ''
                Write-Host 'Rollback target was the deleted snapshot and no snapshots remain.'
                Write-Host 'Auto-rollback now has no target: it will refuse to roll back until a new baseline is taken.'
            }
        }

        Write-Host ''
        if ($DryRun) { Write-Host ("Dry run: {0} snapshot(s) would be deleted." -f $deleted.Count) }
        else { Write-Host ("Deleted {0} snapshot(s). Remaining: {1}" -f $deleted.Count, @(Get-ChildItem -LiteralPath $SnapshotRoot -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'snap-*' }).Count) }
    }
}
