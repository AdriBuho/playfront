<#
.SYNOPSIS
  Empaqueta Playfront para repartir: el paquete de la app (con su actualizador) y, aparte, el paquete
  de assets pesados.

.DESCRIPTION
  Produce lo que el instalador tendra que descargar y lo que se cuelga de una release:

    1. Playfront-win-Setup.exe        el programa, ~55 MB (lleva la app dentro, comprimida)
    2. Playfront-<version>-full.nupkg  + delta   lo que consume el boton de actualizar
    3. PlayfrontAssets-Games.zip     los fondos y el arte de juegos, ~416 MB

  Los assets van APARTE a proposito. El motivo esta en src/Playfront.App/AssetPaths.cs: el sistema de
  actualizacion reemplaza la carpeta del programa cada vez, asi que meter 416 MB ahi dentro convertiria
  una actualizacion de 70 KB en una de 470 MB. Ademas asi pueden ser opcionales y se puede dejar de
  repartir un fichero concreto sin romper ninguna instalacion.

  El instalador los descomprime en %ProgramData%\Playfront\Assets, que es el segundo sitio donde la app
  los busca.

.PARAMETER Runtime
  Plataforma de destino. win-x64 es la de la ROG Ally y la de cualquier PC normal.

.PARAMETER Output
  Carpeta donde se deja el resultado. Por defecto 'dist\release' (fuera de git).

.PARAMETER Clean
  Borra la carpeta de salida antes de empezar.

.PARAMETER SkipAssets
  No genera el zip de assets (tarda, son 416 MB). Util al iterar sobre el programa.

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
#  IDENTIFICADOR DEL PAQUETE: no lo cambies a la ligera, y sobre todo NO lo pongas igual que el nombre
#  de la carpeta de datos.
#
#  Velopack, si nadie le dice otra cosa, instala en %LocalAppData%\<identificador>. Y al desinstalar
#  BORRA esa carpeta entera (comprobado el 2026-07-26: ejecuta rmdir /s /q sobre ella). La app guarda
#  los ajustes del usuario y su sesion de YouTube en %LocalAppData%\Playfront, asi que un identificador
#  "Playfront" haria que instalar y desinstalar se llevara por delante los datos de quien lo tenga
#  puesto. Con "PlayfrontShell" eso es imposible aunque alguien olvide decirle donde instalar.
#
#  El usuario nunca ve este nombre: lo que se ve en el menu de inicio y en "programas instalados" es
#  $packTitle.
#
#  Cambiarlo DESPUES de publicar una version romperia las actualizaciones de quien ya la tenga (para
#  Velopack seria otra aplicacion distinta).
# ---------------------------------------------------------------------------------------------------
$packId    = 'PlayfrontShell'
$packTitle = 'Playfront'

# Nombre de la carpeta de datos de la app (src/Playfront.App/AppData.cs). Solo esta aqui para poder
# comprobar que no coincide con $packId.
$dataFolderName = 'Playfront'

# Donde el instalador debe instalar. Se imprime al final para que no se pierda: hay que pasarselo al
# Setup.exe con --installto. Sin esto instalaria en %LocalAppData%\PlayfrontShell, que funciona pero
# deja una carpeta con un nombre que el usuario no reconoce.
$recommendedInstallDir = '%LocalAppData%\Programs\Playfront'

# Subcarpeta de los assets pesados dentro de la publicacion. Se saca del paquete de la app.
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

# --- La comprobacion que evita el borrado de datos ------------------------------------------------
if ($packId -ieq $dataFolderName) {
    Fail @"
El identificador del paquete ('$packId') es igual al nombre de la carpeta de datos de la app.
Eso hace que Velopack instale ENCIMA de los datos del usuario y los BORRE al desinstalar.
Pon un identificador distinto (por ejemplo 'PlayfrontShell') antes de seguir.
"@
}

# --- Version: del unico sitio donde se define ------------------------------------------------------
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
if (-not (Test-Path $propsPath)) { Fail "No se encuentra Directory.Build.props en $repoRoot" }
$version = ([xml](Get-Content $propsPath)).Project.PropertyGroup.PlayfrontVersion | Where-Object { $_ }
if ([string]::IsNullOrWhiteSpace($version)) { Fail 'No se pudo leer PlayfrontVersion de Directory.Build.props' }

Write-Host ''
Write-Host "  Playfront $version  -  paquetes para repartir ($Runtime, $Configuration)" -ForegroundColor White
Write-Host "  Salida: $Output"

# --- Herramientas necesarias -----------------------------------------------------------------------
Write-Step 'Comprobando herramientas'
$null = (& dotnet --version 2>$null)
if ($LASTEXITCODE -ne 0) { Fail 'No se encuentra "dotnet". Hace falta el SDK de .NET 10.' }
Write-Ok 'SDK de .NET'

# Se comprueba que el comando EXISTA, no se ejecuta: "vpk" sin subcomando devuelve error a proposito.
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Fail 'No se encuentra "vpk" (el empaquetador de Velopack). Instalalo con:  dotnet tool install -g vpk'
}
Write-Ok 'vpk (Velopack)'

if ($Clean -and (Test-Path $Output)) {
    Write-Step 'Borrando la salida anterior'
    Remove-Item -Recurse -Force $Output
    Write-Ok $Output
}
$null = New-Item -ItemType Directory -Force $Output

# --- 1. Publicar la app autocontenida --------------------------------------------------------------
$stage = Join-Path $Output 'stage'
Write-Step 'Publicando la app (autocontenida: lleva su propio .NET dentro)'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }

& dotnet publish (Join-Path $repoRoot 'src\Playfront.App\Playfront.App.csproj') `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $stage `
    --nologo `
    -verbosity:minimal
if ($LASTEXITCODE -ne 0) { Fail 'Fallo la publicacion de la app' }

$exePath = Join-Path $stage 'Playfront.App.exe'
if (-not (Test-Path $exePath)) { Fail "La publicacion termino pero no hay Playfront.App.exe en $stage" }

$stamped = (Get-Item $exePath).VersionInfo.ProductVersion
Write-Ok "Playfront.App.exe  -  version $stamped  -  $(SizeMB $stage) MB"

# Fuera los simbolos de depuracion de librerias AJENAS (100 MB de codigo que no es nuestro y que no
# vamos a depurar). Se conservan los nuestros: son los que hacen que un fallo diga en que linea paso.
$foreignPdb = Get-ChildItem -Path $stage -Filter *.pdb -File |
    Where-Object { $_.Name -ne 'Playfront.App.pdb' }
if ($foreignPdb) {
    $freed = [math]::Round((($foreignPdb | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
    $foreignPdb | Remove-Item -Force
    Write-Ok "Quitados $($foreignPdb.Count) simbolos de terceros ($freed MB)"
}

# --- 2. Separar los assets pesados del paquete de la app -------------------------------------------
Write-Step 'Separando los assets pesados del paquete de la app'
$heavyInStage = Join-Path $stage $heavyAssetsRelative
$heavyTemp    = Join-Path $Output 'assets-stage\Backgrounds\Games'

if (Test-Path $heavyInStage) {
    $null = New-Item -ItemType Directory -Force (Split-Path -Parent $heavyTemp)
    Move-Item $heavyInStage $heavyTemp
    Write-Ok "$(SizeMB $heavyTemp) MB fuera del paquete de la app"
    Write-Note 'La app los buscara en %ProgramData%\Playfront\Assets (ver AssetPaths.cs)'
} else {
    # Pasa en una copia limpia del repositorio: los assets no estan en git (ver .gitignore).
    Write-Note "No hay assets pesados en la publicacion ($heavyAssetsRelative)."
    Write-Note 'Normal en una compilacion desde una copia limpia del repositorio. El programa se'
    Write-Note 'empaqueta igual y arranca sin ellos; solo no se generara su zip.'
}

Write-Ok "El paquete de la app se queda en $(SizeMB $stage) MB"

# --- 3. Empaquetar el programa con Velopack --------------------------------------------------------
Write-Step "Empaquetando el programa con Velopack (identificador '$packId')"
& vpk pack `
    --packId      $packId `
    --packVersion $version `
    --packDir     $stage `
    --mainExe     'Playfront.App.exe' `
    --packTitle   $packTitle `
    --packAuthors $packTitle `
    --outputDir   $Output
if ($LASTEXITCODE -ne 0) { Fail 'Fallo el empaquetado con Velopack' }

$setup = Get-ChildItem -Path $Output -Filter '*-Setup.exe' -File | Select-Object -First 1
if (-not $setup) { Fail "El empaquetado termino pero no hay ningun *-Setup.exe en $Output" }
Write-Ok "$($setup.Name)  -  $([math]::Round($setup.Length/1MB,1)) MB"

# --- 4. Comprimir los assets pesados ---------------------------------------------------------------
if (-not $SkipAssets -and (Test-Path $heavyTemp)) {
    Write-Step 'Comprimiendo los assets pesados'
    $assetsZip = Join-Path $Output 'PlayfrontAssets-Games.zip'
    if (Test-Path $assetsZip) { Remove-Item -Force $assetsZip }

    # Sin compresion: son videos y JPG, ya estan comprimidos. Comprimirlos otra vez tardaria minutos
    # para ahorrar casi nada, y el instalador tiene que descomprimirlo en la maquina del usuario.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        (Split-Path -Parent (Split-Path -Parent $heavyTemp)),
        $assetsZip,
        [IO.Compression.CompressionLevel]::NoCompression,
        $false)

    Write-Ok "PlayfrontAssets-Games.zip  -  $([math]::Round((Get-Item $assetsZip).Length/1MB,1)) MB"
} elseif ($SkipAssets) {
    Write-Step 'Assets pesados: saltados (-SkipAssets)'
}

# --- 5. Limpieza de lo intermedio ------------------------------------------------------------------
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force (Join-Path $Output 'assets-stage') -ErrorAction SilentlyContinue

# --- Resumen ---------------------------------------------------------------------------------------
Write-Step 'Listo'
Get-ChildItem -Path $Output -File | Sort-Object Length -Descending | ForEach-Object {
    Write-Host ("    {0,10} MB   {1}" -f [math]::Round($_.Length/1MB,1), $_.Name)
}
Write-Host ''
Write-Host '    Lo que tiene que hacer el instalador con esto:' -ForegroundColor White
Write-Host "      1. Descargar y ejecutar el Setup.exe con:  --silent --installto `"$recommendedInstallDir`""
Write-Host '         (el --installto NO es opcional por estetica: sin el, la carpeta se llama'
Write-Host "          %LocalAppData%\$packId, que el usuario no reconoce)"
Write-Host '      2. Descomprimir PlayfrontAssets-Games.zip en  %ProgramData%\Playfront\Assets'
Write-Host '      3. Instalar el servicio ayudante (build\Install-Helper.ps1 es el borrador)'
Write-Host ''
