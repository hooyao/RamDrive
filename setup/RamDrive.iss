; RamDrive Inno Setup Script
; Bundles RamDrive AOT exe + WinFsp installer
; Supports both portable (green) and Windows Service modes

#define MyAppName      "RamDrive"
#define MyAppVersion   "1.0.0"
#define MyAppPublisher "HYProjects"
#define MyAppExeName   "RamDrive.exe"
#define MyAppURL       "https://github.com/hooyao/RamDrive"

; Path to AOT publish output (relative to this .iss file)
#define PublishDir     "..\publish-aot"

; WinFsp MSI filename â€” place the .msi in the setup\ folder before compiling
; Download from https://winfsp.dev/rel/  (e.g. winfsp-2.1.24352.msi)
#define WinFspMsi      "winfsp-2.1.25156.msi"

[Setup]
AppId={{E8A3F4D1-7B2C-4E5A-9F6D-1A2B3C4D5E6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\installer-output
OutputBaseFilename=RamDrive-{#MyAppVersion}-setup
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitModeOnly=x64compatible
MinVersion=10.0
SetupIconFile=compiler:SetupClassicIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full";    Description: "Full installation (RamDrive + WinFsp + Windows Service)"
Name: "green";   Description: "Portable / Green installation (RamDrive only)"
Name: "custom";  Description: "Custom installation"; Flags: iscustom

[Components]
Name: "main";    Description: "RamDrive core files";    Types: full green custom; Flags: fixed
Name: "winfsp";  Description: "WinFsp file system driver (required if not already installed)"; Types: full custom
Name: "service"; Description: "Register as Windows Service (auto-start with Windows)"; Types: full custom

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
; RamDrive AOT binaries
Source: "{#PublishDir}\RamDrive.exe";      DestDir: "{app}"; Flags: ignoreversion; Components: main
Source: "{#PublishDir}\appsettings.jsonc";  DestDir: "{app}"; Flags: ignoreversion onlyifdoesntexist; Components: main

; WinFsp MSI bundled installer
Source: "{#WinFspMsi}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Components: winfsp

[Icons]
Name: "{group}\{#MyAppName}";         Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}";   Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Code]
function IsWinFspInstalled: Boolean;
var
  InstallDir: String;
begin
  Result := RegQueryStringValue(HKLM, 'SOFTWARE\WinFsp', 'InstallDir', InstallDir) and (InstallDir <> '');
end;

function IsServiceInstalled: Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('sc.exe', 'query RamDrive', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
            and (ResultCode = 0);
end;

procedure StopAndDeleteService;
var
  ResultCode: Integer;
begin
  Exec('sc.exe', 'stop RamDrive', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(2000);
  Exec('sc.exe', 'delete RamDrive', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure InstallWinFsp;
var
  ResultCode: Integer;
  MsiPath: String;
begin
  MsiPath := ExpandConstant('{tmp}\{#WinFspMsi}');
  if not Exec('msiexec.exe',
              '/i "' + MsiPath + '" /qb INSTALLLEVEL=1000',
              '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('WinFsp installation failed. Please install WinFsp manually from https://winfsp.dev/rel/', mbError, MB_OK);
  end;
end;

procedure CreateService;
var
  ResultCode: Integer;
  ExePath: String;
begin
  ExePath := ExpandConstant('{app}\{#MyAppExeName}');

  // Create auto-start service
  Exec('sc.exe',
       'create RamDrive binPath= "' + ExePath + '" start= auto DisplayName= "RamDrive RAM Disk"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Set description
  Exec('sc.exe',
       'description RamDrive "High-performance RAM disk using WinFsp. Provides a virtual drive backed entirely by system memory."',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Failure recovery: restart after 5s, 10s, 30s
  Exec('sc.exe',
       'failure RamDrive reset= 60 actions= restart/5000/restart/10000/restart/30000',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Start in early service group (before most user-mode services)
  RegWriteStringValue(HKLM,
       'SYSTEM\CurrentControlSet\Services\RamDrive',
       'Group', 'FSFilter Activity Monitor');
end;

function InitializeSetup: Boolean;
begin
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Install WinFsp if selected and not already present
    if WizardIsComponentSelected('winfsp') and not IsWinFspInstalled then
      InstallWinFsp;

    // Stop existing service before re-registering
    if IsServiceInstalled then
      StopAndDeleteService;

    // Register Windows Service if selected
    if WizardIsComponentSelected('service') then
      CreateService;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    // Stop and remove the service
    if IsServiceInstalled then
      StopAndDeleteService;
  end;
end;

[Run]
; Optionally start the service right after install
Filename: "sc.exe"; Parameters: "start RamDrive"; \
  StatusMsg: "Starting RamDrive service..."; \
  Flags: runhidden nowait; Components: service

; Or launch the app directly (green mode)
Filename: "{app}\{#MyAppExeName}"; \
  Description: "Launch {#MyAppName} now"; \
  Flags: nowait postinstall skipifsilent; Components: not service
