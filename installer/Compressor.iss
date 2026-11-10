; Compressor installer (Inno Setup 6). Built by installer\build.ps1, which publishes both exes into artifacts\publish
; and passes the version and publisher from Directory.Build.props:
;   ISCC.exe /DAppVersion=0.1.0 /DAppPublisher="Mustafa Can Yücel" installer\Compressor.iss
;
; Installs per user, without admin rights, into %LocalAppData%\Programs\Compressor: an update (Help > Check for
; updates) can then run the next installer silently, with no elevation prompt. See app-update-client's README for
; what an update needs from the installer: per user, CloseApplications, and the [Run] entry that reopens the app.

#ifndef AppVersion
  #error Pass the version: /DAppVersion=x.y.z (build.ps1 does this).
#endif
#ifndef AppPublisher
  #define AppPublisher "Mustafa Can Yücel"
#endif

#define AppName "Compressor"
#define AppExe "CompressorGui.exe"
#define CliExe "compressor.exe"
#define Publish "..\artifacts\publish"

[Setup]
; Never change the AppId: Windows and later installers recognise the installed app by it.
AppId={{529ACCF1-E967-4F1E-B591-58C7A65186CB}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/mcanyucel/compressor
VersionInfoVersion={#AppVersion}

PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
; The folder is fixed (the [InstallDelete] below clears it), so there is no folder page.
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; .NET 10 needs Windows 10 or later.
MinVersion=10.0

OutputDir=..\artifacts
OutputBaseFilename=Compressor-{#AppVersion}-setup
SetupIconFile=..\Ui\Assets\compressor.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

; An update closes the running app (Restart Manager); [Run] starts it again.
CloseApplications=yes
RestartApplications=no
; The PATH, when that task is ticked.
ChangesEnvironment=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "addtopath"; Description: "Add the command-line tool ({#CliExe}) to the PATH"; GroupDescription: "Command line:"; Flags: unchecked

[InstallDelete]
; Start each install from an empty app folder, so files dropped by a newer version don't linger. The folder holds
; nothing but the app; the user's settings live in %AppData%\Compressor.
Type: filesandordirs; Name: "{app}\*"

[Files]
; The app, both exes, and 7zip\ (7za.exe, its License.txt, VERSION and SHA256SUMS).
Source: "{#Publish}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; App Paths (per user: HKA is HKCU for a per-user install): Windows finds both exes by name without a PATH entry.
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExe}"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExe}"; ValueType: string; ValueName: "Path"; ValueData: "{app}"
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#CliExe}"; ValueType: string; ValueName: ""; ValueData: "{app}\{#CliExe}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#CliExe}"; ValueType: string; ValueName: "Path"; ValueData: "{app}"

[Run]
; A checkbox on the last page of a normal install; in a silent install (an update) it always runs, so the app comes
; back after updating.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall

[Code]
// The per-user PATH (HKCU\Environment), for the "addtopath" task: the app folder is added when the task is ticked,
// removed when it is not (a reinstall without it) and on uninstall. Other entries are left as they are.
const
  EnvironmentKey = 'Environment';

function UserPath(var Paths: string): Boolean;
begin
  Result := RegQueryStringValue(HKEY_CURRENT_USER, EnvironmentKey, 'Path', Paths);
  if not Result then Paths := '';
end;

// The position of Dir in Paths (1-based, ignoring case and a trailing backslash), or 0.
function FindInPath(Paths, Dir: string): Integer;
begin
  Result := Pos(';' + Uppercase(RemoveBackslashUnlessRoot(Dir)) + ';', ';' + Uppercase(Paths) + ';');
end;

// Keeps the PATH's own style, so that RemoveFromPath gives back exactly what was there: "a;b" becomes "a;b;Dir",
// and "a;b;" (a trailing separator) becomes "a;b;Dir;".
procedure AddToPath(Dir: string);
var
  Paths: string;
begin
  UserPath(Paths);
  if FindInPath(Paths, Dir) > 0 then exit;
  Dir := RemoveBackslashUnlessRoot(Dir);
  if Paths = '' then
    Paths := Dir
  else if Paths[Length(Paths)] = ';' then
    Paths := Paths + Dir + ';'
  else
    Paths := Paths + ';' + Dir;
  RegWriteExpandStringValue(HKEY_CURRENT_USER, EnvironmentKey, 'Path', Paths);
end;

procedure RemoveFromPath(Dir: string);
var
  Paths: string;
  At, Count: Integer;
begin
  if not UserPath(Paths) then exit;
  At := FindInPath(Paths, Dir);
  if At = 0 then exit;
  // Dir is Paths[At .. At + Count - 1]; take one separator with it, the following one if there is one.
  Count := Length(RemoveBackslashUnlessRoot(Dir));
  if At + Count <= Length(Paths) then
    Delete(Paths, At, Count + 1)
  else if At > 1 then
    Delete(Paths, At - 1, Count + 1)
  else
    Delete(Paths, At, Count);
  RegWriteExpandStringValue(HKEY_CURRENT_USER, EnvironmentKey, 'Path', Paths);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('addtopath') then
      AddToPath(ExpandConstant('{app}'))
    else
      RemoveFromPath(ExpandConstant('{app}'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then RemoveFromPath(ExpandConstant('{app}'));
end;
