#define MyAppName "FIFINE SC3 RGB+"
#ifndef MyAppVersion
  #error MyAppVersion must be supplied by scripts/build-release.ps1
#endif
#ifndef MyAppNumericVersion
  #error MyAppNumericVersion must be supplied by scripts/build-release.ps1
#endif
#ifndef UpdaterOutputFolder
  #error UpdaterOutputFolder must be supplied by scripts/build-release.ps1
#endif
#define MyAppPublisher "FIFINE SC3 RGB+ Project"
#define MyAppExeName "SC3RGBController.exe"
#define ProjectRoot ".."

[Setup]
AppId={{CB07306D-9D66-4FCB-9643-5F0DA2C76491}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\FIFINE SC3 RGB+
DefaultGroupName=FIFINE SC3 RGB+
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#ProjectRoot}\outputs\installer
OutputBaseFilename=FIFINE-SC3-RGB-Plus-{#MyAppVersion}-Setup
SetupIconFile={#ProjectRoot}\Assets\fifine_sc3_rgb_plus.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
InfoBeforeFile=BETA-NOTICE.txt
CloseApplications=yes
AppMutex=FIFINE-SC3-RGB-PLUS
RestartApplications=no
ChangesAssociations=no
VersionInfoVersion={#MyAppNumericVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} installer
VersionInfoCompany={#MyAppPublisher}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Start FIFINE SC3 RGB+ with Windows"; GroupDescription: "Startup:"

[Files]
Source: "{#ProjectRoot}\outputs\FIFINE-SC3-RGB-Plus-v{#MyAppVersion}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"
Source: "{#ProjectRoot}\outputs\{#UpdaterOutputFolder}\*"; DestDir: "{app}\Tools"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\FIFINE SC3 RGB+"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\FIFINE SC3 RGB+"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "FIFINE SC3 RGB+"; ValueData: """{app}\{#MyAppExeName}"" --startup"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: IsAppUpdate
Filename: "{app}\{#MyAppExeName}"; Description: "Launch FIFINE SC3 RGB+"; Flags: nowait postinstall skipifsilent; Check: IsNormalInstall

[Code]
function HasCommandLineSwitch(const SwitchName: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    if CompareText(ParamStr(I), SwitchName) = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function IsAppUpdate(): Boolean;
begin
  Result := HasCommandLineSwitch('/APPUPDATE');
end;

function IsNormalInstall(): Boolean;
begin
  Result := not IsAppUpdate();
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  SettingsDir: String;
  SettingsFile: String;
begin
  if (CurStep = ssPostInstall) and (not WizardIsTaskSelected('startup')) then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'FIFINE SC3 RGB+');
    SettingsDir := ExpandConstant('{localappdata}\SC3RGBController');
    SettingsFile := SettingsDir + '\settings.json';
    if not FileExists(SettingsFile) then
    begin
      ForceDirectories(SettingsDir);
      SaveStringToFile(SettingsFile, '{"StartWithWindows":false}', False);
    end;
  end;
end;
