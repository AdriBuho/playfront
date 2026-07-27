<#
.SYNOPSIS
  Installs (or moves, or removes) the Playfront helper service in a STABLE location, outside the
  repository.

.DESCRIPTION
  Why it exists: registering the service straight from the build output folder (src\...\bin\Debug\...)
  is a trap. Moving, cleaning or deleting the project folder leaves an auto-start service pointing at
  nothing — and since it runs as SYSTEM, that is not a small detail. That build also depends on .NET
  being installed on the machine.

  This script copies the PUBLISHED build (self-contained, carrying its own .NET) to
  '%ProgramFiles%\Playfront\Helper' and registers the service from there.

  It is both today's solution and the draft of what the installer has to do: same destination, same
  steps, same verification.

  The executable registers itself ('Playfront.Helper.exe --install'), pointing at its own location, so
  the registered path cannot drift from where the file actually is.

.PARAMETER Source
  Folder holding the published helper. Defaults to 'dist\Playfront\Helper', what
  build\Publish-Playfront.ps1 produces.

.PARAMETER Destination
  Where it ends up installed. Defaults to '%ProgramFiles%\Playfront\Helper'.

.PARAMETER Uninstall
  Removes the service and deletes the installed folder, leaving the system as if it had never been
  there.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\Install-Helper.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\Install-Helper.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string] $Source = '',
    [string] $Destination = '',
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'

$ServiceName = 'PlayfrontHelper'
$ExeName     = 'Playfront.Helper.exe'

# Everything is relative to the location of THIS file, never to hand-written paths or the current
# directory.
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Source))      { $Source      = Join-Path $repoRoot 'dist\Playfront\Helper' }
if ([string]::IsNullOrWhiteSpace($Destination)) { $Destination = Join-Path $env:ProgramFiles 'Playfront\Helper' }

function Write-Step { param([string] $Text) Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string] $Text) Write-Host "    OK  $Text" -ForegroundColor Green }
function Write-Note { param([string] $Text) Write-Host "    --  $Text" -ForegroundColor DarkGray }
function Fail       { param([string] $Text) Write-Host "    ERROR  $Text" -ForegroundColor Red; exit 1 }

# --- Administrator required -----------------------------------------------------------------------
# Creating or deleting a service that runs as SYSTEM requires it. This is the ONLY moment elevation is
# needed: afterwards the service runs on its own and the Playfront UI never elevates.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail 'This must be run as administrator (a system service is created/deleted).'
}

# --- Current state, shown before anything changes -------------------------------------------------
function Get-HelperService {
    return Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
}

Write-Step "Current state of service '$ServiceName'"
$existing = Get-HelperService
if ($existing) {
    Write-Note "State:     $($existing.State) (start: $($existing.StartMode))"
    Write-Note "Points to: $($existing.PathName)"
    if ($existing.PathName -like "*$repoRoot*") {
        Write-Note 'That path is INSIDE the repository, which is exactly what this fixes.'
    }
} else {
    Write-Note 'Not installed.'
}

# --- Stop and deregister an existing service ------------------------------------------------------
function Remove-HelperService {
    if (-not (Get-HelperService)) { return }

    Write-Step 'Stopping and deregistering the existing service'
    & sc.exe stop $ServiceName | Out-Null   # fails if already stopped; harmless
    & sc.exe delete $ServiceName | Out-Null

    # 'sc delete' can leave it "marked for deletion" until its process really dies; creating the new
    # one before that fails. Wait for it to disappear instead of assuming it has.
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-HelperService)) { break }
        Start-Sleep -Milliseconds 500
    }
    if (Get-HelperService) { Fail 'The service is still registered 30s after deletion. Reboot and retry.' }
    Write-Ok 'Service deregistered'
}

# --- Uninstall mode -------------------------------------------------------------------------------
if ($Uninstall) {
    Remove-HelperService
    if (Test-Path $Destination) {
        Write-Step 'Deleting the installed folder'
        Remove-Item -Recurse -Force $Destination
        Write-Ok $Destination
        # Also remove 'Program Files\Playfront' if it is left empty, so nothing is left behind.
        $parent = Split-Path -Parent $Destination
        if ((Test-Path $parent) -and -not (Get-ChildItem -Force $parent)) { Remove-Item -Force $parent }
    }
    Write-Step 'Done'
    Write-Host "    The helper is no longer installed. The Playfront UI will still start: with no service"
    Write-Host "    to find, whatever depends on it (installing Steam) is simply disabled."
    Write-Host ''
    exit 0
}

# --- Check the source -----------------------------------------------------------------------------
Write-Step 'Checking the published helper'
$sourceExe = Join-Path $Source $ExeName
if (-not (Test-Path $sourceExe)) {
    Fail "$sourceExe not found.`n           Publish first:  powershell -ExecutionPolicy Bypass -File build\Publish-Playfront.ps1"
}

# Self-contained = carries .NET inside = runs on a Windows without .NET installed. This is verified
# rather than assumed, because installing the framework-dependent build by mistake is exactly the
# failure this script exists to prevent.
$runtimeConfig = Join-Path $Source 'Playfront.Helper.runtimeconfig.json'
if (Test-Path $runtimeConfig) {
    if ((Get-Content $runtimeConfig -Raw) -notmatch 'includedFrameworks') {
        Fail "The build in $Source is NOT self-contained (it depends on the machine's .NET). Use build\Publish-Playfront.ps1."
    }
}
$sourceVersion = (Get-Item $sourceExe).VersionInfo.ProductVersion
Write-Ok "$ExeName version $sourceVersion (self-contained) in $Source"

# --- Deregister, copy, register -------------------------------------------------------------------
Remove-HelperService

Write-Step "Copying to $Destination"
if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $Destination -Recurse -Force
$destExe = Join-Path $Destination $ExeName
if (-not (Test-Path $destExe)) { Fail "Copy finished but there is no $ExeName in $Destination" }
$fileCount = (Get-ChildItem -Recurse -File $Destination).Count
$sizeMB = [math]::Round(((Get-ChildItem -Recurse -File $Destination | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Write-Ok "$fileCount files, $sizeMB MB"

Write-Step 'Registering the service from its new location'
& $destExe --install
if ($LASTEXITCODE -ne 0) { Fail "'$ExeName --install' returned $LASTEXITCODE" }

# --- Verification: proven, not assumed ------------------------------------------------------------
Write-Step 'Verifying'
$svc = Get-HelperService
if (-not $svc) { Fail 'The service is not registered after installing it.' }

# PathName comes back quoted; strip them to compare the real path.
$registered = $svc.PathName.Trim('"')
if ($registered -ne $destExe) { Fail "The service points at '$registered' instead of '$destExe'." }
Write-Ok "Points to $registered"

if ($registered -like "*$repoRoot*") { Fail 'The service still points inside the repository.' }
Write-Ok 'No longer depends on the repository folder'

$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    $svc = Get-HelperService
    if ($svc.State -eq 'Running') { break }
    Start-Sleep -Milliseconds 500
}
if ($svc.State -ne 'Running') { Fail "The service ended up '$($svc.State)' instead of 'Running'." }
Write-Ok "Running (start: $($svc.StartMode)), as $($svc.StartName)"

# End-to-end check: talk to it over the SAME pipe the Playfront UI uses. A service being "Running"
# does not prove it serves requests; this does.
$pipeOk = $false
try {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'PlayfrontHelper', [System.IO.Pipes.PipeDirection]::InOut)
    $pipe.Connect(5000)
    $writer = New-Object System.IO.StreamWriter($pipe)
    $writer.AutoFlush = $true
    $reader = New-Object System.IO.StreamReader($pipe)
    $writer.WriteLine('{"command":"ping"}')
    $reply = $reader.ReadLine()
    $pipe.Dispose()
    if ($reply -match '"[Oo]k":\s*true') {
        $pipeOk = $true
        Write-Ok "Replies on its pipe: $reply"
    } else {
        Write-Note "Unexpected reply on the pipe: $reply"
    }
} catch {
    Write-Note "Could not talk to the service over the pipe: $($_.Exception.Message)"
}
if (-not $pipeOk) { Fail 'The service is running but does not answer requests on its pipe.' }

Write-Step 'Done'
Write-Host "    Playfront Helper $sourceVersion installed in $Destination"
Write-Host '    You can now move, clean or delete the repository folder without breaking the service.'
Write-Host ''
Write-Host '    To remove it:  powershell -ExecutionPolicy Bypass -File build\Install-Helper.ps1 -Uninstall'
Write-Host ''
