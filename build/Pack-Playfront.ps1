<#
.SYNOPSIS
  Packages Playfront for distribution: the app package (with its updater), the privileged service, and
  separately the heavy asset package.

.DESCRIPTION
  Produces everything the installer downloads and everything attached to a release:

    1. PlayfrontShell-win-Setup.exe            the program, ~55 MB (carries the app inside)
    2. PlayfrontShell-<version>-full.nupkg + delta   what the in-app updater consumes
    3. PlayfrontHelper.zip                     the privileged service
    4. PlayfrontAssets-Games.zip               game backgrounds and artwork, ~416 MB

  The assets ship SEPARATELY on purpose; the reasoning lives in src/Playfront.App/AssetPaths.cs. The
  updater replaces the program folder every time, so putting 416 MB in there would turn a 70 KB update
  into a 470 MB one. It also makes them optional, and lets a single file be withdrawn without breaking
  any installation.

  The installer unpacks them into %ProgramData%\Playfront\Assets, the second place the app looks.

.PARAMETER Runtime
  Target platform. win-x64 covers the ROG Ally and any normal PC.

.PARAMETER Output
  Where to leave the result. Defaults to 'dist\release' (outside git).

.PARAMETER Clean
  Delete the output folder before starting.

.PARAMETER SkipAssets
  Skip the asset zip (it is 416 MB and takes a while). Useful when iterating on the program.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\Pack-Playfront.ps1 -Clean
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $Output = '',
    [switch] $Clean,
    [switch] $SkipAssets
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------------
#  PACKAGE ID: do not change it lightly, and above all do NOT make it match the data folder name.
#
#  Left to itself, Velopack installs into %LocalAppData%\<packId>, and on uninstall it DELETES that
#  folder wholesale (verified: it runs rmdir /s /q over it). The app keeps user settings and the
#  YouTube session in %LocalAppData%\Playfront, so a packId of "Playfront" would make install and
#  uninstall take the user's data with them. With "PlayfrontShell" that is impossible even if someone
#  forgets to pass an install location.
#
#  Users never see this name: the Start menu and "installed apps" show $packTitle.
#
#  Changing it AFTER publishing a version would break updates for anyone already on it — to Velopack
#  it would be a different application.
# ---------------------------------------------------------------------------------------------------
$packId    = 'PlayfrontShell'
$packTitle = 'Playfront'

# The app's data folder name (src/Playfront.App/AppData.cs). Here only so the check below can prove it
# does not collide with $packId.
$dataFolderName = 'Playfront'

# Where the installer must install. Printed at the end so it doesn't get lost: it has to be passed to
# Setup.exe with --installto. Without it the folder is %LocalAppData%\PlayfrontShell, which works but
# leaves a name the user doesn't recognise.
$recommendedInstallDir = '%LocalAppData%\Programs\Playfront'

# Heavy-asset subfolder inside the publish output. Pulled out of the app package.
$heavyAssetsRelative = 'Assets\Backgrounds\Games'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $repoRoot 'dist\release' }

function Write-Step { param([string] $Text) Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string] $Text) Write-Host "    OK  $Text" -ForegroundColor Green }
function Write-Note { param([string] $Text) Write-Host "    --  $Text" -ForegroundColor DarkGray }
function Fail       { param([string] $Text) Write-Host "    ERROR  $Text" -ForegroundColor Red; exit 1 }
function SizeMB     { param([string] $Path)
    if (-not (Test-Path $Path)) { return 0 }
    $sum = (Get-ChildItem -Recurse -File $Path | Measure-Object -Property Length -Sum).Sum
    return [math]::Round($sum / 1MB, 1)
}

# --- The check that prevents wiping user data -----------------------------------------------------
if ($packId -ieq $dataFolderName) {
    Fail @"
The package id ('$packId') is the same as the app's data folder name.
That makes Velopack install ON TOP of the user's data and DELETE it on uninstall.
Pick a different id (for example 'PlayfrontShell') before continuing.
"@
}

# --- Version, from the single place it is defined -------------------------------------------------
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
if (-not (Test-Path $propsPath)) { Fail "Directory.Build.props not found in $repoRoot" }
$version = ([xml](Get-Content $propsPath)).Project.PropertyGroup.PlayfrontVersion | Where-Object { $_ }
if ([string]::IsNullOrWhiteSpace($version)) { Fail 'Could not read PlayfrontVersion from Directory.Build.props' }

# --- No leading zeros -----------------------------------------------------------------------------
#  Verified by actually packaging: with PlayfrontVersion = 0.1.001 the .exe keeps "0.1.001" but
#  Velopack normalises it and produces "PlayfrontShell-0.1.1-full.nupkg". So 0.1.001 and 0.1.1 are THE
#  SAME version as far as the updater is concerned.
#
#  Why this stops here instead of letting it through: the failure is silent. If both ever shipped,
#  anyone on one would not update to the other and would never be told why. Meanwhile Settings would
#  show a number (0.1.001) different from the release's (0.1.1).
#
#  Project numbering: bump the third number on every release, with no cap and no padding (0.1.1,
#  0.1.2 ... 0.1.87). It sorts correctly on its own — 0.1.10 comes AFTER 0.1.9, the comparison is
#  numeric, not alphabetical. The middle number moves when the change is large.
if ($version -match '(^|\.)0\d') {
    Fail @"
Version '$version' has leading zeros, and that breaks updates.

Velopack strips them: '$version' would be packaged as a different version from the one in
Directory.Build.props, and two numbers that look different would be the same to the updater.

Drop the leading zeros. For many consecutive releases, bumping the third number is enough:
0.1.1, 0.1.2, 0.1.3 ... 0.1.87  (no cap, and it sorts correctly without padding).
"@
}

Write-Host ''
Write-Host "  Playfront $version  -  distribution packages ($Runtime, $Configuration)" -ForegroundColor White
Write-Host "  Output: $Output"

# --- Required tools -----------------------------------------------------------------------
Write-Step 'Checking tools'
$null = (& dotnet --version 2>$null)
if ($LASTEXITCODE -ne 0) { Fail 'No "dotnet" found. The .NET 10 SDK is required.' }
Write-Ok '.NET SDK'

# Only the command's EXISTENCE is checked, it is not run: "vpk" with no subcommand exits non-zero.
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Fail 'No "vpk" found (the Velopack packager). Install it with:  dotnet tool install -g vpk'
}
Write-Ok 'vpk (Velopack)'

if ($Clean -and (Test-Path $Output)) {
    Write-Step 'Deleting the previous output'
    Remove-Item -Recurse -Force $Output
    Write-Ok $Output
}
$null = New-Item -ItemType Directory -Force $Output

# --- 1. Publish the app, self-contained --------------------------------------------------------------
$stage = Join-Path $Output 'stage'
Write-Step 'Publishing the app (self-contained: carries its own .NET)'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }

& dotnet publish (Join-Path $repoRoot 'src\Playfront.App\Playfront.App.csproj') `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $stage `
    --nologo `
    -verbosity:minimal
if ($LASTEXITCODE -ne 0) { Fail 'Publishing the app failed' }

$exePath = Join-Path $stage 'Playfront.App.exe'
if (-not (Test-Path $exePath)) { Fail "Publish finished but there is no Playfront.App.exe in $stage" }

$stamped = (Get-Item $exePath).VersionInfo.ProductVersion
Write-Ok "Playfront.App.exe  -  version $stamped  -  $(SizeMB $stage) MB"

# Third-party debug symbols are dropped: 100 MB of code we will never debug. Ours are kept, because
# they are what make a failure report the line it happened on.
$foreignPdb = Get-ChildItem -Path $stage -Filter *.pdb -File |
    Where-Object { $_.Name -ne 'Playfront.App.pdb' }
if ($foreignPdb) {
    $freed = [math]::Round((($foreignPdb | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
    $foreignPdb | Remove-Item -Force
    Write-Ok "Dropped $($foreignPdb.Count) third-party symbol files ($freed MB)"
}

# --- 2. Split the heavy assets out of the app package -------------------------------------------
Write-Step 'Splitting the heavy assets out of the app package'
$heavyInStage = Join-Path $stage $heavyAssetsRelative
$heavyTemp    = Join-Path $Output 'assets-stage\Backgrounds\Games'

if (Test-Path $heavyInStage) {
    $null = New-Item -ItemType Directory -Force (Split-Path -Parent $heavyTemp)
    Move-Item $heavyInStage $heavyTemp
    Write-Ok "$(SizeMB $heavyTemp) MB moved out of the app package"
    Write-Note 'The app will look for them in %ProgramData%\Playfront\Assets (see AssetPaths.cs)'
} else {
    # Happens on a clean clone: the assets are not in git (see .gitignore).
    Write-Note "No heavy assets in the publish output ($heavyAssetsRelative)."
    Write-Note 'Normal when building from a clean clone. The program is packaged anyway and starts'
    Write-Note 'without them; only their zip is skipped.'
}

Write-Ok "The app package is down to $(SizeMB $stage) MB"

# --- 3. Package the program with Velopack ----------------------------------------------------------
Write-Step "Packaging the program with Velopack (id '$packId')"
& vpk pack `
    --packId      $packId `
    --packVersion $version `
    --packDir     $stage `
    --mainExe     'Playfront.App.exe' `
    --packTitle   $packTitle `
    --packAuthors $packTitle `
    --outputDir   $Output
if ($LASTEXITCODE -ne 0) { Fail 'Velopack packaging failed' }

$setup = Get-ChildItem -Path $Output -Filter '*-Setup.exe' -File | Select-Object -First 1
if (-not $setup) { Fail "Packaging finished but there is no *-Setup.exe in $Output" }
Write-Ok "$($setup.Name)  -  $([math]::Round($setup.Length/1MB,1)) MB"

# --- 3b. The helper service ----------------------------------------------------------------------
#  Ships as its OWN package, not inside the app's, for two reasons:
#    1. It installs into 'Program Files' and as a Windows service, which needs administrator
#       rights. The app does NOT need them (it lives in the user folder) and must not ask, because
#       if it did it could not update itself silently.
#    2. The updater replaces the app folder on every version. Putting 79 MB that almost never change
#       in there would turn every small update into a huge download.
#  The executable registers itself as a service ('--install'); no separate script does it.
Write-Step 'Publishing the helper service (separate package)'
$helperStage = Join-Path $Output 'helper-stage'
if (Test-Path $helperStage) { Remove-Item -Recurse -Force $helperStage }

& dotnet publish (Join-Path $repoRoot 'src\Playfront.Helper\Playfront.Helper.csproj') `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $helperStage `
    --nologo `
    -verbosity:minimal
if ($LASTEXITCODE -ne 0) { Fail 'Publishing the helper service failed' }

$helperExe = Join-Path $helperStage 'Playfront.Helper.exe'
if (-not (Test-Path $helperExe)) { Fail "Publish finished but there is no Playfront.Helper.exe in $helperStage" }

# Same third-party symbols dropped as for the app: megabytes of code we will never debug.
$helperForeignPdb = Get-ChildItem -Path $helperStage -Filter *.pdb -File |
    Where-Object { $_.Name -ne 'Playfront.Helper.pdb' }
if ($helperForeignPdb) { $helperForeignPdb | Remove-Item -Force }

$helperZip = Join-Path $Output 'PlayfrontHelper.zip'
if (Test-Path $helperZip) { Remove-Item -Force $helperZip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
# This one IS compressed, unlike the assets: they are DLLs and drop from ~79 MB to well under half.
[IO.Compression.ZipFile]::CreateFromDirectory($helperStage, $helperZip, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Ok "PlayfrontHelper.zip  -  $([math]::Round((Get-Item $helperZip).Length/1MB,1)) MB (uncompressed: $(SizeMB $helperStage) MB)"

# --- 4. Compress the heavy assets ---------------------------------------------------------------
if (-not $SkipAssets -and (Test-Path $heavyTemp)) {
    Write-Step 'Compressing the heavy assets'
    $assetsZip = Join-Path $Output 'PlayfrontAssets-Games.zip'
    if (Test-Path $assetsZip) { Remove-Item -Force $assetsZip }

    # No compression: these are video and JPEG, already compressed. Recompressing would take minutes
    # to save almost nothing, and the installer has to unpack it on the user's machine.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        (Split-Path -Parent (Split-Path -Parent $heavyTemp)),
        $assetsZip,
        [IO.Compression.CompressionLevel]::NoCompression,
        $false)

    Write-Ok "PlayfrontAssets-Games.zip  -  $([math]::Round((Get-Item $assetsZip).Length/1MB,1)) MB"
} elseif ($SkipAssets) {
    Write-Step 'Heavy assets: skipped (-SkipAssets)'
}

# --- 5. Clean up intermediates ------------------------------------------------------------------
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $helperStage -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force (Join-Path $Output 'assets-stage') -ErrorAction SilentlyContinue

# --- Summary ---------------------------------------------------------------------------------------
Write-Step 'Done'
Get-ChildItem -Path $Output -File | Sort-Object Length -Descending | ForEach-Object {
    Write-Host ("    {0,10} MB   {1}" -f [math]::Round($_.Length/1MB,1), $_.Name)
}
Write-Host ''
Write-Host '    These files go on GitHub releases - and NOT all on the same one:' -ForegroundColor White
Write-Host ''
Write-Host "      PlayfrontShell-*  + RELEASES/*.json  -> release v$version   (the app, every time)"
Write-Host '      PlayfrontHelper.zip                  -> release helper-vN  (only when it changes)'
Write-Host '      PlayfrontAssets-Games.zip            -> release assets-vN  (only when it changes)'
Write-Host ''
Write-Host '    The last two are pinned by tag in build\installer\Playfront.iss. That is deliberate: they'
Write-Host '    are 450 MB that do not change when the app does, and tying them to the version meant'
Write-Host '    re-uploading all of it on every release. Moving one means publishing the next tag and'
Write-Host '    editing the tag there; the build refuses to compile if the helper drifts from its tag.'
Write-Host ''
Write-Host '    What pulls them together and installs them is build\installer\Playfront.iss (Inno Setup),'
Write-Host '    which produces a single 2 MB .exe:'
Write-Host ''
Write-Host "      1. The app     -> $recommendedInstallDir  (user folder, NO admin rights)"
Write-Host '      2. The helper  -> %ProgramFiles%\Playfront\Helper + service  (admin rights)'
Write-Host '      3. Artwork     -> %ProgramData%\Playfront\Assets'
Write-Host ''
Write-Host '    The installer takes its version from Directory.Build.props, so cutting a new version'
Write-Host '    only means rebuilding it - there is no number to edit by hand:'
Write-Host ''
Write-Host '      powershell -ExecutionPolicy Bypass -File build\installer\Build-Installer.ps1'
Write-Host ''
