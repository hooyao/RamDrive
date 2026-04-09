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

; WinFsp MSI filename - place the .msi in the setup\ folder before compiling
; Download from https://winfsp.dev/rel/
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
ArchitecturesAllowed=x64compatible
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
Name: "{group}\Edit Configuration";   Filename: "notepad.exe"; Parameters: """{app}\appsettings.jsonc"""
Name: "{group}\Restart Service";      Filename: "cmd.exe"; Parameters: "/c sc.exe stop RamDrive & timeout /t 2 & sc.exe start RamDrive & pause"; Components: service
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Code]
var
  ConfigPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  ConfigPage := CreateInputQueryPage(wpSelectTasks,
    'RamDrive Configuration',
    'Configure the RAM disk settings.',
    'Choose a drive letter and capacity. To change these later:' + #13#10 +
    '1. Edit appsettings.jsonc (Start Menu > RamDrive > Edit Configuration)' + #13#10 +
    '2. Restart the service: run "sc.exe stop RamDrive && sc.exe start RamDrive"' + #13#10 +
    '   or use Start Menu > RamDrive > Restart Service');

  ConfigPage.Add('Drive letter (e.g. R):', False);
  ConfigPage.Add('Capacity in MB (e.g. 2048 = 2 GB):', False);

  ConfigPage.Values[0] := 'R';
  ConfigPage.Values[1] := '2048';
end;

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

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Letter: String;
  Cap: String;
  CapVal: Integer;
begin
  Result := True;
  if CurPageID = ConfigPage.ID then
  begin
    Letter := Trim(ConfigPage.Values[0]);
    Cap := Trim(ConfigPage.Values[1]);

    // Validate drive letter
    if (Length(Letter) <> 1) or ((Letter[1] < 'A') or (Letter[1] > 'Z')) and ((Letter[1] < 'a') or (Letter[1] > 'z')) then
    begin
      MsgBox('Please enter a single drive letter (A-Z).', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    // Validate capacity
    CapVal := StrToIntDef(Cap, 0);
    if CapVal < 16 then
    begin
      MsgBox('Capacity must be at least 16 MB.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
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

procedure ConfigureWinFspMountManager;
begin
  // Enable Mount Manager for non-admin mounts so the drive is visible to all apps
  RegWriteDWordValue(HKLM,
       'SOFTWARE\WOW6432Node\WinFsp',
       'MountUseMountmgrFromFSD', 1);
end;

procedure WriteAppSettings;
var
  ConfigPath: String;
  Letter: String;
  Cap: String;
  Lines: TArrayOfString;
begin
  Letter := UpperCase(Trim(ConfigPage.Values[0]));
  Cap := Trim(ConfigPage.Values[1]);
  ConfigPath := ExpandConstant('{app}\appsettings.jsonc');

  SetArrayLength(Lines, 20);
  Lines[0]  := '{';
  Lines[1]  := '  "Logging": {';
  Lines[2]  := '    "LogLevel": {';
  Lines[3]  := '      "Default": "Information",';
  Lines[4]  := '      "Microsoft": "Warning"';
  Lines[5]  := '    }';
  Lines[6]  := '  },';
  Lines[7]  := '';
  Lines[8]  := '  "RamDrive": {';
  Lines[9]  := '    "MountPoint": "' + Letter + ':\\",';
  Lines[10] := '    "CapacityMb": ' + Cap + ',';
  Lines[11] := '    "PageSizeKb": 64,';
  Lines[12] := '    "PreAllocate": false,';
  Lines[13] := '    "VolumeLabel": "RamDrive",';
  Lines[14] := '    "EnableKernelCache": true';
  Lines[15] := '  }';
  Lines[16] := '}';
  Lines[17] := '';

  SaveStringsToUTF8File(ConfigPath, Lines, False);
end;

procedure CreateService;
var
  ExePath: String;
  SvcKey: String;
  ResultCode: Integer;
begin
  ExePath := ExpandConstant('{app}\{#MyAppExeName}');
  SvcKey := 'SYSTEM\CurrentControlSet\Services\RamDrive';

  // Create service directly via registry (avoids sc.exe quoting issues with spaces in path)
  RegWriteDWordValue(HKLM, SvcKey, 'Type', 16);           // WIN32_OWN_PROCESS
  RegWriteDWordValue(HKLM, SvcKey, 'Start', 2);           // AUTO_START
  RegWriteDWordValue(HKLM, SvcKey, 'ErrorControl', 1);    // NORMAL
  RegWriteExpandStringValue(HKLM, SvcKey, 'ImagePath', '"' + ExePath + '"');
  RegWriteStringValue(HKLM, SvcKey, 'ObjectName', 'LocalSystem');
  RegWriteStringValue(HKLM, SvcKey, 'DisplayName', 'RamDrive RAM Disk');
  RegWriteStringValue(HKLM, SvcKey, 'Description',
       'High-performance RAM disk using WinFsp. Provides a virtual drive backed entirely by system memory.');

  // Start in early service group (before most user-mode services)
  RegWriteStringValue(HKLM, SvcKey, 'Group', 'FSFilter Activity Monitor');

  // Failure recovery: restart after 5s, 10s, 30s
  // FailureActions is a binary blob, using sc.exe for this specific setting
  Exec('sc.exe', 'failure RamDrive reset= 60 actions= restart/5000/restart/10000/restart/30000',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
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

    // Enable Mount Manager so non-admin mounts are visible to all apps
    ConfigureWinFspMountManager;

    // Write appsettings.jsonc with user-chosen drive letter and capacity
    WriteAppSettings;

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
