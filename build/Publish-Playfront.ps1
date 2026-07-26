<#
.SYNOPSIS
  Genera la version REPARTIBLE de Playfront: carpetas autocontenidas que arrancan en un Windows
  que no tiene .NET instalado.

.DESCRIPTION
  Cumple la norma de distribucion del proyecto: el producto no es
  la carpeta de compilacion ni "abre una terminal y escribe dotnet run", sino algo que se copia y
  funciona. Este script es el paso previo al instalador: produce exactamente lo que el instalador
  tendra que empaquetar.

  Autocontenido significa que cada carpeta lleva su propia copia de .NET dentro. Pesa bastante mas
  (~100 MB por programa) y da igual: quien instale Playfront no va a instalar el SDK ni el runtime.

  La app y el servicio ayudante van en carpetas SEPARADAS a proposito, cada una con su copia de
  .NET. Compartir una sola copia entre los dos ahorraria espacio, pero no lo hacen: la app arrastra
  las librerias de escritorio de Windows (por WebView2) y el servicio no, asi que mezclarlos es
  justo el tipo de detalle que funciona aqui y falla en la maquina de otro.

.PARAMETER Runtime
  Plataforma de destino. win-x64 es la de la ROG Ally y la de cualquier PC normal.

.PARAMETER Output
  Carpeta donde se deja el resultado. Por defecto 'dist' en la raiz del repositorio (fuera de git).

.PARAMETER Clean
  Borra la carpeta de salida antes de publicar, para no dejar restos de una version anterior.

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

# Todo se resuelve a partir de la posicion de ESTE fichero, nunca de la carpeta desde la que se
# lanza ni de una ruta escrita a mano (norma de distribucion: nada atado a esta maquina).
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $repoRoot 'dist' }

function Write-Step { param([string] $Text) Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string] $Text) Write-Host "    OK  $Text" -ForegroundColor Green }
function Fail       { param([string] $Text) Write-Host "    ERROR  $Text" -ForegroundColor Red; exit 1 }

# --- Version: se lee del unico sitio donde se define, para que el informe no pueda mentir --------
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
if (-not (Test-Path $propsPath)) { Fail "No se encuentra Directory.Build.props en $repoRoot" }
$version = ([xml](Get-Content $propsPath)).Project.PropertyGroup.PlayfrontVersion | Where-Object { $_ }
if ([string]::IsNullOrWhiteSpace($version)) { Fail 'No se pudo leer PlayfrontVersion de Directory.Build.props' }

Write-Host ''
Write-Host "  Playfront $version  -  publicacion autocontenida ($Runtime, $Configuration)" -ForegroundColor White
Write-Host "  Salida: $Output"

# --- Comprobacion previa: el SDK de .NET tiene que estar -----------------------------------------
Write-Step 'Comprobando el SDK de .NET'
$sdk = (& dotnet --version 2>$null)
if ($LASTEXITCODE -ne 0) { Fail 'No se encuentra "dotnet". Hace falta el SDK de .NET 10 para PUBLICAR (no para ejecutar lo publicado).' }
Write-Ok "SDK $sdk"

# --- Limpieza opcional ---------------------------------------------------------------------------
if ($Clean -and (Test-Path $Output)) {
    Write-Step 'Borrando la salida anterior'
    Remove-Item -Recurse -Force $Output
    Write-Ok $Output
}

# --- Lo que se publica ---------------------------------------------------------------------------
# 'Exe' es el nombre del ejecutable que MSBuild produce; se comprueba que exista al final, porque
# "dotnet publish" puede acabar con exito y aun asi no dejar lo que esperabamos.
$targets = @(
    [pscustomobject]@{
        Name    = 'App (el shell)'
        Project = Join-Path $repoRoot 'src\Playfront.App\Playfront.App.csproj'
        Dest    = Join-Path $Output 'Playfront'
        Exe     = 'Playfront.App.exe'
    },
    [pscustomobject]@{
        Name    = 'Helper (el servicio con permisos)'
        Project = Join-Path $repoRoot 'src\Playfront.Helper\Playfront.Helper.csproj'
        Dest    = Join-Path $Output 'Playfront\Helper'
        Exe     = 'Playfront.Helper.exe'
    }
)

foreach ($t in $targets) {
    Write-Step "Publicando $($t.Name)"
    if (-not (Test-Path $t.Project)) { Fail "No se encuentra el proyecto: $($t.Project)" }

    # Los simbolos de depuracion (.pdb) SI se incluyen a proposito: sin telemetria, el registro de
    # %LocalAppData%\Playfront\playfront.log es lo unico con lo que diagnosticar un fallo en la maquina de
    # otra persona, y sin .pdb los errores no dicen en que linea pasaron.
    & dotnet publish $t.Project `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        --output $t.Dest `
        --nologo `
        -verbosity:minimal

    if ($LASTEXITCODE -ne 0) { Fail "Fallo la publicacion de $($t.Name)" }

    $exePath = Join-Path $t.Dest $t.Exe
    if (-not (Test-Path $exePath)) { Fail "La publicacion termino pero no hay $($t.Exe) en $($t.Dest)" }

    # Fuera los simbolos de depuracion de librerias AJENAS. Se conservan los NUESTROS (pesan ~100 KB
    # y son los que hacen que un fallo diga en que linea de nuestro codigo paso), pero los de las
    # librerias de terceros que dibujan la interfaz ocupan 100 MB y no sirven para diagnosticar
    # Playfront: son simbolos de codigo que no es nuestro y que no vamos a depurar nunca.
    $ownPdb = [IO.Path]::ChangeExtension($t.Exe, '.pdb')
    $foreignPdb = Get-ChildItem -Path $t.Dest -Filter *.pdb -File | Where-Object { $_.Name -ne $ownPdb }
    if ($foreignPdb) {
        $freedMB = [math]::Round((($foreignPdb | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
        $foreignPdb | Remove-Item -Force
        Write-Ok "Quitados $($foreignPdb.Count) simbolos de terceros ($freedMB MB); se conserva $ownPdb"
    }

    # Se lee la version del ejecutable REAL, no la del fichero de propiedades: asi se confirma que
    # el numero llego de verdad hasta el binario que se va a repartir.
    $stamped = (Get-Item $exePath).VersionInfo.ProductVersion
    $sizeMB  = [math]::Round(((Get-ChildItem -Recurse -File $t.Dest | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
    Write-Ok "$($t.Exe)  -  version $stamped  -  $sizeMB MB en $($t.Dest)"
}

# --- Resumen -------------------------------------------------------------------------------------
$totalMB = [math]::Round(((Get-ChildItem -Recurse -File $Output | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Write-Step 'Listo'
Write-Host "    Playfront $version publicado en $Output  ($totalMB MB en total)"
Write-Host ''
Write-Host '    Siguiente paso para probarlo DE VERDAD (norma de distribucion): copiar esa carpeta'
Write-Host '    a una maquina o particion SIN el SDK de .NET y sin el repositorio, y arrancar'
Write-Host '    Playfront\Playfront.App.exe desde ahi. Probarlo aqui no demuestra que sea autocontenido.'
Write-Host ''
