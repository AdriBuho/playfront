<#
.SYNOPSIS
  Produces the distributable build of Playfront: self-contained folders that run on a Windows machine
  with no .NET installed.

.DESCRIPTION
  The product is not the build output folder, and not "open a terminal and type dotnet run" — it is
  something you copy and it works. This script is the step before the installer: it produces exactly
  what the installer has to package.

  Self-contained means each folder carries its own copy of .NET. That costs roughly 100 MB per program
  and is worth it: whoever installs Playfront is not going to install the SDK or the runtime.

  The app and the helper service go in SEPARATE folders, each with its own copy of .NET. Sharing one
  copy would save space, but they do not carry the same thing: the app drags in the Windows desktop
  libraries (for WebView2) and the service does not. Mixing them is exactly the kind of detail that
  works here and fails on someone else's machine.

.PARAMETER Runtime
  Target platform. win-x64 covers the ROG Ally and any normal PC.

.PARAMETER Output
  Where to leave the result. Defaults to 'dist' at the repository root (outside git).

.PARAMETER Clean
  Delete the output folder first, so no leftovers from a previous version survive.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\Publish-Playfront.ps1 -Clean
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $Output = '',
    [switch] $Clean
)

$ErrorActionPreference = 'Stop'

# Everything resolves from the location of THIS file, never from the current directory or a hand-written
# path: nothing may be tied to one machine.
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $repoRoot 'dist' }

function Write-Step { param([string] $Text) Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string] $Text) Write-Host "    OK  $Text" -ForegroundColor Green }
function Fail       { param([string] $Text) Write-Host "    ERROR  $Text" -ForegroundColor Red; exit 1 }

# --- Version, read from the single place it is defined so the report cannot lie -------------------
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
if (-not (Test-Path $propsPath)) { Fail "Directory.Build.props not found in $repoRoot" }
$version = ([xml](Get-Content $propsPath)).Project.PropertyGroup.PlayfrontVersion | Where-Object { $_ }
if ([string]::IsNullOrWhiteSpace($version)) { Fail 'Could not read PlayfrontVersion from Directory.Build.props' }

Write-Host ''
Write-Host "  Playfront $version  -  self-contained publish ($Runtime, $Configuration)" -ForegroundColor White
Write-Host "  Output: $Output"

# --- Preflight: the .NET SDK must be present ------------------------------------------------------
Write-Step 'Checking the .NET SDK'
$sdk = (& dotnet --version 2>$null)
if ($LASTEXITCODE -ne 0) { Fail 'No "dotnet" found. The .NET 10 SDK is needed to PUBLISH (not to run the result).' }
Write-Ok "SDK $sdk"

# --- Optional cleanup -----------------------------------------------------------------------------
if ($Clean -and (Test-Path $Output)) {
    Write-Step 'Deleting the previous output'
    Remove-Item -Recurse -Force $Output
    Write-Ok $Output
}

# --- What gets published --------------------------------------------------------------------------
# 'Exe' is the executable name MSBuild produces; its existence is checked afterwards, because
# "dotnet publish" can succeed and still not leave what was expected.
$targets = @(
    [pscustomobject]@{
        Name    = 'App (the shell)'
        Project = Join-Path $repoRoot 'src\Playfront.App\Playfront.App.csproj'
        Dest    = Join-Path $Output 'Playfront'
        Exe     = 'Playfront.App.exe'
    },
    [pscustomobject]@{
        Name    = 'Helper (the privileged service)'
        Project = Join-Path $repoRoot 'src\Playfront.Helper\Playfront.Helper.csproj'
        Dest    = Join-Path $Output 'Playfront\Helper'
        Exe     = 'Playfront.Helper.exe'
    }
)

foreach ($t in $targets) {
    Write-Step "Publishing $($t.Name)"
    if (-not (Test-Path $t.Project)) { Fail "Project not found: $($t.Project)" }

    # Our own debug symbols (.pdb) ARE included deliberately: with no telemetry, the log at
    # %LocalAppData%\Playfront\playfront.log is the only way to diagnose a failure on someone else's
    # machine, and without .pdb the errors don't say which line they happened on.
    & dotnet publish $t.Project `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        --output $t.Dest `
        --nologo `
        -verbosity:minimal

    if ($LASTEXITCODE -ne 0) { Fail "Publishing $($t.Name) failed" }

    $exePath = Join-Path $t.Dest $t.Exe
    if (-not (Test-Path $exePath)) { Fail "Publish finished but there is no $($t.Exe) in $($t.Dest)" }

    # Third-party debug symbols are dropped. Ours are kept — around 100 KB, and they are what make a
    # failure report the line it happened on. The UI libraries' symbols are 100 MB of code we are
    # never going to debug and they do nothing to diagnose Playfront.
    $ownPdb = [IO.Path]::ChangeExtension($t.Exe, '.pdb')
    $foreignPdb = Get-ChildItem -Path $t.Dest -Filter *.pdb -File | Where-Object { $_.Name -ne $ownPdb }
    if ($foreignPdb) {
        $freedMB = [math]::Round((($foreignPdb | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
        $foreignPdb | Remove-Item -Force
        Write-Ok "Dropped $($foreignPdb.Count) third-party symbol files ($freedMB MB); kept $ownPdb"
    }

    # The version is read back from the REAL executable, not from the properties file: that confirms
    # the number actually reached the binary being shipped.
    $stamped = (Get-Item $exePath).VersionInfo.ProductVersion
    $sizeMB  = [math]::Round(((Get-ChildItem -Recurse -File $t.Dest | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
    Write-Ok "$($t.Exe)  -  version $stamped  -  $sizeMB MB in $($t.Dest)"
}

# --- Summary --------------------------------------------------------------------------------------
$totalMB = [math]::Round(((Get-ChildItem -Recurse -File $Output | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Write-Step 'Done'
Write-Host "    Playfront $version published to $Output  ($totalMB MB total)"
Write-Host ''
Write-Host '    To test this properly: copy that folder to a machine or partition WITHOUT the .NET SDK'
Write-Host '    and without the repository, and start Playfront\Playfront.App.exe from there. Running it'
Write-Host '    here proves nothing about whether it is really self-contained.'
Write-Host ''
