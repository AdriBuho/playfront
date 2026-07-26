<#
.SYNOPSIS
  Instala (o mueve, o quita) el servicio ayudante de Playfront en una ubicacion ESTABLE, fuera del
  repositorio.

.DESCRIPTION
  Por que existe: registrar el servicio directamente desde la carpeta de compilacion
  (src\...\bin\Debug\...) es una trampa. Mover, limpiar o borrar la carpeta del proyecto deja un
  servicio de arranque automatico apuntando al vacio - y como corre como SYSTEM, no es un detalle
  menor. Esa version depende ademas de tener .NET instalado en la maquina.

  Este script copia la version PUBLICADA (autocontenida, lleva su propio .NET dentro) a
  '%ProgramFiles%\Playfront\Helper' y registra el servicio desde ahi.

  Es a la vez la solucion de hoy y el borrador de lo que tendra que hacer el instalador cuando
  exista: mismo destino, mismos pasos, misma verificacion.

  Quien registra el servicio es el propio ejecutable ('Playfront.Helper.exe --install'), que se apunta a
  si mismo: asi la ruta registrada no puede desincronizarse de donde esta el fichero de verdad.

.PARAMETER Source
  Carpeta con el ayudante ya publicado. Por defecto 'dist\Playfront\Helper', que es lo que produce
  build\Publish-Playfront.ps1.

.PARAMETER Destination
  Donde queda instalado. Por defecto '%ProgramFiles%\Playfront\Helper'.

.PARAMETER Uninstall
  Quita el servicio y borra la carpeta instalada, dejando el sistema como si nunca hubiera estado.

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

# Todo relativo a la posicion de ESTE fichero, nunca a rutas escritas a mano ni a la carpeta desde
# la que se lanza (norma de distribucion).
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Source))      { $Source      = Join-Path $repoRoot 'dist\Playfront\Helper' }
if ([string]::IsNullOrWhiteSpace($Destination)) { $Destination = Join-Path $env:ProgramFiles 'Playfront\Helper' }

function Write-Step { param([string] $Text) Write-Host "`n==> $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string] $Text) Write-Host "    OK  $Text" -ForegroundColor Green }
function Write-Note { param([string] $Text) Write-Host "    --  $Text" -ForegroundColor DarkGray }
function Fail       { param([string] $Text) Write-Host "    ERROR  $Text" -ForegroundColor Red; exit 1 }

# --- Hace falta administrador ---------------------------------------------------------------------
# Crear o borrar un servicio que corre como SYSTEM lo exige. Es el UNICO momento que necesita
# elevacion: despues el servicio ya corre solo y la interfaz de Playfront nunca se eleva.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail 'Hace falta ejecutar esto como administrador (se crea/borra un servicio del sistema).'
}

# --- Estado actual: se ENSENA antes de cambiar nada ------------------------------------------------
function Get-HelperService {
    return Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
}

Write-Step "Estado actual del servicio '$ServiceName'"
$existing = Get-HelperService
if ($existing) {
    Write-Note "Estado:  $($existing.State) (arranque: $($existing.StartMode))"
    Write-Note "Apunta a: $($existing.PathName)"
    if ($existing.PathName -like "*$repoRoot*") {
        Write-Note 'Esa ruta esta DENTRO del repositorio: es justo lo que se viene a arreglar.'
    }
} else {
    Write-Note 'No esta instalado.'
}

# --- Parada y baja del servicio existente ---------------------------------------------------------
function Remove-HelperService {
    if (-not (Get-HelperService)) { return }

    Write-Step 'Parando y dando de baja el servicio existente'
    & sc.exe stop $ServiceName | Out-Null   # falla si ya estaba parado; da igual
    & sc.exe delete $ServiceName | Out-Null

    # 'sc delete' puede dejarlo "marcado para borrar" hasta que su proceso muera de verdad; si se
    # intenta crear el nuevo antes de eso, falla. Se espera a que desaparezca en vez de suponerlo.
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-HelperService)) { break }
        Start-Sleep -Milliseconds 500
    }
    if (Get-HelperService) { Fail 'El servicio sigue registrado 30 s despues de darlo de baja. Reinicia y reintenta.' }
    Write-Ok 'Servicio dado de baja'
}

# --- Modo desinstalar -----------------------------------------------------------------------------
if ($Uninstall) {
    Remove-HelperService
    if (Test-Path $Destination) {
        Write-Step 'Borrando la carpeta instalada'
        Remove-Item -Recurse -Force $Destination
        Write-Ok $Destination
        # Se borra tambien 'Program Files\Playfront' si queda vacia, para no dejar rastro.
        $parent = Split-Path -Parent $Destination
        if ((Test-Path $parent) -and -not (Get-ChildItem -Force $parent)) { Remove-Item -Force $parent }
    }
    Write-Step 'Listo'
    Write-Host "    El ayudante ya no esta instalado. La interfaz de Playfront seguira arrancando: al no"
    Write-Host "    encontrar el servicio, lo que dependa de el (instalar Steam) quedara desactivado."
    Write-Host ''
    exit 0
}

# --- Comprobacion del origen ----------------------------------------------------------------------
Write-Step 'Comprobando la version publicada del ayudante'
$sourceExe = Join-Path $Source $ExeName
if (-not (Test-Path $sourceExe)) {
    Fail "No se encuentra $sourceExe.`n           Publica primero:  powershell -ExecutionPolicy Bypass -File build\Publish-Playfront.ps1"
}

# Autocontenido = lleva .NET dentro = funciona en un Windows sin .NET instalado. Se comprueba en
# lugar de darlo por hecho, porque instalar por error la version que depende del .NET de la maquina
# es exactamente el fallo que este script viene a corregir.
$runtimeConfig = Join-Path $Source 'Playfront.Helper.runtimeconfig.json'
if (Test-Path $runtimeConfig) {
    if ((Get-Content $runtimeConfig -Raw) -notmatch 'includedFrameworks') {
        Fail "La version de $Source NO es autocontenida (depende del .NET instalado en la maquina). Usa build\Publish-Playfront.ps1."
    }
}
$sourceVersion = (Get-Item $sourceExe).VersionInfo.ProductVersion
Write-Ok "$ExeName version $sourceVersion (autocontenido) en $Source"

# --- Baja, copia y alta ---------------------------------------------------------------------------
Remove-HelperService

Write-Step "Copiando a $Destination"
if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $Destination -Recurse -Force
$destExe = Join-Path $Destination $ExeName
if (-not (Test-Path $destExe)) { Fail "La copia termino pero no hay $ExeName en $Destination" }
$fileCount = (Get-ChildItem -Recurse -File $Destination).Count
$sizeMB = [math]::Round(((Get-ChildItem -Recurse -File $Destination | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Write-Ok "$fileCount ficheros, $sizeMB MB"

Write-Step 'Registrando el servicio desde su nueva ubicacion'
& $destExe --install
if ($LASTEXITCODE -ne 0) { Fail "'$ExeName --install' devolvio $LASTEXITCODE" }

# --- Verificacion: que quede probado, no supuesto -------------------------------------------------
Write-Step 'Verificando'
$svc = Get-HelperService
if (-not $svc) { Fail 'El servicio no aparece registrado despues de instalarlo.' }

# PathName llega entre comillas; se quitan para comparar la ruta de verdad.
$registered = $svc.PathName.Trim('"')
if ($registered -ne $destExe) { Fail "El servicio quedo apuntando a '$registered' en vez de '$destExe'." }
Write-Ok "Apunta a $registered"

if ($registered -like "*$repoRoot*") { Fail 'El servicio sigue apuntando dentro del repositorio.' }
Write-Ok 'Ya NO depende de la carpeta del repositorio'

$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    $svc = Get-HelperService
    if ($svc.State -eq 'Running') { break }
    Start-Sleep -Milliseconds 500
}
if ($svc.State -ne 'Running') { Fail "El servicio quedo en estado '$($svc.State)' en vez de 'Running'." }
Write-Ok "En marcha (arranque: $($svc.StartMode)), como $($svc.StartName)"

# Prueba de extremo a extremo: hablarle por la MISMA tuberia que usa la interfaz de Playfront. Que el
# servicio este "Running" no demuestra que atienda peticiones; esto si.
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
        Write-Ok "Responde por su tuberia: $reply"
    } else {
        Write-Note "Respuesta inesperada por la tuberia: $reply"
    }
} catch {
    Write-Note "No se pudo hablar con el servicio por la tuberia: $($_.Exception.Message)"
}
if (-not $pipeOk) { Fail 'El servicio esta en marcha pero no atiende peticiones por su tuberia.' }

Write-Step 'Listo'
Write-Host "    Playfront Helper $sourceVersion instalado en $Destination"
Write-Host '    Ya puedes mover, limpiar o borrar la carpeta del repositorio sin romper el servicio.'
Write-Host ''
Write-Host '    Para quitarlo:  powershell -ExecutionPolicy Bypass -File build\Install-Helper.ps1 -Uninstall'
Write-Host ''
