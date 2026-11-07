; WyreMonitor installer (Inno Setup 6). Built by Build-Installer.ps1, which passes the
; version read from WyreMonitor.csproj: it is written only there.

#ifndef AppVersion
  #error Missing version: build with Build-Installer.ps1 (ISCC /DAppVersion=x.y.z)
#endif

[Setup]
AppId={{7B0F4E32-6A1C-4D58-9E27-3C1A8F5D2B64}
AppName=WyreMonitor
AppVersion={#AppVersion}
AppVerName=WyreMonitor {#AppVersion}
DefaultDirName={autopf}\WyreMonitor
DefaultGroupName=WyreMonitor
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=output
OutputBaseFilename=WyreMonitor-Setup-{#AppVersion}
VersionInfoVersion={#AppVersion}
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
Name: "autostart"; Description: "Start WyreMonitor with Windows (only the icon next to the clock)"
Name: "desktopicon"; Description: "Create a desktop shortcut"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "install-tasks.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\WyreMonitor"; Filename: "{app}\WyreMonitor.exe"
Name: "{autodesktop}\WyreMonitor"; Filename: "{app}\WyreMonitor.exe"; Tasks: desktopicon

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\install-tasks.ps1"" -ExePath ""{app}\WyreMonitor.exe"" {code:AutostartArgument}"; StatusMsg: "Setting up startup..."; Flags: runhidden
Filename: "{app}\WyreMonitor.exe"; Description: "Open WyreMonitor"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/f /t /im WyreMonitor.exe"; Flags: runhidden; RunOnceId: "StopApp"
Filename: "schtasks.exe"; Parameters: "/delete /tn ""WyreMonitor"" /f"; Flags: runhidden; RunOnceId: "RemoveLaunchTask"
Filename: "schtasks.exe"; Parameters: "/delete /tn ""WyreMonitor Autostart"" /f"; Flags: runhidden; RunOnceId: "RemoveAutostartTask"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\WyreMonitor"

[Code]
function AutostartArgument(Param: String): String;
begin
  if WizardIsTaskSelected('autostart') then
    Result := '-Autostart'
  else
    Result := '';
end;

function IsPawnIOInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO')
    or RegKeyExists(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO');
end;

// An update must be able to replace the files: close the running instance.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/f /t /im WyreMonitor.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(800);
  Result := '';
end;

// PawnIO is the signed driver that reads CPU temperature, power and fans, also with
// Windows Memory Integrity on. It is installed separately, from its own publisher.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and not IsPawnIOInstalled then
  begin
    WizardForm.StatusLabel.Caption := 'Installing the PawnIO driver...';
    Exec(ExpandConstant('{cmd}'),
      '/c winget install --id=namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if not IsPawnIOInstalled then
      SuppressibleMsgBox('The PawnIO driver could not be installed. Without it, CPU temperature and fans are missing.' + #13#10#13#10 +
        'Install it with: winget install --id=namazso.PawnIO -e',
        mbError, MB_OK, IDOK);
  end;
end;
