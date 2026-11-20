#ifndef AppVersion
  #error AppVersion must be set by eng/Build-Release.ps1
#endif
#ifndef PayloadDirectory
  #error PayloadDirectory must be set by eng/Build-Release.ps1
#endif

[Setup]
AppId={{412DF28B-9607-49BA-827C-71BEC140207A}
AppName=Flurryveil
AppVersion={#AppVersion}
VersionInfoProductName=Flurryveil
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Flurryveil
DefaultGroupName=Flurryveil
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0.19045
WizardStyle=modern
SetupIconFile=..\Snowfall\Assets\Flurryveil.ico
UninstallDisplayIcon={app}\Flurryveil.exe
OutputBaseFilename=Flurryveil-{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
AppMutex=Local\QuietSnow.Desktop.Instance.v2
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Messages]
english.SetupAppRunningError=Exit Flurryveil or Quiet Snow (Тихий снег) from the notification area, then click OK to continue.
english.UninstallAppRunningError=Exit Flurryveil or Quiet Snow (Тихий снег) from the notification area, then click OK to continue.
russian.SetupAppRunningError=Закройте Flurryveil или «Тихий снег» через меню значка рядом с часами. Затем нажмите «OK», чтобы продолжить.
russian.UninstallAppRunningError=Закройте Flurryveil или «Тихий снег» через меню значка рядом с часами. Затем нажмите «OK», чтобы продолжить.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDirectory}\Flurryveil.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDirectory}\licenses\*.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Flurryveil"; Filename: "{app}\Flurryveil.exe"; AppUserModelID: "Flurryveil.Desktop"
Name: "{autodesktop}\Flurryveil"; Filename: "{app}\Flurryveil.exe"; AppUserModelID: "Flurryveil.Desktop"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "QuietSnow"; ValueData: """{app}\Flurryveil.exe"" --background"; Check: HasLegacyStartup

[Run]
Filename: "{app}\Flurryveil.exe"; Description: "{cm:LaunchProgram,Flurryveil}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValue = 'QuietSnow';
  StartupSuffix = '" --background';

function HasLegacyStartup(): Boolean;
var
  Command, ProgramPath: String;
begin
  Result := False;
  if not RegQueryStringValue(HKCU, RunKey, RunValue, Command) then
    Exit;
  if (Length(Command) <= 1 + Length(StartupSuffix)) or (Copy(Command, 1, 1) <> '"') then
    Exit;
  if CompareText(Copy(Command, Length(Command) - Length(StartupSuffix) + 1, Length(StartupSuffix)), StartupSuffix) <> 0 then
    Exit;
  ProgramPath := Copy(Command, 2, Length(Command) - Length(StartupSuffix) - 1);
  Result := (Pos('"', ProgramPath) = 0) and (CompareText(ExtractFileName(ProgramPath), 'Тихий снег.exe') = 0);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command, InstalledCommand: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  InstalledCommand := '"' + ExpandConstant('{app}\Flurryveil.exe') + StartupSuffix;
  if RegQueryStringValue(HKCU, RunKey, RunValue, Command) and (CompareText(Command, InstalledCommand) = 0) then
    RegDeleteValue(HKCU, RunKey, RunValue);
end;
