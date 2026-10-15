; Compile with Inno Setup 7.1 or later. SourceDir must contain only release files.
; TestRoot and TestId are compile-time isolation hooks, never runtime switches.
#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef AppVersion
  #define AppVersion "1.19.0"
#endif
#ifdef TestRoot
  #ifndef TestId
    #error TestRoot requires TestId
  #endif
  #define InstallPath TestRoot + "\data\App"
  #define StartMenuPath TestRoot + "\shortcuts\start-menu"
  #define DesktopPath TestRoot + "\shortcuts\desktop"
  #define StartupSubkey "Software\UsageWidget\Tests\Setup\" + TestId + "\Run"
  #define SetupAppId "Nephylo.SetupTest." + TestId
#else
  #ifdef TestId
    #error TestId requires TestRoot
  #endif
  #define InstallPath "{localappdata}\UsageWidget\App"
  #define StartMenuPath "{userprograms}"
  #define DesktopPath "{userdesktop}"
  #define StartupSubkey "Software\Microsoft\Windows\CurrentVersion\Run"
  #define SetupAppId "Nephylo.UsageWidget"
#endif

[Setup]
AppId={#SetupAppId}
AppName=Nephylo
AppVersion={#AppVersion}
AppVerName=Nephylo {#AppVersion}
AppPublisher=Nephylo
AppPublisherURL=https://nephylo.com/
AppSupportURL=https://nephylo.com/#support
AppUpdatesURL=https://nephylo.com/#download
DefaultDirName={#InstallPath}
UsePreviousAppDir=no
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
MinVersion=10.0.18362
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\UsageWidget.exe
UninstallDisplayName=Nephylo
UninstallFilesDir={app}
UsePreviousTasks=no
UsePreviousLanguage=no
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=auto
WizardStyle=modern dynamic windows11
SetupIconFile={#SourceDir}\widget.ico
OutputDir={#OutputDir}
OutputBaseFilename=Nephylo-Setup
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
AllowCancelDuringInstall=no
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName=Nephylo
VersionInfoDescription=Nephylo Setup
#ifdef TestRoot
SetupMutex=Local\Nephylo.SetupTest.{#TestId}
#else
AppMutex=Local\UsageWidget.SingleInstance.v1
SetupMutex=Local\UsageWidget.Install.v1
#endif

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"

[Messages]
en.WindowsVersionNotSupported=Nephylo requires Windows 10 version 1903 (build 18362) or later on x64, or Windows 11 on x64/ARM64.%n%n32-bit Windows and Windows 10 ARM64 are not supported.
ko.WindowsVersionNotSupported=Nephylo는 Windows 10 1903(빌드 18362) 이상 x64 또는 Windows 11 x64/ARM64에서 설치할 수 있습니다.%n%n32비트 Windows와 Windows 10 ARM64는 지원하지 않습니다.

[CustomMessages]
en.Options=Preferences
ko.Options=설치 옵션
en.Startup=Start Nephylo when I sign in to Windows
ko.Startup=Windows 로그인 시 Nephylo 자동 실행
en.Desktop=Create a desktop shortcut
ko.Desktop=바탕 화면에 바로가기 만들기
en.Launch=Open Nephylo
ko.Launch=Nephylo 실행
en.InstallHint=Install Nephylo for this Windows account. Existing connections, settings and history will be kept.
ko.InstallHint=현재 Windows 계정에 설치합니다. 기존 연결 정보, 설정과 사용 이력은 유지됩니다.
en.FixedPath=Nephylo uses a fixed folder for this Windows account. Run Setup without a custom /DIR option.
ko.FixedPath=Nephylo는 현재 Windows 계정의 고정 폴더에 설치됩니다. /DIR 옵션 없이 설치 파일을 실행해 주세요.
en.PathLink=The installation or shortcut path contains a folder link. Use a regular local folder and try again.
ko.PathLink=설치 또는 바로가기 경로에 폴더 링크가 있습니다. 일반 로컬 폴더를 사용한 뒤 다시 실행해 주세요.
en.NewerVersion=A newer version of Nephylo is already installed. Download the latest setup file.
ko.NewerVersion=더 최신 Nephylo가 이미 설치되어 있습니다. 최신 설치 파일을 사용해 주세요.
en.InvalidExisting=The existing application could not be verified. Close Nephylo and check the installation folder before trying again.
ko.InvalidExisting=기존 실행 파일을 확인하지 못했습니다. Nephylo를 종료하고 설치 폴더를 확인한 뒤 다시 실행해 주세요.
en.ShortcutCollision=A different or customized Nephylo shortcut already exists. Rename it and run Setup again.
ko.ShortcutCollision=다른 앱 또는 사용자 지정 Nephylo 바로가기가 있습니다. 해당 바로가기 이름을 변경한 뒤 다시 설치해 주세요.
en.StartupChanged=The Windows startup entry changed during installation. Run Setup again to keep the current preference.
ko.StartupChanged=설치 중 Windows 자동 실행 항목이 변경되었습니다. 현재 설정을 반영하도록 설치 파일을 다시 실행해 주세요.
en.Running=Close Nephylo from the taskbar tray, then try again. Your settings and history will be kept.
ko.Running=작업 표시줄 트레이에서 Nephylo를 종료한 뒤 다시 시도해 주세요. 설정과 이력은 유지됩니다.
en.KeepStartup=An existing custom startup entry will be kept. You can manage startup in Nephylo settings after installation.
ko.KeepStartup=기존 사용자 지정 자동 실행 항목을 유지합니다. 설치 후 Nephylo 설정에서 자동 실행을 관리할 수 있습니다.

[Tasks]
Name: "startup"; Description: "{cm:Startup}"; GroupDescription: "{cm:Options}"; Flags: unchecked; Check: CanManageStartup
Name: "desktopicon"; Description: "{cm:Desktop}"; GroupDescription: "{cm:Options}"; Flags: unchecked

[Files]
; No recursive wildcard and no user data. Unknown destination files are untouched.
Source: "{#SourceDir}\UsageWidget.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\UsageWidget.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\widget.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\widget.png"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Uninstall removes these in code only while their exact target/arguments are ours.
Name: "{#StartMenuPath}\Nephylo"; Filename: "{app}\UsageWidget.exe"; WorkingDir: "{app}"; Flags: uninsneveruninstall
Name: "{#DesktopPath}\Nephylo"; Filename: "{app}\UsageWidget.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Flags: uninsneveruninstall

[Registry]
; No uninsdeletevalue: uninstall re-checks ownership so a later changed value survives.
Root: HKCU; Subkey: "{#StartupSubkey}"; ValueType: string; ValueName: "UsageWidget"; ValueData: """{app}\UsageWidget.exe"""; Tasks: startup; Check: CanManageStartup
Root: HKCU; Subkey: "{#StartupSubkey}"; ValueName: "UsageWidget"; Flags: deletevalue; Check: RemoveStartup

#ifndef TestRoot
[Run]
Filename: "{app}\UsageWidget.exe"; Description: "{cm:Launch}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
#endif

[Code]
const
  StartupKey = '{#StartupSubkey}';
  StartupValue = 'UsageWidget';
  ReparsePoint = $400;
var
  InitialStartup: String;
  InitialStartupExists: Boolean;
  TasksSeeded: Boolean;

function GetFileAttributesW(Name: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function FixedAppPath: String;
begin
  Result := ExpandConstant('{#InstallPath}');
end;

function ExePath: String;
begin
  Result := FixedAppPath + '\UsageWidget.exe';
end;

function StartLink: String;
begin
  Result := ExpandConstant('{#StartMenuPath}\Nephylo.lnk');
end;

function DesktopLink: String;
begin
  Result := ExpandConstant('{#DesktopPath}\Nephylo.lnk');
end;

function HasReparsePath(Path: String): Boolean;
var
  Current, Parent: String;
  Attributes: Cardinal;
begin
  Result := False;
  Current := RemoveBackslashUnlessRoot(ExpandFileName(Path));
  while Current <> '' do begin
    Attributes := GetFileAttributesW(Current);
    if (Attributes <> $FFFFFFFF) and ((Attributes and ReparsePoint) <> 0) then begin
      Result := True;
      Exit;
    end;
    Parent := RemoveBackslashUnlessRoot(ExtractFileDir(Current));
    if CompareText(Parent, Current) = 0 then Exit;
    Current := Parent;
  end;
end;

function IsOwnedShortcut(Path: String): Boolean;
var
  Shell, Link: Variant;
  Target, Arguments: String;
begin
  Result := False;
  if not FileExists(Path) or HasReparsePath(Path) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Link := Shell.CreateShortcut(Path);
    Target := Link.TargetPath;
    Arguments := Link.Arguments;
    Result := (CompareText(ExpandFileName(Target), ExePath) = 0) and (Trim(Arguments) = '');
  except
    Result := False;
  end;
end;

function IsWidgetStartup(Command: String): Boolean;
var
  Path: String;
begin
  Result := False;
  if Length(Command) < 4 then Exit;
  if (Command[1] <> '"') or (Command[Length(Command)] <> '"') then Exit;
  Path := Copy(Command, 2, Length(Command) - 2);
  if Pos('"', Path) <> 0 then Exit;
  if (Length(Path) < 3) or (Path[2] <> ':') or (Path[3] <> '\') then Exit;
  Result := CompareText(ExtractFileName(Path), 'UsageWidget.exe') = 0;
end;

function CanManageStartup: Boolean;
var
  Current: String;
begin
  Result := not RegValueExists(HKCU, StartupKey, StartupValue);
  if not Result then
    Result := RegQueryStringValue(HKCU, StartupKey, StartupValue, Current) and IsWidgetStartup(Current);
end;

function RemoveStartup: Boolean;
var
  Current: String;
begin
  Result := not WizardIsTaskSelected('startup') and
    RegQueryStringValue(HKCU, StartupKey, StartupValue, Current) and IsWidgetStartup(Current);
end;

function HasExplicitTasks: Boolean;
var
  I: Integer;
  P: String;
begin
  Result := False;
  for I := 1 to ParamCount do begin
    P := Uppercase(ParamStr(I));
    if Pos('/TASKS=', P) = 1 then begin
      Result := True;
      Exit;
    end;
    if Pos('/MERGETASKS=', P) = 1 then begin
      P := ',' + Copy(P, 13, Length(P)) + ',';
      if (Pos(',STARTUP,', P) <> 0) or (Pos(',!STARTUP,', P) <> 0) then begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

procedure InitializeWizard;
begin
  InitialStartupExists := RegValueExists(HKCU, StartupKey, StartupValue);
  RegQueryStringValue(HKCU, StartupKey, StartupValue, InitialStartup);
  WizardForm.TasksList.MinItemHeight := ScaleY(30);
  WizardForm.SelectTasksLabel.Caption := CustomMessage('InstallHint');
  if not CanManageStartup then
    WizardForm.SelectTasksLabel.Caption := CustomMessage('InstallHint') + #13#10#13#10 + CustomMessage('KeepStartup');
end;

procedure SeedTasks;
begin
  if TasksSeeded then Exit;
  TasksSeeded := True;
  if not HasExplicitTasks and IsWidgetStartup(InitialStartup) then WizardSelectTasks('startup');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectTasks then SeedTasks;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  { Silent installations also simulate this event. Never reset a user's later choice. }
  if CurPageID = wpSelectTasks then SeedTasks;
  Result := True;
end;

function CheckInstallPaths: String;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := '';
  if CompareText(RemoveBackslashUnlessRoot(ExpandFileName(WizardDirValue)),
    RemoveBackslashUnlessRoot(ExpandFileName(FixedAppPath))) <> 0 then begin
    Result := CustomMessage('FixedPath'); Exit;
  end;
  Names := ['UsageWidget.exe', 'UsageWidget.exe.config', 'widget.ico', 'widget.png', 'README.txt', 'THIRD-PARTY-NOTICES.txt', 'unins000.exe', 'unins000.dat'];
  if HasReparsePath(FixedAppPath) or HasReparsePath(StartLink) or HasReparsePath(DesktopLink) then begin
    Result := CustomMessage('PathLink'); Exit;
  end;
  for I := 0 to GetArrayLength(Names) - 1 do
    if HasReparsePath(FixedAppPath + '\' + Names[I]) then begin
      Result := CustomMessage('PathLink'); Exit;
    end;
  if FileExists(StartLink) and not IsOwnedShortcut(StartLink) then begin
    Result := CustomMessage('ShortcutCollision'); Exit;
  end;
  if WizardIsTaskSelected('desktopicon') and FileExists(DesktopLink) and not IsOwnedShortcut(DesktopLink) then
    Result := CustomMessage('ShortcutCollision');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExistingVersion, IncomingVersion: Int64;
  Current: String;
  CurrentExists: Boolean;
begin
  Result := CheckInstallPaths;
  if Result <> '' then Exit;
  if FileExists(ExePath) then begin
    if not GetPackedVersion(ExePath, ExistingVersion) then begin
      Result := CustomMessage('InvalidExisting'); Exit;
    end;
    if not StrToVersion('{#AppVersion}.0', IncomingVersion) then begin
      Result := CustomMessage('InvalidExisting'); Exit;
    end;
    if ComparePackedVersion(ExistingVersion, IncomingVersion) > 0 then begin
      Result := CustomMessage('NewerVersion'); Exit;
    end;
  end;
  CurrentExists := RegValueExists(HKCU, StartupKey, StartupValue);
  RegQueryStringValue(HKCU, StartupKey, StartupValue, Current);
  if (CurrentExists <> InitialStartupExists) or (Current <> InitialStartup) then begin
    Result := CustomMessage('StartupChanged'); Exit;
  end;
#ifndef TestRoot
  if CheckForMutexes('Local\UsageWidget.SingleInstance.v1') then
    Result := CustomMessage('Running');
#endif
end;

procedure DeleteOwnedShortcut(Path: String);
begin
  if IsOwnedShortcut(Path) then
    if not DeleteFile(Path) then Log('An owned shortcut could not be removed: ' + Path);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssDone then begin
    DeleteOwnedShortcut(ExpandConstant('{#StartMenuPath}\Milos.lnk'));
    DeleteOwnedShortcut(ExpandConstant('{#StartMenuPath}\Usage Widget.lnk'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Current: String;
begin
  if CurUninstallStep = usUninstall then begin
    DeleteOwnedShortcut(StartLink);
    DeleteOwnedShortcut(DesktopLink);
    if RegQueryStringValue(HKCU, StartupKey, StartupValue, Current) and
      (CompareText(Current, '"' + ExePath + '"') = 0) then
      RegDeleteValue(HKCU, StartupKey, StartupValue);
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := not HasReparsePath(FixedAppPath);
  if not Result then
    SuppressibleMsgBox(CustomMessage('PathLink'), mbCriticalError, MB_OK, IDOK);
end;
