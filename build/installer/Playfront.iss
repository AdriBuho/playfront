; ===================================================================================================
;  Playfront installer
;
;  A single few-MB .exe that downloads and sets up everything:
;
;    1. The app       -> user folder, NO administrator rights
;    2. The helper    -> Program Files + Windows service, WITH administrator rights
;    3. Artwork/video -> ProgramData, shared by every account
;
;  WHY IT IS SPLIT THIS WAY
;
;  The app must live in the user folder so it can update itself silently. From Program Files, every
;  update would prompt for administrator rights. It is the same reason Chrome and Discord install into
;  the user folder.
;
;  The helper does the privileged work (TDP, services, the Windows registry) and has to be a system
;  service, which does need administrator rights - once, at install time.
;
;  THE TRAP, and why the code below takes a detour:
;
;  This installer runs ELEVATED. Launching the app's own installer from here directly would put the
;  app in the ADMINISTRATOR's folder, not that of whoever is using the PC. On a machine where the
;  everyday user is not an administrator, Playfront would install into a profile nobody opens: the
;  install would report success and the app would be nowhere.
;
;  So the app step runs through ExecAsOriginalUser, which drops back to the rights of whoever started
;  the installer. The user's real folder is resolved by asking a process running as them (see
;  GetOriginalUserLocalAppData); resolving it here would give the administrator's folder again.
; ===================================================================================================

#define AppName        "Playfront"
#define AppVersion     "0.1.0"
#define AppPublisher   "Playfront"
#define AppUrl         "https://github.com/AdriBuho/playfront"

; Where everything is downloaded from. Cutting a new version means changing ReleaseTag and nothing else.
#define ReleaseTag     "v0.1.0"
#define BaseUrl        "https://github.com/AdriBuho/playfront/releases/download/" + ReleaseTag

#define ShellSetupFile "PlayfrontShell-win-Setup.exe"
#define HelperZipFile  "PlayfrontHelper.zip"
#define AssetsZipFile  "PlayfrontAssets-Games.zip"

; Free space to demand: ~55 MB app + ~40 MB helper + ~416 MB assets, plus what they take unpacked and
; headroom for the temporary download. Rounded well up on purpose.
#define RequiredFreeMB 2048

[Setup]
; Never change AppId: it is what identifies this installation for upgrading and uninstalling.
AppId={{7A5E6C41-9D3B-4F82-A0C7-1E5B8D24F930}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
VersionInfoVersion={#AppVersion}

; This is where the HELPER goes. Not the app: that goes to the user folder (see the header).
DefaultDirName={autopf}\Playfront
; No folder choice: the service has to sit in a fixed, protected location, and the app is not even
; installed here. Offering a choice would misrepresent what that box does.
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=no

PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809 and later: what .NET 10 needs, and what any Ally ships with.
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
  // Prevents downloading twice: interactively it downloads when "Install" is pressed; in silent mode
  // (/SILENT, how any automation invokes it) there is no wizard and it downloads later.
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

// Checks for space BEFORE downloading half a gigabyte. Without this the failure surfaces mid-download
// or, worse, while unpacking, once the system has already been touched.
//
// Returns the reason as text, or '' when there is room. It deliberately does not show the message
// itself: in a silent install a dialog would leave the installer waiting for a click nobody will
// give. The caller shows it, if appropriate.
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

// Downloads the three pieces. Returns '' on success, or the failure reason as text.
//
// Called from TWO places on purpose (see FilesDownloaded): from the "Install" button when there is a
// wizard, and from PrepareToInstall when running silently. Hanging it off the button alone meant a
// /SILENT install downloaded nothing and produced a half-built installation.
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
      // What fails is almost always the network. Say so plainly instead of dumping the raw error.
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

// Last safety net before touching the disk. Returning text here ABORTS the install cleanly and shows
// the reason, rather than leaving the system half-built.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := CheckFreeSpace();
  if Result = '' then
    Result := DownloadEverything();
end;

// Unpacks through PowerShell and .NET rather than Expand-Archive: for 416 MB, Expand-Archive takes
// minutes and this takes seconds. -ExecutionPolicy Bypass so it does not depend on the machine's
// script policy.
//
// MIND THE POWERSHELL VERSION - this broke once already. What runs here is the PowerShell that ships
// with Windows (5.1, on .NET Framework), NOT 7. And 5.1 only has TWO overloads of ExtractToDirectory:
//     (source, destination)  and  (source, destination, encoding)
// The three-argument one with "overwrite" exists only in PowerShell 7. Using it here made the whole
// extraction fail without saying why. Hence two arguments, and emptying the destination by hand first.
function ExtractZip(const ZipPath, DestDir: String): Boolean;
var
  Cmd: String;
  ResultCode: Integer;
begin
  // Empty the destination first: the two-argument overload fails if a file of the same name is
  // already there. Without this, reinstalling over an existing install would error out.
  if DirExists(DestDir) then
    DelTree(DestDir, True, True, True);

  Cmd := '-NoProfile -ExecutionPolicy Bypass -Command "' +
         '$ErrorActionPreference=''Stop''; ' +
         'Add-Type -AssemblyName System.IO.Compression.FileSystem; ' +
         '[IO.Compression.ZipFile]::ExtractToDirectory(''' + ZipPath + ''', ''' + DestDir + ''')"';

  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Cmd, '',
                 SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);

  // Lands in the installer log (/LOG), which is what gets read when this fails on someone else's
  // machine and there is no way to debug it live.
  if Result then
    Log('ExtractZip OK: ' + ZipPath + ' -> ' + DestDir)
  else
    Log('ExtractZip FAILED (code ' + IntToStr(ResultCode) + '): ' + ZipPath + ' -> ' + DestDir);
end;

// Resolves the data folder of the user WHO STARTED the installer, not that of the administrator who
// elevated it. It asks a process running with their rights ('echo %LOCALAPPDATA%') and collects the
// answer through a file, because Exec does not return output.
//
// The result has to be stored: at UNINSTALL time it can no longer be asked for (ExecAsOriginalUser
// does not exist in that phase), so the path is written to the registry here and read back there.
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

  // --- 1. Helper: into Program Files, registered as a service -----------------------------------
  if not ExtractZip(HelperZipPath, ExpandConstant('{app}\Helper')) then
  begin
    MsgBox('Could not unpack the helper service.' + #13#10#13#10 +
           'Playfront will still work, but features that need system access ' +
           '(power profiles, installing games) will be unavailable.', mbError, MB_OK);
  end
  else
  begin
    // The executable registers itself with '--install'; no sc.exe needed here.
    HelperExe := ExpandConstant('{app}\Helper\Playfront.Helper.exe');
    if not (Exec(HelperExe, '--install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0)) then
      MsgBox('The helper service could not be registered (code ' + IntToStr(ResultCode) + ').' + #13#10#13#10 +
             'Playfront will still work, but features that need system access will be unavailable.',
             mbError, MB_OK);
  end;

  // --- 2. Artwork and video: shared by every account --------------------------------------------
  // On failure the app still starts, showing grey placeholders instead of backgrounds. Warn and go on.
  if not ExtractZip(AssetsZipPath, ExpandConstant('{commonappdata}\Playfront\Assets')) then
    MsgBox('Could not unpack the artwork and videos.' + #13#10#13#10 +
           'Playfront will start and work normally, but game artwork will appear as empty placeholders.',
           mbInformation, MB_OK);

  // --- 3. App: into the USER folder, without elevation -------------------------------------------
  // See the header: this installer runs elevated, so the real user's folder has to be resolved and
  // the install launched with THEIR rights.
  AppDir := GetOriginalUserLocalAppData();
  if AppDir = '' then
  begin
    // Last resort: the folder of whoever is running this. Correct when the user is an administrator
    // of their own machine, which is the normal case on a home PC.
    Log('Could not resolve the original user folder; falling back to the current process one.');
    AppDir := ExpandConstant('{localappdata}');
  end;
  AppDir := AppDir + '\Programs\Playfront';
  Log('Playfront will be installed to: ' + AppDir);

  // Stored so the uninstaller can find it: by then there is no way left to resolve it.
  RegWriteStringValue(HKEY_LOCAL_MACHINE, 'Software\Playfront', 'AppPath', AppDir);

  if not (ExecAsOriginalUser(ShellSetupPath, '--silent --installto "' + AppDir + '"', '',
                             SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0)) then
    MsgBox('Playfront could not be installed (code ' + IntToStr(ResultCode) + ').' + #13#10#13#10 +
           'The helper service was installed. Try running this installer again.',
           mbCriticalError, MB_OK);
end;

// ===================================================================================================
//  UNINSTALL
//
//  Has to undo all THREE parts. The app's own uninstaller lives in the user folder, not here.
// ===================================================================================================
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  HelperExe, AppDir, Updater: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  // 1. Service out (stop + deregister). The executable does it itself.
  HelperExe := ExpandConstant('{app}\Helper\Playfront.Helper.exe');
  if FileExists(HelperExe) then
    Exec(HelperExe, '--uninstall', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // 2. The user's app, handled by its own uninstaller (Update.exe).
  //
  // ExecAsOriginalUser CANNOT BE USED HERE - this broke once already. Inno forbids it during
  // uninstall and raises a fatal error that ABORTS everything after it, leaving the app, the artwork
  // and even the uninstaller itself in place. Hence the path saved to the registry at install time:
  // here it is only read back and run with a plain Exec.
  if RegQueryStringValue(HKEY_LOCAL_MACHINE, 'Software\Playfront', 'AppPath', AppDir) then
  begin
    Updater := AppDir + '\Update.exe';
    if FileExists(Updater) then
    begin
      if Exec(Updater, '--uninstall --silent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
        Log('Uninstalled the app from ' + AppDir)
      else
        Log('Could not uninstall the app from ' + AppDir);
    end;
  end
  else
    Log('No stored app path; skipping (it will have to be removed by hand).');

  RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE, 'Software\Playfront');

  // 3. Artwork and video.
  DelTree(ExpandConstant('{commonappdata}\Playfront\Assets'), True, True, True);

  // 4. The helper. Deleted by hand: its files were unpacked by this script rather than laid down by
  //    Inno, so Inno does not know they exist and would leave the folder full.
  DelTree(ExpandConstant('{app}\Helper'), True, True, True);

  // %LocalAppData%\Playfront is deliberately NOT touched: the user's settings and YouTube session
  // live there, and wiping them on uninstall would take their data with it.
end;