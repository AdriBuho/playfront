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

; THE VERSION IS NOT WRITTEN HERE. Build-Installer.ps1 reads it from Directory.Build.props (the one
; place it is defined) and passes it in with /DPfVersion. Compiling without it is a hard error on
; purpose: a hardcoded number here would silently drift from the app's.
;
; What that drift costs, if it is ever "simplified" back to a literal: the release tag is derived from
; this number, so an installer left at an old version keeps downloading the OLD release forever. It
; installs fine, reports success, and hands out a stale build with nothing to indicate it. Same class
; of silent failure as pointing the updater at a repository that does not exist.
#ifndef PfVersion
  #error PfVersion was not supplied. Build with: build\installer\Build-Installer.ps1
#endif

#define AppName        "Playfront"
#define AppVersion     PfVersion
#define AppPublisher   "Playfront"
#define AppUrl         "https://github.com/AdriBuho/playfront"

; Where everything is downloaded from: the release tagged with this same version.
#define ReleaseTag     "v" + PfVersion
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
; Handover folder between this ELEVATED installer and the ORIGINAL user, deleted when done.
; It exists because {tmp} does not work for that: when someone elevates with a DIFFERENT
; administrator account, {tmp} belongs to the administrator and the real user cannot read or write
; there - so the app either installed into the wrong profile or not at all. "users-modify" is the
; minimum that lets them run the staged installer and drop their answer back.
Name: "{commonappdata}\Playfront\stage"; Permissions: users-modify

[Code]
var
  DownloadPage: TDownloadWizardPage;
  ShellSetupPath, HelperZipPath, AssetsZipPath: String;
  // Prevents downloading twice: interactively it downloads when "Install" is pressed; in silent mode
  // (/SILENT, how any automation invokes it) there is no wizard and it downloads later.
  FilesDownloaded: Boolean;
  // Tracked apart from the download so that retrying after a failed unpack really retries the unpack
  // instead of reporting success. See DownloadEverything.
  AssetsUnpacked: Boolean;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing),
    SetupMessage(msgPreparingDesc), @OnDownloadProgress);
end;

// Only x64 binaries are built, and ArchitecturesAllowed=x64compatible lets an ARM64 machine through
// so Windows 11 can run them under emulation. Windows 10 on ARM64 has NO x64 emulation, so there the
// install used to succeed and the app then refused to start with "this app can't run on your PC".
// Refusing up front, with the reason, beats a working installer and a dead shortcut.
function InitializeSetup(): Boolean;
begin
  Result := True;
  // GetWindowsVersion packs the version as (major shl 24) or (minor shl 16) or build.
  // Windows 11 is 10.0.22000, and 22000 = $55F0, hence $0A0055F0.
  if (ProcessorArchitecture = paARM64) and (GetWindowsVersion < $0A0055F0) then
  begin
    Result := False;
    MsgBox('Playfront cannot run on this PC.' + #13#10#13#10 +
           'This is an ARM64 computer running Windows 10, which cannot run 64-bit Intel programs. ' +
           'Windows 11 on ARM64 can, so upgrading Windows would make Playfront work here.',
           mbCriticalError, MB_OK);
  end;
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

// Unpacks through PowerShell and .NET rather than Expand-Archive: for 416 MB, Expand-Archive takes
// minutes and this takes seconds. -ExecutionPolicy Bypass so it does not depend on the machine's
// script policy.
//
// MIND THE POWERSHELL VERSION - this broke once already. What runs here is the PowerShell that ships
// with Windows (5.1, on .NET Framework), NOT 7. And 5.1 only has TWO overloads of ExtractToDirectory:
//     (source, destination)  and  (source, destination, encoding)
// The three-argument one with "overwrite" exists only in PowerShell 7. Using it here made the whole
// extraction fail without saying why. Hence two arguments, and emptying the destination by hand first.
//
// Declared BEFORE its callers: Pascal Script has no forward declarations, so moving this below
// DownloadEverything stops the whole script compiling.
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

// Downloads the three pieces AND unpacks the artwork. Returns '' on success, or the failure reason
// as text - and returning text aborts the install, which is the point.
//
// THE ARTWORK IS NOT OPTIONAL. It used to be unpacked later with a "carry on without it" warning, so
// a failure produced an installation that starts into a black screen with grey boxes and no way for
// the user to tell that from a broken install. Everything the user gets is one thing: one progress
// bar, and either it all lands or nothing is installed.
//
// Unpacking here rather than in CurStepChanged is deliberate: this is the last point at which Inno
// can still call the whole thing off cleanly instead of leaving the system half-built.
//
// Called from TWO places on purpose: from the "Install" button when there is a wizard, and from
// PrepareToInstall when running silently. Hanging it off the button alone meant a /SILENT install
// downloaded nothing and produced a half-built installation.
//
// TWO separate flags, and that matters. Pressing Install after a failed unpack calls this again; with
// one shared flag it returned "already done" and the install carried on WITHOUT the artwork - the
// exact outcome this function exists to prevent. Downloading and unpacking retry independently, so a
// retry does not re-fetch 470 MB that is already on disk.
function DownloadEverything(): String;
begin
  Result := '';

  if not FilesDownloaded then
  begin
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

    if Result <> '' then
      Exit;
  end;

  if not AssetsUnpacked then
  begin
    // Same page, new caption: unpacking 416 MB takes long enough to look like a freeze otherwise, and
    // to the user this is still one continuous operation with one progress bar.
    if not WizardSilent then
    begin
      DownloadPage.SetText('Unpacking the artwork and videos...', '');
      DownloadPage.Show;
    end;
    try
      // ForceDirectories because [Dirs] has not run yet at this point in the install.
      ForceDirectories(ExpandConstant('{commonappdata}\Playfront'));
      if ExtractZip(AssetsZipPath, ExpandConstant('{commonappdata}\Playfront\Assets')) then
        AssetsUnpacked := True
      else
        Result := 'The artwork and videos could not be unpacked.' + #13#10#13#10 +
                  'Playfront needs them, so nothing has been installed. Check there is enough free ' +
                  'disk space and run this installer again.';
    finally
      if not WizardSilent then
        DownloadPage.Hide;
    end;
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

// Resolves the data folder of the user WHO STARTED the installer, not that of the administrator who
// elevated it. It asks a process running with their rights ('echo %LOCALAPPDATA%') and collects the
// answer through a file, because Exec does not return output.
//
// The result has to be stored: at UNINSTALL time it can no longer be asked for (ExecAsOriginalUser
// does not exist in that phase), so the path is written to the registry here and read back there.
// It writes into the shared staging folder, NOT {tmp}: {tmp} belongs to whoever elevated the
// installer, and when that is a different administrator the original user cannot write there at all,
// so this silently returned '' and the app went into the administrator's profile.
function GetOriginalUserLocalAppData(): String;
var
  TmpFile: String;
  ResultCode: Integer;
  Lines: TArrayOfString;
begin
  Result := '';
  TmpFile := ExpandConstant('{commonappdata}\Playfront\stage\localappdata.txt');
  if ExecAsOriginalUser(ExpandConstant('{cmd}'), '/C echo %LOCALAPPDATA%> "' + TmpFile + '"', '',
                        SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if LoadStringsFromFile(TmpFile, Lines) and (GetArrayLength(Lines) > 0) then
      Result := Trim(Lines[0]);
  end;
end;

// Copies a downloaded file out of {tmp} and into the shared staging folder, so the original user can
// execute it. Returns the new path, or the original one if the copy failed (no worse than before).
function StageForOriginalUser(const SourcePath, FileName: String): String;
begin
  Result := ExpandConstant('{commonappdata}\Playfront\stage\') + FileName;
  if not CopyFile(SourcePath, Result, False) then
  begin
    Log('Could not stage ' + FileName + ' for the original user; using the temporary copy.');
    Result := SourcePath;
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
  // Deregister the OLD service first. On an upgrade it is running as SYSTEM, so its .exe is locked
  // and the folder below cannot be emptied: extraction then failed and the machine kept the old
  // helper forever while the app moved on. Failure here is fine - it just means none was installed.
  HelperExe := ExpandConstant('{app}\Helper\Playfront.Helper.exe');
  if FileExists(HelperExe) then
  begin
    Log('Existing helper found; deregistering it before replacing the files.');
    Exec(HelperExe, '--uninstall', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

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

  // --- 2. Artwork and video ---------------------------------------------------------------------
  // Already unpacked, in DownloadEverything. It is a REQUIRED part of the product, so it happens
  // where a failure can still call the whole install off instead of producing a Playfront with no
  // backgrounds and no artwork.

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

  // Run it from the shared folder: {tmp} is not readable by the original user when a different
  // administrator elevated this.
  ShellSetupPath := StageForOriginalUser(ShellSetupPath, '{#ShellSetupFile}');

  if not (ExecAsOriginalUser(ShellSetupPath, '--silent --installto "' + AppDir + '"', '',
                             SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0)) then
    MsgBox('Playfront could not be installed (code ' + IntToStr(ResultCode) + ').' + #13#10#13#10 +
           'The helper service was installed. Try running this installer again.',
           mbCriticalError, MB_OK);

  // The handover is over; nothing here is needed again. Leaving a world-writable folder behind on
  // every machine would be careless.
  DelTree(ExpandConstant('{commonappdata}\Playfront\stage'), True, True, True);
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
  // ...and the parent it lived in. Inno only tracks what [Dirs] created, and the Assets folder is
  // emptied by the line above, so without this an empty %ProgramData%\Playfront was left behind.
  // RemoveDir only deletes an EMPTY directory, so anything else that ever lands there survives.
  RemoveDir(ExpandConstant('{commonappdata}\Playfront'));

  // 4. The helper. Deleted by hand: its files were unpacked by this script rather than laid down by
  //    Inno, so Inno does not know they exist and would leave the folder full.
  DelTree(ExpandConstant('{app}\Helper'), True, True, True);

  // %LocalAppData%\Playfront is deliberately NOT touched: the user's settings and YouTube session
  // live there, and wiping them on uninstall would take their data with it.
end;