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

  Which release it downloads from is set by ReleaseTag inside Playfront.iss.

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
$null = New-Item -ItemType Directory -Force $Output

Write-Step 'Compiling'
& $iscc "/O$Output" $issPath
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
