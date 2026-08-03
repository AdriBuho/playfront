<#
.SYNOPSIS
  Builds the Playfront installer: a single ~2 MB .exe that downloads everything else.

.DESCRIPTION
  The installer carries nothing inside. It downloads the three pieces from the GitHub release and puts
  each one where it belongs, which is why it weighs 2 MB instead of 500.

  These must already be attached to a release (build\Pack-Playfront.ps1 produces them), and NOT all to
  the same one:

    PlayfrontShell-win-Setup.exe   the app       -> release v<PlayfrontVersion>
    PlayfrontHelper.zip            the service   -> release HelperTag  (pinned, see the .iss)
    PlayfrontAssets-Games.zip      artwork       -> release AssetsTag  (pinned, see the .iss)

  Only the app follows the version. The other two are 450 MB that do not change when the app does, so
  pinning them is what keeps cutting a version to a ~55 MB upload instead of 470.

  The version is read here from Directory.Build.props and passed to the compiler, so it cannot drift
  from the app's. The pinned helper cannot go stale either: its source is fingerprinted below.

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

# --- The helper's fingerprint ---------------------------------------------------------------------
#  The helper service is downloaded from a PINNED release (HelperTag in the .iss), not from the
#  version's, because it is 35 MB that almost never change. The risk that buys is real though: change
#  its source, forget the tag, and the installer hands out an app talking to an OLD service. That
#  installs, reports success, and only misbehaves later when the app sends a verb the service does not
#  know - the silent kind of failure this project keeps getting bitten by.
#
#  So the source is fingerprinted and compared with what the .iss says was published under that tag.
function Get-HelperSourceId {
    param([string] $Root)
    $files = Get-ChildItem $Root -Recurse -File |
        Where-Object { $_.Extension -in '.cs', '.csproj', '.json' } |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        Sort-Object FullName
    $sb = [Text.StringBuilder]::new()
    foreach ($f in $files) {
        $null = $sb.Append($f.FullName.Substring($Root.Length))
        $null = $sb.Append((Get-FileHash $f.FullName -Algorithm SHA256).Hash)
    }
    $stream = [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($sb.ToString()))
    return (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash.Substring(0, 10).ToLower()
}

$helperRoot = Join-Path $repoRoot 'src\Playfront.Helper'
$issText    = Get-Content $issPath -Raw
$pinnedId   = ([regex]::Match($issText, '#define\s+HelperSourceId\s+"([0-9a-f]+)"')).Groups[1].Value
$helperTag  = ([regex]::Match($issText, '#define\s+HelperTag\s+"([^"]+)"')).Groups[1].Value
$assetsTag  = ([regex]::Match($issText, '#define\s+AssetsTag\s+"([^"]+)"')).Groups[1].Value
$actualId   = Get-HelperSourceId $helperRoot

if ($actualId -ne $pinnedId) {
    Fail @"
The helper service's source has changed since '$helperTag' was published.

    published under $helperTag : $pinnedId
    what is here now           : $actualId

The installer downloads the helper from that tag, so building now would ship the NEW app with the OLD
service, and nothing would say so. Do this instead:

    1. build\Pack-Playfront.ps1        (produces a fresh PlayfrontHelper.zip)
    2. Publish a release tagged helper-vN with that zip attached
    3. In build\installer\Playfront.iss set:
           HelperTag      "helper-vN"
           HelperSourceId "$actualId"
    4. Run this script again

Old installers keep pointing at $helperTag and keep working; only new ones move.
"@
}
Write-Ok "Helper pinned to $helperTag (source $actualId)"
Write-Ok "Artwork pinned to $assetsTag"

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
