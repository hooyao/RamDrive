; RamDrive Inno Setup Script
; Bundles RamDrive AOT exe + WinFsp installer
; Supports both portable (green) and Windows Service modes

#define MyAppName      "RamDrive"
#define MyAppVersion   "0.4.0-beta.1"
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
Name: "{group}\Edit Configuration";   Filename: "notepad.exe"; Parameters: """{app}\appsettings.jsonc"""; AfterInstall: SetElevationBit('{group}\Edit Configuration.lnk')
Name: "{group}\Restart Service";      Filename: "cmd.exe"; Parameters: "/c sc.exe stop RamDrive & timeout /t 2 & sc.exe start RamDrive & pause"; Components: service; AfterInstall: SetElevationBit('{group}\Restart Service.lnk')
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Code]

// Windows API for UpDown (spin) control
function CreateWindowEx(dwExStyle: Cardinal; lpClassName, lpWindowName: String;
  dwStyle: Cardinal; X, Y, nWidth, nHeight: Integer; hWndParent: HWND;
  hMenu, hInstance, lpParam: Integer): HWND;
  external 'CreateWindowExW@user32.dll stdcall';
function SendMsg(hWnd: HWND; Msg: Cardinal; wParam, lParam: Integer): Integer;
  external 'SendMessageW@user32.dll stdcall';

const
  UDS_SETBUDDYINT = $0002;
  UDS_ALIGNRIGHT  = $0004;
  UDS_ARROWKEYS   = $0020;
  UDS_NOTHOUSANDS = $0080;
  UDM_SETBUDDY    = $0469;
  UDM_SETRANGE32  = $046F;
  UDM_SETPOS32    = $0471;

procedure SetElevationBit(Filename: string);
var
  Buffer: string;
  Stream: TStream;
begin
  Filename := ExpandConstant(Filename);
  Stream := TFileStream.Create(Filename, fmOpenReadWrite);
  try
    Stream.Seek(21, soFromBeginning);
    SetLength(Buffer, 1);
    Stream.ReadBuffer(Buffer, 1);
    Buffer[1] := Chr(Ord(Buffer[1]) or $20);
    Stream.Seek(-1, soFromCurrent);
    Stream.WriteBuffer(Buffer, 1);
  finally
    Stream.Free;
  end;
end;

var
  ConfigPage: TWizardPage;
  DriveCombo: TNewComboBox;
  CapacityEdit: TNewEdit;
  CreateTempCheckbox: TNewCheckBox;

procedure InitializeWizard;
var
  Lbl: TNewStaticText;
  InfoLbl: TNewStaticText;
  UpDown: HWND;
  I: Integer;
begin
  ConfigPage := CreateCustomPage(wpSelectTasks,
    'RamDrive Configuration',
    'Configure the RAM disk settings.');

  // --- Drive letter dropdown ---
  Lbl := TNewStaticText.Create(ConfigPage);
  Lbl.Parent := ConfigPage.Surface;
  Lbl.Caption := 'Drive letter:';
  Lbl.Top := 0;
  Lbl.Left := 0;

  DriveCombo := TNewComboBox.Create(ConfigPage);
  DriveCombo.Parent := ConfigPage.Surface;
  DriveCombo.Style := csDropDownList;
  DriveCombo.Top := Lbl.Top + Lbl.Height + 4;
  DriveCombo.Left := 0;
  DriveCombo.Width := 80;
  for I := Ord('D') to Ord('Z') do
    DriveCombo.Items.Add(Chr(I) + ':');
  DriveCombo.ItemIndex := DriveCombo.Items.IndexOf('R:');

  // --- Capacity spin edit ---
  Lbl := TNewStaticText.Create(ConfigPage);
  Lbl.Parent := ConfigPage.Surface;
  Lbl.Caption := 'Capacity (MB):';
  Lbl.Top := DriveCombo.Top + DriveCombo.Height + 16;
  Lbl.Left := 0;

  CapacityEdit := TNewEdit.Create(ConfigPage);
  CapacityEdit.Parent := ConfigPage.Surface;
  CapacityEdit.Top := Lbl.Top + Lbl.Height + 4;
  CapacityEdit.Left := 0;
  CapacityEdit.Width := 120;
  CapacityEdit.Text := '2048';

  // Attach a native Windows UpDown control to the edit box
  UpDown := CreateWindowEx(0, 'msctls_updown32', '',
    $40000000 or $10000000 or UDS_SETBUDDYINT or UDS_ALIGNRIGHT or UDS_ARROWKEYS or UDS_NOTHOUSANDS,
    0, 0, 0, 0, ConfigPage.Surface.Handle, 0, 0, 0);
  SendMsg(UpDown, UDM_SETBUDDY, CapacityEdit.Handle, 0);
  SendMsg(UpDown, UDM_SETRANGE32, 16, 131072);  // 16 MB .. 128 GB
  SendMsg(UpDown, UDM_SETPOS32, 0, 2048);

  // --- Create Temp directory checkbox ---
  CreateTempCheckbox := TNewCheckBox.Create(ConfigPage);
  CreateTempCheckbox.Parent := ConfigPage.Surface;
  CreateTempCheckbox.Top := CapacityEdit.Top + CapacityEdit.Height + 20;
  CreateTempCheckbox.Left := 0;
  CreateTempCheckbox.Width := ConfigPage.SurfaceWidth;
  CreateTempCheckbox.Height := ScaleY(20);
  CreateTempCheckbox.Caption := 'Create a Temp directory on the RAM disk at startup';
  CreateTempCheckbox.Checked := False;

  // --- Info label ---
  InfoLbl := TNewStaticText.Create(ConfigPage);
  InfoLbl.Parent := ConfigPage.Surface;
  InfoLbl.WordWrap := True;
  InfoLbl.Top := CreateTempCheckbox.Top + CreateTempCheckbox.Height + 20;
  InfoLbl.Left := 0;
  InfoLbl.Width := ConfigPage.SurfaceWidth;
  InfoLbl.Caption :=
    'To add more initial directories or change settings after installation:' + #13#10 +
    '1. Edit appsettings.jsonc (Start Menu > RamDrive > Edit Configuration)' + #13#10 +
    '2. Restart the service (Start Menu > RamDrive > Restart Service)';
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
  CapVal: Integer;
begin
  Result := True;
  if CurPageID = ConfigPage.ID then
  begin
    // Drive letter is from dropdown, always valid
    if DriveCombo.ItemIndex < 0 then
    begin
      MsgBox('Please select a drive letter.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    // Validate capacity (UpDown enforces range, but user can type directly)
    CapVal := StrToIntDef(Trim(CapacityEdit.Text), 0);
    if CapVal < 16 then
    begin
      MsgBox('Capacity must be at least 16 MB.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;

procedure KillProcess(ExeName: String);
var
  ResultCode: Integer;
begin
  Exec('powershell.exe', '-NoProfile -Command "Stop-Process -Name ''' + ExeName + ''' -Force -ErrorAction SilentlyContinue"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure StopAndDeleteService;
var
  ScExe: String;
  ResultCode: Integer;
  Retries: Integer;
begin
  ScExe := ExpandConstant('{sysnative}\sc.exe');
  if not FileExists(ScExe) then
    ScExe := ExpandConstant('{sys}\sc.exe');

  // Stop the service
  Exec(ScExe, 'stop RamDrive', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Kill the process to speed up stop (WinFsp unmount can be slow)
  KillProcess('RamDrive');

  // Delete the service
  Exec(ScExe, 'delete RamDrive', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Wait until SCM fully removes it (sc.exe query returns error 1060)
  Retries := 0;
  while Retries < 20 do
  begin
    Exec(ScExe, 'query RamDrive', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if ResultCode <> 0 then
      Break;  // Service is gone
    Sleep(500);
    Retries := Retries + 1;
  end;
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
  I: Integer;
begin
  Letter := Copy(DriveCombo.Items[DriveCombo.ItemIndex], 1, 1);
  Cap := Trim(CapacityEdit.Text);
  ConfigPath := ExpandConstant('{app}\appsettings.jsonc');

  // Common prefix (lines 0..14) + variable suffix
  if CreateTempCheckbox.Checked then
    SetArrayLength(Lines, 21)
  else
    SetArrayLength(Lines, 19);

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
  Lines[14] := '    "EnableKernelCache": true,';

  if CreateTempCheckbox.Checked then
  begin
    Lines[15] := '    "InitialDirectories": {';
    Lines[16] := '      "Temp": {}';
    Lines[17] := '    }';
    I := 18;
  end
  else
  begin
    Lines[15] := '    "InitialDirectories": {}';
    I := 16;
  end;

  Lines[I]     := '  }';
  Lines[I + 1] := '}';
  Lines[I + 2] := '';

  SaveStringsToUTF8File(ConfigPath, Lines, False);
end;

procedure CreateService;
var
  ExePath: String;
  ScExe: String;
  ResultCode: Integer;
begin
  ExePath := ExpandConstant('{app}\{#MyAppExeName}');
  // Use native sc.exe to avoid WOW64 redirection in 32-bit installer
  ScExe := ExpandConstant('{sysnative}\sc.exe');
  if not FileExists(ScExe) then
    ScExe := ExpandConstant('{sys}\sc.exe');

  // Create service via SCM (immediately startable, no reboot needed)
  Exec(ScExe,
       'create RamDrive binPath= "' + ExePath + '" start= auto DisplayName= "RamDrive RAM Disk"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode <> 0 then
  begin
    MsgBox('Failed to register Windows Service (sc.exe exit code: ' + IntToStr(ResultCode) + ').' + #13#10 +
           'You can register manually: sc.exe create RamDrive binPath= "' + ExePath + '" start= auto',
           mbError, MB_OK);
    Exit;
  end;

  // Description
  Exec(ScExe, 'description RamDrive "High-performance RAM disk using WinFsp."',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Failure recovery: restart after 5s, 10s, 30s
  Exec(ScExe, 'failure RamDrive reset= 60 actions= restart/5000/restart/10000/restart/30000',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Start in early service group (before most user-mode services)
  RegWriteStringValue(HKLM,
       'SYSTEM\CurrentControlSet\Services\RamDrive',
       'Group', 'FSFilter Activity Monitor');
end;

procedure StartService;
var
  ScExe: String;
  ResultCode: Integer;
begin
  ScExe := ExpandConstant('{sysnative}\sc.exe');
  if not FileExists(ScExe) then
    ScExe := ExpandConstant('{sys}\sc.exe');
  Exec(ScExe, 'start RamDrive', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function IsRamDriveRunning: Boolean;
var
  ResultCode: Integer;
begin
  Exec('powershell.exe',
       '-NoProfile -Command "if (Get-Process -Name RamDrive -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := (ResultCode = 0);
end;

function InitializeSetup: Boolean;
var
  Choice: Integer;
begin
  Result := True;

  if IsRamDriveRunning then
  begin
    Choice := MsgBox('RamDrive is currently running.' + #13#10 + #13#10 +
                      'The installer needs to stop it before proceeding. ' +
                      'Any data on the RAM disk will be lost.' + #13#10 + #13#10 +
                      'Stop RamDrive and continue installation?',
                      mbConfirmation, MB_YESNO);
    if Choice = IDYES then
    begin
      if IsServiceInstalled then
        StopAndDeleteService;
      KillProcess('RamDrive');
      Sleep(2000);
    end
    else
    begin
      Result := False;
    end;
  end;
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

    // Register and start Windows Service if selected
    if WizardIsComponentSelected('service') then
    begin
      CreateService;
      StartService;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    // Stop service first, then kill any remaining process
    if IsServiceInstalled then
      StopAndDeleteService;
    KillProcess('RamDrive');
    Sleep(1000);
  end;
end;

[Run]
; Launch the app directly (green mode only, service mode started in CurStepChanged)
Filename: "{app}\{#MyAppExeName}"; \
  Description: "Launch {#MyAppName} now"; \
  Flags: nowait postinstall skipifsilent; Components: not service
