; HyperWhisper x64 Installer Script
; Requires Inno Setup 6.0+ (https://jrsoftware.org/isinfo.php)
;
; Build command:
;   iscc setup-x64.iss
;
; Prerequisites:
;   1. Build the app first: dotnet publish -c Release -r win-x64 --self-contained true
;   2. Ensure Inno Setup is installed and iscc.exe is in PATH

#define MyAppName "HyperWhisper"
#define MyAppVersion "1.7.0"
#define MyAppPublisher "HyperWhisper"
#define MyAppURL "https://www.hyperwhisper.com"
#define MyAppExeName "HyperWhisper.exe"
#define MyAppAssocName "HyperWhisper"
#define MyArchitecture "x64"

[Setup]
; Unique AppId - DO NOT change after first release (used for upgrades)
AppId={{8F4E9A2B-3C5D-4E6F-A1B2-C3D4E5F6A7B8}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
; Output installer name: HyperWhisper-1.0.0-x64-Setup.exe
OutputBaseFilename={#MyAppName}-{#MyAppVersion}-{#MyArchitecture}-Setup
OutputDir=..\windows-installers
Compression=lzma2/ultra64
SolidCompression=yes
; Require Windows 10+ x64
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64
; Modern Windows 11 style
WizardStyle=modern
; Uninstall settings
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
; Privileges - per-user install by default, admin for Program Files
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Wipe rebuilt native subtrees before copying the new payload. Inno Setup
; overwrites files but never removes ones that are gone from the new build, so
; a renamed/removed native DLL from a previous version would linger in {app}
; and could be loaded ahead of the matching new DLL (native ABI skew).
; No user data lives in {app} (it lives in {localappdata}\HyperWhisper).
Type: filesandordirs; Name: "{app}\runtimes"
Type: filesandordirs; Name: "{app}\parakeet-engine"

[Files]
; Copy all published files from the x64 publish folder
Source: "HyperWhisper\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Parakeet engine (sherpa-onnx with DirectML) + Silero VAD
Source: "HyperWhisper\Resources\parakeet-engine\x64\*"; DestDir: "{app}\parakeet-engine"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Option to launch app after interactive install (user can uncheck)
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; Auto-relaunch after silent update (runs only when /VERYSILENT is used)
Filename: "{app}\{#MyAppExeName}"; Flags: nowait skipifnotsilent runasoriginaluser

; Include dependency installer library
#include "dependencies\CodeDependencies.iss"

// =============================================================================
// RUNTIME PREREQUISITES
// =============================================================================

function InitializeSetup(): Boolean;
begin
  // App is published self-contained (--self-contained true in build-release.ps1):
  // the .NET 10 Desktop + ASP.NET Core runtimes ship inside {app}, so no system-wide
  // .NET install is required. Do NOT re-add the Dependency_AddDotNet100* installers.
  // Visual C++ 2015-2022 Redistributable — needed by the /MD-linked
  // parakeet-engine.exe (Parakeet local transcription). Without it, fresh
  // Windows boxes hit a silent daemon load failure. Skipped if already installed.
  Dependency_AddVC2015To2022;
  Result := True;
end;

// =============================================================================
// UNINSTALL CLEANUP - PROMPT USER TO REMOVE DATA (KEEP RECORDINGS)
// =============================================================================

procedure DeleteFolder(const FolderPath: String);
var
  FindRec: TFindRec;
  FilePath: String;
begin
  if FindFirst(FolderPath + '\*', FindRec) then begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then begin
          FilePath := FolderPath + '\' + FindRec.Name;
          if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
            DeleteFolder(FilePath)
          else
            DeleteFile(FilePath);
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
  RemoveDir(FolderPath);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDataPath: String;
  ModelsSize: String;
begin
  if CurUninstallStep = usPostUninstall then begin
    AppDataPath := ExpandConstant('{localappdata}\HyperWhisper');

    // Check if app data folder exists
    if DirExists(AppDataPath) then begin
      // Check models folder size for user info
      if DirExists(AppDataPath + '\Models') then
        ModelsSize := ' (including Whisper and Parakeet models which may be several GB)'
      else
        ModelsSize := '';

      if MsgBox('Do you want to remove {#MyAppName} settings, database, and downloaded models?' + ModelsSize + #13#10 + #13#10 +
                'Your audio recordings will be kept.' + #13#10 + #13#10 +
                'Click Yes to remove all app data,' + #13#10 +
                'Click No to keep your data for future reinstallation.',
                mbConfirmation, MB_YESNO) = IDYES then begin

        // Delete specific files
        DeleteFile(AppDataPath + '\hyperwhisper.db');
        DeleteFile(AppDataPath + '\hyperwhisper.db-shm');
        DeleteFile(AppDataPath + '\hyperwhisper.db-wal');
        DeleteFile(AppDataPath + '\settings.json');
        DeleteFile(AppDataPath + '\license.json');
        DeleteFile(AppDataPath + '\device_id');
        DeleteFile(AppDataPath + '\usage.json');
        DeleteFile(AppDataPath + '\vocabulary.json');
        DeleteFile(AppDataPath + '\history.json');

        // Delete folders (except Audio - keep recordings)
        if DirExists(AppDataPath + '\Logs') then
          DeleteFolder(AppDataPath + '\Logs');
        if DirExists(AppDataPath + '\Models') then
          DeleteFolder(AppDataPath + '\Models');

        // Try to remove parent folder if empty (won't delete if Audio exists)
        RemoveDir(AppDataPath);
      end;
    end;
  end;
end;
