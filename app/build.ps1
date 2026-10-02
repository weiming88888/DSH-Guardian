<#
  One-shot build: Guardian.exe.cs -> dsh-guardian.exe (plus an icon).
  Pure ASCII. Uses the .NET Framework compiler already present on Windows:
  no network, no NuGet, no admin rights.
#>
[CmdletBinding()]
param(
    [string]$Source = '',
    [string]$Out = '',
    [string]$Icon = ''
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is EMPTY for some -File invocations under Windows PowerShell
# 5.1, which made the defaults above fail before anything ran. Derive the
# directory from $MyInvocation instead and resolve the paths here.
if (-not $PSScriptRoot) {
    $here = $null
    try { $here = Split-Path -Parent $MyInvocation.MyCommand.Path } catch { }
    if (-not $here) { $here = (Get-Location).Path }
} else {
    $here = $PSScriptRoot
}
if (-not $Source) { $Source = Join-Path $here 'Guardian.exe.cs' }
if (-not $Out) { $Out = Join-Path $here 'dsh-guardian.exe' }
if (-not $Icon) { $Icon = Join-Path $here 'dsh-guardian-app.ico' }

$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc)) { throw 'csc.exe not found in the .NET Framework directory.' }
if (-not (Test-Path -LiteralPath $Source)) { throw "Source not found: $Source" }

# Icon is generated only when absent, so a custom .ico is respected.
if (-not (Test-Path -LiteralPath $Icon)) {
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap 32, 32
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(28, 92, 168))
    $font = New-Object System.Drawing.Font 'Segoe UI', 15, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF 0, 0, 32, 32
    $g.DrawString('G', $font, [System.Drawing.Brushes]::White, $rect, $sf)
    $icoObj = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
    $fs = [System.IO.File]::Create($Icon)
    $icoObj.Save($fs)
    $fs.Close(); $g.Dispose(); $bmp.Dispose()
    Write-Host "icon generated: $Icon"
}

# Two builds, one product:
#   dsh-guardian.exe         the WinForms shell - what the desktop shortcut runs
#   dsh-guardian-console.exe the console build kept for CLI verbs (logs, baseline,
#                            arm/disarm) and for the version picker the GUI calls
# /target:winexe is what stops a console window appearing.
#
# The manifest declares DPI awareness to the OS. Without it the process was
# DPI-virtualised: a window designed as 1120x470 client opened at 761x351 physical
# with the whole UI bitmap-stretched, and SetProcessDPIAware() from managed Main was
# not honoured. A manifest applies before any managed code runs.
$manifest = Join-Path $here 'Guardian.manifest'
$commonArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu', '/optimize+',
    "/win32icon:$Icon",
    # System.Core and Microsoft.CSharp used to be needed for the `dynamic` COM calls
    # that wrote the desktop shortcut. The shortcut is now written by make-shortcut.ps1,
    # so nothing in this source uses dynamic any more and the extra references are gone.
    '/r:System.dll', '/r:System.Drawing.dll'
)
if (Test-Path -LiteralPath $manifest) { $commonArgs += "/win32manifest:$manifest" }
else { Write-Host "WARNING: $manifest missing - the window may open DPI-virtualised" }

$guiSource = Join-Path $here 'Guardian.Win.cs'
$guiOut    = Join-Path $here 'dsh-guardian.exe'
$conOut    = Join-Path $here 'dsh-guardian-console.exe'

Write-Host "compiling GUI   : $guiSource"
& $csc ($commonArgs + @('/r:System.Windows.Forms.dll', "/out:$guiOut", $guiSource))
if ($LASTEXITCODE -ne 0) { throw "GUI compile failed with exit code $LASTEXITCODE" }

Write-Host "compiling console: $Source"
& $csc ($commonArgs + @("/out:$conOut", $Source))
if ($LASTEXITCODE -ne 0) { throw "console compile failed with exit code $LASTEXITCODE" }
$Out = $guiOut

$exe = Get-Item -LiteralPath $Out
Write-Host ''
Write-Host ("built: {0}  ({1:N0} bytes)" -f $exe.FullName, $exe.Length)
Write-Host 'Double-click it, or run: .\dsh-guardian.exe help'
