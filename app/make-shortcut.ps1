# Creates one desktop shortcut.
#
# Why this is a separate script instead of C# code in the exe:
#
# The program used to write the shortcut itself, first through raw reflected COM
# (Type.GetTypeFromProgID + Type.InvokeMember) and then through the C# dynamic binder.
# Both fail from inside the exe with UnauthorizedAccessException "cannot save shortcut
# <desktop path>" -- while a PowerShell process on the same machine, same user, same
# path, performs the identical WScript.Shell calls successfully. Reproduced repeatedly:
# the failure follows the process, not the path or the permissions.
#
# Rather than keep guessing at the COM activation difference, the shortcut is now
# written by the mechanism that is measured to work. Each language stays in its own
# file and the exe calls this one by path -- no code is generated inside another
# language, which is what made the old .cmd/.vbs launchers so fragile.
#
# Pure ASCII on purpose: Windows PowerShell 5.1 decodes a script without a BOM using
# the ANSI code page, so non-ASCII here would corrupt. The Chinese labels arrive as
# parameters, already escaped by the caller.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Lnk,
    [Parameter(Mandatory = $true)][string]$Target,
    [Parameter(Mandatory = $true)][string]$WorkDir,
    [string]$Icon = '',
    [string]$Description = '',
    [string]$Arguments = '',
    [int]$WindowStyle = 1
)

$ErrorActionPreference = 'Stop'

# Diagnostic step, reported on every failure.
#
# "cannot save shortcut" is the Shell's message and it does not say whether the write
# was refused or the COM call was. A plain file write to the same folder as the .lnk
# separates the two: if this succeeds while the shortcut fails, the problem is the
# Shell call, not permission on the folder. Kept because the next person to see this
# failure will otherwise repeat the same week of guessing.
$probeResult = 'not attempted'
try {
    $probeDir = Split-Path -Parent $Lnk
    if (-not $probeDir) { $probeDir = '.' }
    $probe = Join-Path $probeDir ('._guardian_write_probe_' + $PID + '.tmp')
    Set-Content -LiteralPath $probe -Value 'probe' -Encoding ascii
    if (Test-Path -LiteralPath $probe) {
        $probeResult = "plain file write to $probeDir OK"
        Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
    } else {
        $probeResult = "plain file write to $probeDir reported success but file missing"
    }
} catch {
    $probeResult = "plain file write FAILED: " + $_.Exception.Message
}

try {
    $dir = Split-Path -Parent $Lnk
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    $shell = New-Object -ComObject WScript.Shell
    $sc = $shell.CreateShortcut($Lnk)
    $sc.TargetPath = $Target
    if ($Arguments) { $sc.Arguments = $Arguments }
    if ($WorkDir) { $sc.WorkingDirectory = $WorkDir }
    if ($Icon) { $sc.IconLocation = $Icon }
    if ($Description) { $sc.Description = $Description }
    $sc.WindowStyle = $WindowStyle
    $sc.Save()

    if (-not (Test-Path -LiteralPath $Lnk)) {
        Write-Output "FAILED: Save reported success but $Lnk does not exist"
        exit 2
    }
    Write-Output "OK $Lnk"
    exit 0
} catch {
    Write-Output ("FAILED: " + $_.Exception.Message)
    Write-Output ("  as user: " + [System.Security.Principal.WindowsIdentity]::GetCurrent().Name)
    Write-Output ("  probe  : " + $probeResult)
    # Integrity level and how the folder's ACL sees this token. "Access denied" on a
    # path the same user can write from another process usually means the token is
    # not the one you think it is.
    try {
        $il = (whoami.exe /groups | Select-String -Pattern 'Mandatory Label|强制标签' | Select-Object -First 1)
        Write-Output ("  integrity: " + ("$il").Trim())
    } catch { }
    try {
        $acl = Get-Acl -LiteralPath $probeDir
        Write-Output ("  acl owner: " + $acl.Owner)
        $acl.Access | Select-Object -First 6 | ForEach-Object {
            Write-Output ("    " + $_.IdentityReference + " | " + $_.FileSystemRights + " | " + $_.AccessControlType)
        }
    } catch { }
    exit 1
}
