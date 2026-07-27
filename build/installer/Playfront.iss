; ===================================================================================================
;  Instalador de Playfront
;
;  Un solo .exe de pocos MB que se descarga y monta TODO lo que hace falta:
;
;    1. La app          -> carpeta del usuario, SIN permisos de administrador
;    2. El ayudante     -> Program Files + servicio de Windows, CON permisos
;    3. Arte y videos   -> ProgramData, compartidos por todos los usuarios
;
;  POR QUE ESTA PARTIDO ASI (no es capricho):
;
;  La app tiene que vivir en la carpeta del usuario para poder actualizarse SOLA y en silencio. Si
;  viviera en Program Files, cada actualizacion pediria permisos de administrador. Es el mismo motivo
;  por el que Chrome o Discord se instalan en la carpeta del usuario.
;
;  Pero el ayudante hace el trabajo privilegiado (TDP, servicios, el registro de Windows) y tiene que
;  ser un servicio del sistema. Eso si necesita permisos, una vez, al instalar.
;
;  LA TRAMPA IMPORTANTE, y por que el codigo de abajo da rodeos:
;
;  Este instalador corre ELEVADO. Si desde aqui lanzaramos el instalador de la app tal cual, la app
;  acabaria en la carpeta del ADMINISTRADOR, no en la de quien esta usando el PC. En un equipo donde
;  el usuario normal no es administrador, Playfront se instalaria en un perfil que nadie abre: la
;  instalacion diria "correcto" y la app no aparecia por ningun lado.
;
;  Por eso la parte de la app se lanza con ExecAsOriginalUser (baja los permisos a los de quien
;  ejecuto el instalador) Y ADEMAS a traves de cmd, para que sea ESE proceso quien resuelva
;  %LOCALAPPDATA%. Resolverlo aqui daria la carpeta del administrador otra vez.
; ===================================================================================================

#define AppName        "Playfront"
#define AppVersion     "0.1.0"
#define AppPublisher   "Playfront"
#define AppUrl         "https://github.com/AdriBuho/playfront"

; De donde se descarga todo. Al sacar una version nueva se cambia ReleaseTag y ya.
#define ReleaseTag     "v0.1.0"
#define BaseUrl        "https://github.com/AdriBuho/playfront/releases/download/" + ReleaseTag

#define ShellSetupFile "PlayfrontShell-win-Setup.exe"
#define HelperZipFile  "PlayfrontHelper.zip"
#define AssetsZipFile  "PlayfrontAssets-Games.zip"

; Sitio libre que hay que exigir: ~55 MB app + ~40 MB ayudante + ~416 MB assets, mas lo que ocupan
; descomprimidos y el margen de la descarga temporal. Se redondea muy al alza a proposito.
#define RequiredFreeMB 2048

[Setup]
; No cambiar AppId nunca: es lo que identifica esta instalacion para actualizarla o desinstalarla.
AppId={{7A5E6C41-9D3B-4F82-A0C7-1E5B8D24F930}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
VersionInfoVersion={#AppVersion}

; Aqui va el AYUDANTE. La app no: esa va a la carpeta del usuario (ver el comentario de arriba).
DefaultDirName={autopf}\Playfront
; No se deja elegir carpeta: el servicio tiene que estar en un sitio fijo y protegido, y la app ni
; siquiera se instala aqui. Dar a elegir seria mentir sobre lo que hace ese cuadro.
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=no

PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809 en adelante: es lo que necesita .NET 10 y lo que lleva cualquier Ally.
MinVersion=10.0.17763

OutputBaseFilename=PlayfrontSetup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\Helper\Playfront.Helper.exe

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Messages]
en.WelcomeLabel2=This will install [name/ver] on your computer.%n%nPlayfront replaces the Windows desktop with a console-style interface you drive with a controller. The installer downloads about 500 MB.%n%nWindows stays underneath and is always one button away.

[Dirs]
Name: "{app}\Helper"
Name: "{commonappdata}\Playfront\Assets"

[Code]
var
  DownloadPage: TDownloadWizardPage;
  ShellSetupPath, HelperZipPath, AssetsZipPath: String;
  // Evita descargar dos veces: en modo normal baja al pulsar "Instalar", y en modo silencioso
  // (/SILENT, que es como lo llamara cualquier automatismo) no hay asistente y baja mas tarde.
  FilesDownloaded: Boolean;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing),
    SetupMessage(msgPreparingDesc), @OnDownloadProgress);
end;

// Comprueba que hay sitio ANTES de descargar medio giga. Sin esto, el fallo aparece a mitad de la
// descarga o, peor, al descomprimir, cuando ya se ha tocado el sistema.
//
// Devuelve el motivo en texto, o '' si hay sitio. No enseña el aviso por su cuenta a proposito:
// en una instalacion silenciosa un cuadro de dialogo dejaria el instalador esperando un clic que
// nadie va a dar. Que lo enseñe quien llama, si procede.
function CheckFreeSpace(): String;
var
  FreeBytes, TotalBytes: Int64;
  FreeMB: Int64;
begin
  Result := '';
  if GetSpaceOnDisk64(ExpandConstant('{sd}\'), FreeBytes, TotalBytes) then
  begin
    FreeMB := FreeBytes / 1048576;
    if FreeMB < {#RequiredFreeMB} then
      Result := 'Not enough free disk space.' + #13#10#13#10 +
                'Playfront needs about ' + IntToStr({#RequiredFreeMB}) + ' MB free on ' +
                ExpandConstant('{sd}') + ', but only ' + IntToStr(FreeMB) + ' MB are available.' + #13#10#13#10 +
                'Free up some space and run this installer again.';
  end;
end;

// Descarga las tres piezas. Devuelve '' si fue bien, o el motivo del fallo en texto.
// Se llama desde DOS sitios a proposito (ver FilesDownloaded): al pulsar "Instalar" cuando hay
// asistente, y desde PrepareToInstall cuando se ejecuta en silencio. Si solo colgara del boton,
// una instalacion con /SILENT no descargaria nada y montaria una instalacion a medias.
function DownloadEverything(): String;
begin
  Result := '';
  if FilesDownloaded then
    Exit;

  DownloadPage.Clear;
  DownloadPage.Add('{#BaseUrl}/{#ShellSetupFile}', '{#ShellSetupFile}', '');
  DownloadPage.Add('{#BaseUrl}/{#HelperZipFile}',  '{#HelperZipFile}',  '');
  DownloadPage.Add('{#BaseUrl}/{#AssetsZipFile}',  '{#AssetsZipFile}',  '');
  if not WizardSilent then
    DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      ShellSetupPath := ExpandConstant('{tmp}\{#ShellSetupFile}');
      HelperZipPath  := ExpandConstant('{tmp}\{#HelperZipFile}');
      AssetsZipPath  := ExpandConstant('{tmp}\{#AssetsZipFile}');
      FilesDownloaded := True;
    except
      // Lo que falla casi siempre es la red. Se dice en claro en vez de soltar el error tecnico.
      Result := 'The download failed.' + #13#10#13#10 + GetExceptionMessage + #13#10#13#10 +
                'Check your internet connection and run this installer again.';
    end;
  finally
    if not WizardSilent then
      DownloadPage.Hide;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Error: String;
begin
  Result := True;
  if CurPageID = wpReady then
  begin
    Error := CheckFreeSpace();
    if Error = '' then
      Error := DownloadEverything();

    if Error <> '' then
    begin
      MsgBox(Error, mbCriticalError, MB_OK);
      Result := False;
    end;
  end;
end;

// Ultima red de seguridad antes de tocar el disco. Devolver texto aqui ABORTA la instalacion
// limpiamente y lo enseña, en vez de dejar el sistema a medias.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := CheckFreeSpace();
  if Result = '' then
    Result := DownloadEverything();
end;

// Descomprime con PowerShell y .NET en vez de con Expand-Archive: con 416 MB, Expand-Archive tarda
// varios minutos y este metodo segundos. -ExecutionPolicy Bypass para que no dependa de la politica
// de scripts de la maquina.
//
// OJO CON LA VERSION DE POWERSHELL (esto ya rompio una vez, el 2026-07-27):
// aqui corre el PowerShell que trae Windows (5.1, sobre .NET Framework), NO el 7. Y el 5.1 solo
// tiene DOS versiones de ExtractToDirectory:
//     (origen, destino)  y  (origen, destino, codificacion)
// La de tres parametros con "sobrescribir" solo existe en PowerShell 7. Usarla aqui hacia que
// fallara la descompresion entera sin decir por que. Por eso se llama con DOS parametros y se
// vacia el destino a mano antes.
function ExtractZip(const ZipPath, DestDir: String): Boolean;
var
  Cmd: String;
  ResultCode: Integer;
begin
  // Vaciar el destino primero: la version de dos parametros falla si ya hay un fichero con el
  // mismo nombre dentro. Sin esto, reinstalar encima daria error.
  if DirExists(DestDir) then
    DelTree(DestDir, True, True, True);

  Cmd := '-NoProfile -ExecutionPolicy Bypass -Command "' +
         '$ErrorActionPreference=''Stop''; ' +
         'Add-Type -AssemblyName System.IO.Compression.FileSystem; ' +
         '[IO.Compression.ZipFile]::ExtractToDirectory(''' + ZipPath + ''', ''' + DestDir + ''')"';

  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Cmd, '',
                 SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);

  // Queda en el registro del instalador (/LOG), que es donde se mira cuando algo falla en la
  // maquina de otro y no se puede depurar en directo.
  if Result then
    Log('ExtractZip OK: ' + ZipPath + ' -> ' + DestDir)
  else
    Log('ExtractZip FALLO (codigo ' + IntToStr(ResultCode) + '): ' + ZipPath + ' -> ' + DestDir);
end;

// Averigua la carpeta de datos del usuario QUE LANZO el instalador, no la del administrador que
// lo elevo. Se pregunta a un proceso lanzado con sus permisos ('echo %LOCALAPPDATA%') y se recoge
// la respuesta por un fichero, porque Exec no devuelve la salida.
//
// Hace falta guardarla: al DESINSTALAR ya no se puede preguntar (ExecAsOriginalUser no existe en
// esa fase), asi que la ruta se escribe en el registro aqui y se lee alli.
function GetOriginalUserLocalAppData(): String;
var
  TmpFile: String;
  ResultCode: Integer;
  Lines: TArrayOfString;
begin
  Result := '';
  TmpFile := ExpandConstant('{tmp}\localappdata.txt');
  if ExecAsOriginalUser(ExpandConstant('{cmd}'), '/C echo %LOCALAPPDATA%> "' + TmpFile + '"', '',
                        SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if LoadStringsFromFile(TmpFile, Lines) and (GetArrayLength(Lines) > 0) then
      Result := Trim(Lines[0]);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  HelperExe, AppDir: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  // --- 1. El ayudante: a Program Files y registrado como servicio -------------------------------
  if not ExtractZip(HelperZipPath, ExpandConstant('{app}\Helper')) then
  begin
    MsgBox('Could not unpack the helper service.' + #13#10#13#10 +
           'Playfront will still work, but features that need system access ' +
           '(power profiles, installing games) will be unavailable.', mbError, MB_OK);
  end
  else
  begin
    // El propio ejecutable se registra: '--install'. No hace falta sc.exe aqui.
    HelperExe := ExpandConstant('{app}\Helper\Playfront.Helper.exe');
    if not (Exec(HelperExe, '--install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0)) then
      MsgBox('The helper service could not be registered (code ' + IntToStr(ResultCode) + ').' + #13#10#13#10 +
             'Playfront will still work, but features that need system access will be unavailable.',
             mbError, MB_OK);
  end;

  // --- 2. Arte y videos: compartidos por todos los usuarios --------------------------------------
  // Si falla, la app arranca igual: se ve con huecos grises en vez de fondos. Se avisa y se sigue.
  if not ExtractZip(AssetsZipPath, ExpandConstant('{commonappdata}\Playfront\Assets')) then
    MsgBox('Could not unpack the artwork and videos.' + #13#10#13#10 +
           'Playfront will start and work normally, but game artwork will appear as empty placeholders.',
           mbInformation, MB_OK);

  // --- 3. La app: a la carpeta del USUARIO, sin permisos -----------------------------------------
  // Ver la explicacion de la cabecera: este instalador corre elevado, asi que hay que averiguar la
  // carpeta del usuario de verdad y lanzar la instalacion con SUS permisos.
  AppDir := GetOriginalUserLocalAppData();
  if AppDir = '' then
  begin
    // Ultimo recurso: la carpeta de quien esta ejecutando esto. Es correcta cuando el usuario es
    // administrador de su propio equipo, que es el caso normal en un PC de casa.
    Log('No se pudo averiguar la carpeta del usuario original; se usa la del proceso actual.');
    AppDir := ExpandConstant('{localappdata}');
  end;
  AppDir := AppDir + '\Programs\Playfront';
  Log('Playfront se instalara en: ' + AppDir);

  // Se guarda para poder desinstalar despues: en la desinstalacion ya no hay forma de averiguarla.
  RegWriteStringValue(HKEY_LOCAL_MACHINE, 'Software\Playfront', 'AppPath', AppDir);

  if not (ExecAsOriginalUser(ShellSetupPath, '--silent --installto "' + AppDir + '"', '',
                             SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0)) then
    MsgBox('Playfront could not be installed (code ' + IntToStr(ResultCode) + ').' + #13#10#13#10 +
           'The helper service was installed. Try running this installer again.',
           mbCriticalError, MB_OK);
end;

// ===================================================================================================
//  DESINSTALACION
//
//  Tiene que deshacer las TRES partes. La de la app se lanza otra vez como el usuario original: su
//  desinstalador vive en su carpeta, no aqui.
// ===================================================================================================
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  HelperExe, AppDir, Updater: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  // 1. Servicio fuera (parar + dar de baja). Lo hace el propio ejecutable.
  HelperExe := ExpandConstant('{app}\Helper\Playfront.Helper.exe');
  if FileExists(HelperExe) then
    Exec(HelperExe, '--uninstall', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // 2. La app del usuario. Su propio desinstalador (Update.exe) se encarga.
  //
  // OJO, esto ya rompio una vez (2026-07-27): AQUI NO SE PUEDE USAR ExecAsOriginalUser. Inno no lo
  // permite durante la desinstalacion y lanza un error fatal que ABORTA todo lo que venga despues
  // -- se quedaban sin borrar la app, el arte y hasta el propio desinstalador. Por eso la ruta se
  // guardo en el registro al instalar: aqui solo se lee y se ejecuta con Exec normal.
  if RegQueryStringValue(HKEY_LOCAL_MACHINE, 'Software\Playfront', 'AppPath', AppDir) then
  begin
    Updater := AppDir + '\Update.exe';
    if FileExists(Updater) then
    begin
      if Exec(Updater, '--uninstall --silent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
        Log('Desinstalada la app de ' + AppDir)
      else
        Log('No se pudo desinstalar la app de ' + AppDir);
    end;
  end
  else
    Log('No hay ruta guardada de la app; no se desinstala (habra que quitarla a mano).');

  RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE, 'Software\Playfront');

  // 3. Arte y videos.
  DelTree(ExpandConstant('{commonappdata}\Playfront\Assets'), True, True, True);

  // 4. El ayudante. Hay que borrarlo a mano: sus ficheros los descomprimio el instalador, no los
  //    puso Inno, asi que Inno no sabe que existen y dejaria la carpeta llena.
  DelTree(ExpandConstant('{app}\Helper'), True, True, True);

  // OJO: NO se toca %LocalAppData%\Playfront. Ahi viven los ajustes del usuario y su sesion de
  // YouTube, y borrarlos al desinstalar seria llevarse sus datos por delante.
end;
