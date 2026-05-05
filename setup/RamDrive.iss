; RamDrive Inno Setup Script
; Bundles RamDrive AOT exe + WinFsp installer
; Supports both portable (green) and Windows Service modes

#define MyAppName      "RamDrive"
#define MyAppVersion   "0.0.0-dev"
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
; appsettings.jsonc — copied to installer's temp first; WriteAppSettings then
; loads it, patches MountPoint/CapacityMb/InitialDirectories with the user's
; choices, and writes the result to {app} (preserving every other field and
; all the inline JSONC comments).
Source: "{#PublishDir}\appsettings.jsonc";  DestDir: "{tmp}"; Flags: deleteafterinstall; Components: main

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

// Patch one "key": value line in a JSONC text. Searches Lines for the first
// line whose trimmed leading content starts with `"<Key>":`; replaces from
// after the first colon to (but not including) any trailing comma, with
// NewValueLiteral. Returns True if a replacement happened.
//
// Limitations: only patches single-line scalar values. Object/array values
// (like "InitialDirectories": { ... }) need PatchInitialDirectoriesObject.
function PatchScalarValue(var Lines: TArrayOfString; const Key, NewValueLiteral: string): Boolean;
var
  I, ColonPos, CommaPos, KeyPos: Integer;
  Line, Trimmed, Prefix, Suffix: string;
  Pattern: string;
begin
  Result := False;
  Pattern := '"' + Key + '"';
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Lines[I];
    Trimmed := Trim(Line);
    if (Length(Trimmed) >= 2) and (Trimmed[1] = '/') and (Trimmed[2] = '/') then Continue;
    KeyPos := Pos(Pattern, Line);
    if KeyPos = 0 then Continue;
    // Must be followed (allowing whitespace) by ':'
    ColonPos := Pos(':', Copy(Line, KeyPos + Length(Pattern), Length(Line)));
    if ColonPos = 0 then Continue;
    ColonPos := KeyPos + Length(Pattern) - 1 + ColonPos;
    Prefix := Copy(Line, 1, ColonPos);  // up to and including ':'
    Suffix := Copy(Line, ColonPos + 1, Length(Line));
    // Find trailing comma (preserve it) — the rightmost ',' on the line.
    CommaPos := -1;
    if Length(Suffix) > 0 then
    begin
      CommaPos := Length(Suffix);
      while (CommaPos > 0) and (Suffix[CommaPos] <> ',') do
        Dec(CommaPos);
    end;
    if CommaPos > 0 then
      Lines[I] := Prefix + ' ' + NewValueLiteral + Copy(Suffix, CommaPos, Length(Suffix))
    else
      Lines[I] := Prefix + ' ' + NewValueLiteral;
    Result := True;
    Exit;
  end;
end;

// Count occurrences of substring Sub in S. Defined ahead of
// PatchInitialDirectories which uses it.
function CountSubstr(const S, Sub: string): Integer;
var
  P, Count: Integer;
  Rest: string;
begin
  Count := 0;
  Rest := S;
  P := Pos(Sub, Rest);
  while P > 0 do
  begin
    Inc(Count);
    Rest := Copy(Rest, P + Length(Sub), Length(Rest));
    P := Pos(Sub, Rest);
  end;
  Result := Count;
end;

// Replace the InitialDirectories value (which may be `{}` or `{ ... multiline ... }`)
// with the given object literal. Removes any continuation lines until a matching `}`.
function PatchInitialDirectories(var Lines: TArrayOfString; const NewLiteral: string): Boolean;
var
  I, J, KeyPos, BraceDepth, K: Integer;
  Line, Trimmed, Prefix: string;
  HasComma: Boolean;
  TrailingComma: string;
begin
  Result := False;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Lines[I];
    Trimmed := Trim(Line);
    if (Length(Trimmed) >= 2) and (Trimmed[1] = '/') and (Trimmed[2] = '/') then Continue;
    KeyPos := Pos('"InitialDirectories"', Line);
    if KeyPos = 0 then Continue;

    // Determine indentation prefix (everything up to the opening quote).
    Prefix := Copy(Line, 1, KeyPos - 1);

    // Walk forward from this line counting braces until depth returns to 0;
    // that's the line where the value ends.
    HasComma := False;
    BraceDepth := 0;
    J := I;
    while J < GetArrayLength(Lines) do
    begin
      BraceDepth := BraceDepth + CountSubstr(Lines[J], '{');
      BraceDepth := BraceDepth - CountSubstr(Lines[J], '}');
      if BraceDepth <= 0 then
      begin
        if (Length(Trim(Lines[J])) > 0) and (Trim(Lines[J])[Length(Trim(Lines[J]))] = ',') then
          HasComma := True;
        Break;
      end;
      Inc(J);
    end;
    if J >= GetArrayLength(Lines) then Exit;

    // Build replacement single line.
    if HasComma then TrailingComma := ',' else TrailingComma := '';
    Lines[I] := Prefix + '"InitialDirectories": ' + NewLiteral + TrailingComma;

    // Remove the continuation lines (I+1 .. J inclusive).
    if J > I then
    begin
      for K := I + 1 to GetArrayLength(Lines) - 1 - (J - I) do
        Lines[K] := Lines[K + (J - I)];
      SetArrayLength(Lines, GetArrayLength(Lines) - (J - I));
    end;
    Result := True;
    Exit;
  end;
end;

// Reads the literal value (everything between ':' and trailing comma, trimmed)
// of the first non-comment line that contains "<Key>": ... in Lines.
// Returns empty string if not found. Includes surrounding quotes for string
// values, e.g. for `"MountPoint": "Z:\\",` it returns `"Z:\\"`.
function ReadScalarValue(const Lines: TArrayOfString; const Key: string): string;
var
  I, ColonPos, KeyPos, EndPos: Integer;
  Line, Trimmed, Suffix: string;
  Pattern: string;
begin
  Result := '';
  Pattern := '"' + Key + '"';
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Lines[I];
    Trimmed := Trim(Line);
    if (Length(Trimmed) >= 2) and (Trimmed[1] = '/') and (Trimmed[2] = '/') then Continue;
    KeyPos := Pos(Pattern, Line);
    if KeyPos = 0 then Continue;
    ColonPos := Pos(':', Copy(Line, KeyPos + Length(Pattern), Length(Line)));
    if ColonPos = 0 then Continue;
    ColonPos := KeyPos + Length(Pattern) - 1 + ColonPos;
    Suffix := Trim(Copy(Line, ColonPos + 1, Length(Line)));
    // Strip trailing ','
    EndPos := Length(Suffix);
    while (EndPos > 0) and (Suffix[EndPos] = ',') do Dec(EndPos);
    Result := Trim(Copy(Suffix, 1, EndPos));
    Exit;
  end;
end;

// Read the InitialDirectories object literal (multi-line aware) and return it
// as a single-line compacted string, e.g. `{ "Temp": {} }`. Empty string if
// not found. Walks brace depth to find the end.
function ReadInitialDirectories(const Lines: TArrayOfString): string;
var
  I, J, KeyPos, ColonPos, BraceDepth: Integer;
  Line, Trimmed, Buf: string;
begin
  Result := '';
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Lines[I];
    Trimmed := Trim(Line);
    if (Length(Trimmed) >= 2) and (Trimmed[1] = '/') and (Trimmed[2] = '/') then Continue;
    KeyPos := Pos('"InitialDirectories"', Line);
    if KeyPos = 0 then Continue;
    ColonPos := Pos(':', Copy(Line, KeyPos, Length(Line)));
    if ColonPos = 0 then Continue;
    ColonPos := KeyPos + ColonPos - 1;

    // Buf accumulates everything from after the colon, across as many lines
    // as needed until the brace depth returns to 0.
    Buf := Copy(Line, ColonPos + 1, Length(Line));
    BraceDepth := CountSubstr(Buf, '{') - CountSubstr(Buf, '}');
    J := I + 1;
    while (BraceDepth > 0) and (J < GetArrayLength(Lines)) do
    begin
      Buf := Buf + ' ' + Trim(Lines[J]);
      BraceDepth := BraceDepth + CountSubstr(Lines[J], '{') - CountSubstr(Lines[J], '}');
      Inc(J);
    end;

    // Strip a trailing ',' that belongs to the parent object, not the value.
    Buf := Trim(Buf);
    if (Length(Buf) > 0) and (Buf[Length(Buf)] = ',') then
      Buf := Trim(Copy(Buf, 1, Length(Buf) - 1));
    Result := Buf;
    Exit;
  end;
end;

procedure WriteAppSettings;
var
  TemplatePath: String;
  ConfigPath: String;
  Lines: TArrayOfString;
  ExistingLines: TArrayOfString;
  MountValue, CapacityValue, InitDirsLiteral: String;
  ExistingMount, ExistingCapacity, ExistingInitDirs: String;
begin
  TemplatePath := ExpandConstant('{tmp}\appsettings.jsonc');
  ConfigPath := ExpandConstant('{app}\appsettings.jsonc');

  if not FileExists(TemplatePath) then
  begin
    MsgBox('Internal error: appsettings template not found at ' + TemplatePath, mbError, MB_OK);
    Exit;
  end;
  if not LoadStringsFromFile(TemplatePath, Lines) then
  begin
    MsgBox('Internal error: could not read appsettings template.', mbError, MB_OK);
    Exit;
  end;

  // Compute the three user-controlled values.
  //   - Default to whatever the wizard collected (fresh install).
  //   - If an existing config is present (upgrade), preserve its values for
  //     these three fields so the user's mount letter / capacity / initial
  //     directory tree are not silently reset.
  //   - All other fields (PageSize / EnableKernelCache / FileInfoTimeoutMs /
  //     EnableNotifications / Logging / etc.) are taken from the new template,
  //     so upgrades pick up new fields and updated defaults automatically.
  MountValue := '"' + Copy(DriveCombo.Items[DriveCombo.ItemIndex], 1, 1) + ':\\"';
  CapacityValue := Trim(CapacityEdit.Text);
  if CreateTempCheckbox.Checked then
    InitDirsLiteral := '{ "Temp": {} }'
  else
    InitDirsLiteral := '{}';

  if FileExists(ConfigPath) and LoadStringsFromFile(ConfigPath, ExistingLines) then
  begin
    ExistingMount := ReadScalarValue(ExistingLines, 'MountPoint');
    if ExistingMount <> '' then MountValue := ExistingMount;
    ExistingCapacity := ReadScalarValue(ExistingLines, 'CapacityMb');
    if ExistingCapacity <> '' then CapacityValue := ExistingCapacity;
    ExistingInitDirs := ReadInitialDirectories(ExistingLines);
    if ExistingInitDirs <> '' then InitDirsLiteral := ExistingInitDirs;
  end;

  // Patch the three preserved fields into the new template. Every other line —
  // including all JSONC comments and any new fields added in future releases —
  // is preserved verbatim from the published template.
  PatchScalarValue(Lines, 'MountPoint', MountValue);
  PatchScalarValue(Lines, 'CapacityMb', CapacityValue);
  PatchInitialDirectories(Lines, InitDirsLiteral);

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
  TempPath: string;
  NewTempDir: string;
  ResultCode: Integer;
begin
  Result := True;

  // Heads-up before we touch anything: if the user's TEMP is on a RAM disk
  // (their own existing one, or any other WinFsp mount), the installer will
  // self-destruct when sc.exe stop unmounts the drive. We can't reliably
  // detect "is this drive a RAM disk" from inside Inno Setup, so we ask the
  // user — and if they want to relocate, we re-launch with TEMP overridden.
  TempPath := ExpandConstant('{tmp}');
  Choice := TaskDialogMsgBox(
    'TEMP folder check',
    'If your TEMP folder is on a RAM disk, this installer will fail mid-install. ' +
    'Stopping the existing RamDrive service to swap binaries unmounts the RAM ' +
    'disk — which deletes the installer''s own working files (in TEMP), leaving ' +
    'the system in a broken state (service stopped, new binaries not in place).' + #13#10 + #13#10 +
    'Current TEMP location:' + #13#10 +
    '    ' + TempPath,
    mbError,
    MB_YESNOCANCEL, ['Continue installation' + #13#10 + 'TEMP is NOT on a RAM disk — safe to proceed.',
     'Pick a different TEMP folder...' + #13#10 + 'Browse to a folder on a non-RAM-disk drive; the installer will re-launch with TEMP set there.',
     'Cancel' + #13#10 + 'Exit the installer.'],
    0);

  if Choice = IDYES then
  begin
    // Continue as-is.
  end
  else if Choice = IDNO then
  begin
    // Browse for a new TEMP folder.
    NewTempDir := ExpandConstant('{sd}\Windows\Temp');
    if not BrowseForFolder('Choose a TEMP folder NOT on a RAM disk:', NewTempDir, True) then
    begin
      Result := False;
      Exit;
    end;
    if not DirExists(NewTempDir) then
    begin
      MsgBox('Folder does not exist: ' + NewTempDir, mbError, MB_OK);
      Result := False;
      Exit;
    end;
    // Re-launch ourselves with TEMP/TMP overridden via cmd.exe so the
    // child process inherits the new environment. We use cmd /c to set
    // env vars then start the installer detached, and exit ourselves so
    // {tmp} (already created on the RAM disk) gets cleaned up cleanly.
    Exec(
      ExpandConstant('{cmd}'),
      '/c set "TEMP=' + NewTempDir + '" && set "TMP=' + NewTempDir + '" && start "" "' + ExpandConstant('{srcexe}') + '"',
      '', SW_HIDE, ewNoWait, ResultCode);
    Result := False;
    Exit;
  end
  else
  begin
    // IDCANCEL or any other return — abort.
    Result := False;
    Exit;
  end;

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

// When the user reaches the ConfigPage for the first time, pre-fill the three
// user-controlled fields from an existing appsettings.jsonc (upgrade scenario).
// {app} has been resolved by then (set on the SelectDir page that comes first).
var
  ConfigPagePrefilled: Boolean;

procedure CurPageChanged(CurPageID: Integer);
var
  ConfigPath: string;
  ExistingLines: TArrayOfString;
  Mount, Cap, ItemIdx: string;
  LetterChar: string;
  Idx, IntCap: Integer;
begin
  if (CurPageID <> ConfigPage.ID) or ConfigPagePrefilled then Exit;
  ConfigPagePrefilled := True;

  ConfigPath := ExpandConstant('{app}\appsettings.jsonc');
  if not FileExists(ConfigPath) then Exit;
  if not LoadStringsFromFile(ConfigPath, ExistingLines) then Exit;

  // MountPoint: looks like `"Z:\\"` after ReadScalarValue. Pull the first
  // letter out of the quoted value.
  Mount := ReadScalarValue(ExistingLines, 'MountPoint');
  if (Length(Mount) >= 3) and (Mount[1] = '"') then
  begin
    LetterChar := Uppercase(Copy(Mount, 2, 1));
    ItemIdx := LetterChar + ':';
    Idx := DriveCombo.Items.IndexOf(ItemIdx);
    if Idx >= 0 then DriveCombo.ItemIndex := Idx;
  end;

  // CapacityMb: bare integer.
  Cap := ReadScalarValue(ExistingLines, 'CapacityMb');
  IntCap := StrToIntDef(Cap, -1);
  if IntCap >= 16 then
    CapacityEdit.Text := IntToStr(IntCap);

  // CreateTempCheckbox: tick if the existing config lists a "Temp" key under
  // InitialDirectories. We don't try to round-trip the full tree — the
  // checkbox is just a convenience for first-time installs; the existing
  // value will be preserved verbatim by WriteAppSettings via
  // ReadInitialDirectories().
  if Pos('"Temp"', ReadInitialDirectories(ExistingLines)) > 0 then
    CreateTempCheckbox.Checked := True
  else
    CreateTempCheckbox.Checked := False;
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
