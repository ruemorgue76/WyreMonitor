; WyreMonitor installer (Inno Setup 6.1 or later). Built by Build-Installer.ps1, which passes the
; version read from WyreMonitor.csproj: it is written only there.
;
; What it sets up:
;   - the app in Program Files (the folder can't be changed: only administrators may write there,
;     and the service runs this exe as LocalSystem)
;   - the WyreMonitor service, LocalSystem, automatic start: it reads the sensors
;   - optionally a per-user Run entry that shows the icon next to the clock at sign-in
;   - PawnIO, the signed sensor driver, downloaded at a pinned version and checked by SHA-256
; Every program it runs is called by its full path in System32.

#ifndef AppVersion
  #error Missing version: build with Build-Installer.ps1 (ISCC /DAppVersion=x.y.z)
#endif

; PawnIO 2.2.0, from https://github.com/namazso/PawnIO.Setup/releases. The hash matches the
; winget-pkgs manifest and the file is Authenticode-signed by namazso.
#define PawnIOUrl "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe"
#define PawnIOSha256 "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032"

[Setup]
AppId={{7B0F4E32-6A1C-4D58-9E27-3C1A8F5D2B64}
AppName=WyreMonitor
AppVersion={#AppVersion}
AppVerName=WyreMonitor {#AppVersion}
DefaultDirName={autopf}\WyreMonitor
DisableDirPage=yes
; An earlier install in another folder must not be reused: see PrepareToInstall.
UsePreviousAppDir=no
DefaultGroupName=WyreMonitor
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=output
OutputBaseFilename=WyreMonitor-Setup-{#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoProductName=WyreMonitor
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription=WyreMonitor Setup
; Before installing: what the setup changes on the PC (a service with system rights, a driver).
InfoBeforeFile=before-install.txt
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\WyreMonitor.exe
UninstallDisplayName=WyreMonitor
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Show the WyreMonitor icon next to the clock when I sign in"
Name: "desktopicon"; Description: "Create a desktop shortcut"

[InstallDelete]
; The page's files are replaced as a whole on every update: files a newer version dropped
; (an old image, say) must not stay behind and keep being served.
Type: filesandordirs; Name: "{app}\wwwroot"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; The ID lets the app show Windows notifications under its own name (see Desktop/Toasts.cs).
Name: "{group}\WyreMonitor"; Filename: "{app}\WyreMonitor.exe"; AppUserModelID: "WyreMonitor.App"
Name: "{autodesktop}\WyreMonitor"; Filename: "{app}\WyreMonitor.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WyreMonitor"; ValueData: """{app}\WyreMonitor.exe"" --background"; Tasks: autostart; Flags: uninsdeletevalue
; Written by the service (its port) and by Windows (the Event Log source): removed on uninstall.
Root: HKLM; Subkey: "SOFTWARE\WyreMonitor"; Flags: uninsdeletekey dontcreatekey
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\EventLog\Application\WyreMonitor"; Flags: uninsdeletekey dontcreatekey

[Run]
; runasoriginaluser: the window opens with the user's rights, not the installer's.
Filename: "{app}\WyreMonitor.exe"; Description: "Open WyreMonitor"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop WyreMonitor"; Flags: runhidden; RunOnceId: "StopService"
Filename: "{sys}\taskkill.exe"; Parameters: "/f /t /im WyreMonitor.exe"; Flags: runhidden; RunOnceId: "StopApp"
Filename: "{sys}\sc.exe"; Parameters: "delete WyreMonitor"; Flags: runhidden; RunOnceId: "DeleteService"

[Code]
function Run(const Exe, Params: String): Integer;
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{sys}\') + Exe, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    ResultCode := -1;
  Result := ResultCode;
end;

// True when PawnIO 2.2.0 or later is installed. Older versions are replaced: 2.1.0 and earlier
// had known problems (an anti-cheat conflict, fixed in 2.1.0).
function IsPawnIOCurrent: Boolean;
var
  Installed: String;
  Version, Wanted: Int64;
begin
  Result := False;
  if not RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO', 'DisplayVersion', Installed) then
    exit;
  if StrToVersion(Installed, Version) and StrToVersion('2.2.0.0', Wanted) then
    Result := ComparePackedVersion(Version, Wanted) >= 0;
end;

// The service runs this folder's files as LocalSystem: it must be a folder only administrators
// can write to. Program Files is; a folder chosen with /DIR= may not be.
function IsInProgramFiles: Boolean;
begin
  Result := Pos(Lowercase(AddBackslash(ExpandConstant('{commonpf64}'))), Lowercase(AddBackslash(ExpandConstant('{app}')))) = 1;
end;

// An update must be able to replace the files: stop the service and close the app.
// Versions 1.0.x started the app through two scheduled tasks: remove them.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if not IsInProgramFiles then
  begin
    Result := 'WyreMonitor must be installed in ' + ExpandConstant('{commonpf64}') +
      ', because its service runs the installed files with full system rights.';
    exit;
  end;
  Run('sc.exe', 'stop WyreMonitor');
  Sleep(2000);
  Run('taskkill.exe', '/f /t /im WyreMonitor.exe');
  Run('schtasks.exe', '/delete /tn "WyreMonitor" /f');
  Run('schtasks.exe', '/delete /tn "WyreMonitor Autostart" /f');
  Sleep(800);
  Result := '';
end;

procedure InstallPawnIO;
var
  Code: Integer;
  Setup: String;
begin
  WizardForm.StatusLabel.Caption := 'Downloading the PawnIO driver...';
  Setup := ExpandConstant('{app}\PawnIO_setup.exe');
  try
    // Raises an exception if the download fails or the SHA-256 doesn't match.
    DownloadTemporaryFile('{#PawnIOUrl}', 'PawnIO_setup.exe', '{#PawnIOSha256}', nil);
    // Run it from Program Files, where only administrators can write, after checking the hash
    // again there: the temporary folder belongs to the user, so its copy could be swapped.
    if not FileCopy(ExpandConstant('{tmp}\PawnIO_setup.exe'), Setup, False) then
      RaiseException('could not copy the PawnIO setup');
    if Lowercase(GetSHA256OfFile(Setup)) <> '{#PawnIOSha256}' then
      RaiseException('the PawnIO setup changed after download');
    WizardForm.StatusLabel.Caption := 'Installing the PawnIO driver...';
    if not Exec(Setup, '-install -silent', '', SW_HIDE, ewWaitUntilTerminated, Code) then
      Code := -1;
    DeleteFile(Setup);
    if (Code <> 0) and (Code <> 3010) then
      RaiseException('PawnIO setup ended with code ' + IntToStr(Code));
  except
    DeleteFile(Setup);
    SuppressibleMsgBox('The PawnIO driver could not be installed: ' + GetExceptionMessage + #13#10#13#10 +
      'Without it, CPU temperature and fans are missing. You can install it from ' +
      'https://github.com/namazso/PawnIO.Setup/releases and then restart the WyreMonitor service.',
      mbError, MB_OK, IDOK);
  end;
end;

procedure InstallService;
var
  Exe: String;
  Attempt, Code: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Starting the WyreMonitor service...';
  Exe := ExpandConstant('{app}\WyreMonitor.exe');
  Run('sc.exe', 'stop WyreMonitor');
  Run('sc.exe', 'delete WyreMonitor');
  // A deleted service lingers while something holds a handle to it (Services open, for one):
  // creating it again fails with 1072 until then, so retry for a while.
  Code := -1;
  for Attempt := 1 to 15 do
  begin
    Code := Run('sc.exe', 'create WyreMonitor binPath= "\"' + Exe + '\" --service" start= auto DisplayName= "WyreMonitor"');
    if Code = 0 then break;
    Sleep(1000);
  end;
  if Code <> 0 then
  begin
    SuppressibleMsgBox('The WyreMonitor service could not be created (error ' + IntToStr(Code) + '). ' +
      'Close the Services window if it is open, then run the installer again.', mbError, MB_OK, IDOK);
    exit;
  end;
  Run('sc.exe', 'description WyreMonitor "Reads hardware sensors for the WyreMonitor app and serves them on 127.0.0.1 only."');
  Run('sc.exe', 'failure WyreMonitor reset= 86400 actions= restart/5000/restart/5000/restart/60000');
  // Least privilege: keep only the privileges reading the sensors needs, and give the service
  // its own SID.
  Run('sc.exe', 'privs WyreMonitor SeChangeNotifyPrivilege/SeCreateGlobalPrivilege/SeIncreaseWorkingSetPrivilege');
  Run('sc.exe', 'sidtype WyreMonitor unrestricted');
  if Run('sc.exe', 'start WyreMonitor') <> 0 then
    SuppressibleMsgBox('The WyreMonitor service did not start. Open Services, find WyreMonitor and start it, or reinstall.',
      mbError, MB_OK, IDOK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not IsPawnIOCurrent then InstallPawnIO;
    InstallService;
  end;
end;
