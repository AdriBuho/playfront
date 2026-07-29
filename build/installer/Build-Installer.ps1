<#
.SYNOPSIS
  Builds the Playfront installer: a single ~2 MB .exe that downloads everything else.

.DESCRIPTION
  The installer carries nothing inside. It downloads the three pieces from the GitHub release and puts
  each one where it belongs, which is why it weighs 2 MB instead of 500.

  These must already be attached to the release (build\Pack-Playfront.ps1 produces them):

    PlayfrontShell-win-Setup.exe   the app
    PlayfrontHelper.zip            the privileged service
    PlayfrontAssets-Games.zip      artwork and video

  Which release it downloads from is derived from the version: it always points at the release tagged
  'v<PlayfrontVersion>'. The version is read here from Directory.Build.props and passed to the
  compiler, so it cannot drift from the app's.

.PARAMETER Output
  Folder to leave the .exe in. Defaults to 'dist\installer' (outside git).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\installer\Build-Installer.ps1
#>
[CmdletBinding()]
param(
    [string] $Output = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$issPath  = Join-Path $PSScriptRoot 'Playfront.iss'
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $repoRoot 'dist\installer' }

function Write-Step { param([string] $Text) Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string] $Text) Write-Host "    OK  $Text" -ForegroundColor Green }
function Fail       { param([string] $Text) Write-Host "    ERROR  $Text" -ForegroundColor Red; exit 1 }

Write-Step 'Looking for the Inno Setup compiler'
# Install with: winget install --id JRSoftware.InnoSetup
# Note: winget puts it under the user profile, not Program Files, so both are checked.
$candidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)
$iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Fail @"
ISCC.exe (the Inno Setup compiler) was not found. Install it with:

    winget install --id JRSoftware.InnoSetup

Searched:
$($candidates -join "`n")
"@
}
Write-Ok $iscc

if (-not (Test-Path $issPath)) { Fail "$issPath not found" }

# --- Version, from the single place it is defined -------------------------------------------------
# Read here and handed to the compiler rather than written inside the .iss: the release tag is derived
# from it, so a stale number there would keep shipping an installer that downloads an OLD release
# without ever saying so. The .iss refuses to compile if this is missing.
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
if (-not (Test-Path $propsPath)) { Fail "Directory.Build.props not found in $repoRoot" }
$version = ([xml](Get-Content $propsPath)).Project.PropertyGroup.PlayfrontVersion | Where-Object { $_ }
if ([string]::IsNullOrWhiteSpace($version)) { Fail 'Could not read PlayfrontVersion from Directory.Build.props' }

# Same rule as Pack-Playfront.ps1: leading zeros break updates, and they break them silently. Rejected
# in both places so it cannot slip in through whichever one is run first.
if ($version -match '(^|\.)0\d') {
    Fail @"
Version '$version' has leading zeros, and that breaks updates.

Drop them: 0.1.1, 0.1.2, 0.1.3 ... 0.1.87 (no cap, and it sorts correctly without padding).
"@
}
Write-Ok "Version $version (from Directory.Build.props)"

$null = New-Item -ItemType Directory -Force $Output

Write-Step 'Compiling'
& $iscc "/DPfVersion=$version" "/O$Output" $issPath
if ($LASTEXITCODE -ne 0) { Fail "The compiler returned $LASTEXITCODE" }

$exe = Join-Path $Output 'PlayfrontSetup.exe'
if (-not (Test-Path $exe)) { Fail "Compiled, but there is no PlayfrontSetup.exe in $Output" }

Write-Step 'Done'
Write-Host ("    {0:N2} MB   {1}" -f ((Get-Item $exe).Length / 1MB), $exe)
Write-Host ''
Write-Host '    Testing this properly means installing AND uninstalling, checking all three parts' -ForegroundColor White
Write-Host '    (app, service, artwork) and that the user settings survive:'
Write-Host ''
Write-Host '      PlayfrontSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /LOG=%TEMP%\pf.log'
Write-Host ''
Write-Host "    That /LOG is the only account of what happened when it fails on someone else's machine."
Write-Host ''
