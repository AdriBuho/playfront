<#
.SYNOPSIS
  Compila el instalador de Playfront (un unico .exe de ~2 MB que se descarga todo lo demas).

.DESCRIPTION
  El instalador NO lleva nada dentro: descarga las tres piezas de la release de GitHub y las monta
  cada una en su sitio. Por eso pesa 2 MB y no 500.

  Antes de compilar hay que tener colgados de la release (los genera build\Pack-Playfront.ps1):

    PlayfrontShell-win-Setup.exe   la app
    PlayfrontHelper.zip            el servicio con permisos
    PlayfrontAssets-Games.zip      el arte y los videos

  La version que se descarga la fija ReleaseTag dentro de Playfront.iss.

.PARAMETER Output
  Carpeta donde dejar el .exe. Por defecto 'dist\installer' (fuera de git).

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

Write-Step 'Buscando el compilador de Inno Setup'
# Se instala con: winget install --id JRSoftware.InnoSetup
# Ojo: winget lo deja en la carpeta del usuario, no en Program Files, asi que se miran los dos.
$candidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)
$iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Fail @"
No se encuentra ISCC.exe (el compilador de Inno Setup). Instalalo con:

    winget install --id JRSoftware.InnoSetup

Buscado en:
$($candidates -join "`n")
"@
}
Write-Ok $iscc

if (-not (Test-Path $issPath)) { Fail "No se encuentra $issPath" }
$null = New-Item -ItemType Directory -Force $Output

Write-Step 'Compilando'
& $iscc "/O$Output" $issPath
if ($LASTEXITCODE -ne 0) { Fail "El compilador devolvio $LASTEXITCODE" }

$exe = Join-Path $Output 'PlayfrontSetup.exe'
if (-not (Test-Path $exe)) { Fail "Compilo pero no hay PlayfrontSetup.exe en $Output" }

Write-Step 'Listo'
Write-Host ("    {0:N2} MB   {1}" -f ((Get-Item $exe).Length / 1MB), $exe)
Write-Host ''
Write-Host '    Para probarlo de verdad hay que instalarlo Y desinstalarlo, comprobando las tres' -ForegroundColor White
Write-Host '    partes (app, servicio, arte) y que los ajustes del usuario sobreviven:'
Write-Host ''
Write-Host '      PlayfrontSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /LOG=%TEMP%\pf.log'
Write-Host ''
Write-Host '    El /LOG es lo unico que cuenta que ha pasado cuando falla en la maquina de otro.'
Write-Host ''
