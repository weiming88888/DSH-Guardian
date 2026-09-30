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
if (-not $Icon) { $Icon = Join-Path $here 'dsh-guardian.ico' }

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

$cscArgs = @(
    '/nologo',
    # /target:winexe is what stops the window. /target:exe builds a console
    # (CUI) program, and Task Scheduler allocates a visible console window for
    # those no matter how the task is configured - that was the popup.
    '/target:winexe',
    '/platform:anycpu', '/optimize+',
    "/out:$Out", "/win32icon:$Icon",
    '/r:System.dll', '/r:System.Drawing.dll',
    $Source
)

Write-Host "compiling: $Source"
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "compile failed with exit code $LASTEXITCODE" }

$exe = Get-Item -LiteralPath $Out
Write-Host ''
Write-Host ("built: {0}  ({1:N0} bytes)" -f $exe.FullName, $exe.Length)
Write-Host 'Double-click it, or run: .\dsh-guardian.exe help'
