; HyperWhisper ARM64 Installer Script
; Requires Inno Setup 6.0+ (https://jrsoftware.org/isinfo.php)
;
; Build command:
;   iscc setup-arm64.iss
;
; Prerequisites:
;   1. Build the app first: dotnet publish -c Release -r win-arm64 --self-contained true
;   2. Ensure Inno Setup is installed and iscc.exe is in PATH

#define MyAppName "HyperWhisper"
#define MyAppVersion "1.7.0"
#define MyAppPublisher "HyperWhisper"
#define MyAppURL "https://www.hyperwhisper.com"
#define MyAppExeName "HyperWhisper.exe"
#define MyAppAssocName "HyperWhisper"
#define MyArchitecture "arm64"

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
; Output installer name: HyperWhisper-1.0.0-arm64-Setup.exe
OutputBaseFilename={#MyAppName}-{#MyAppVersion}-{#MyArchitecture}-Setup
OutputDir=..\windows-installers
Compression=lzma2/ultra64
SolidCompression=yes
; Require Windows 10+ ARM64
MinVersion=10.0
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
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
; Copy all published files from the ARM64 publish folder
Source: "HyperWhisper\bin\Release\net10.0-windows10.0.19041.0\win-arm64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Native ARM64 Parakeet/Qwen3/Nemotron engine + Silero VAD
Source: "HyperWhisper\Resources\parakeet-engine\arm64\*"; DestDir: "{app}\parakeet-engine"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Option to launch app after interactive install (user can uncheck)
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; Auto-relaunch after silent update (runs only when /VERYSILENT is used)
Filename: "{app}\{#MyAppExeName}"; Flags: nowait skipifnotsilent runasoriginaluser

; Include ARM64 dependency installer library
#include "dependencies\CodeDependencies-arm64.iss"

// =============================================================================
// RUNTIME PREREQUISITES
// =============================================================================

function InitializeSetup(): Boolean;
begin
  // App is published self-contained (--self-contained true in build-release.ps1):
  // the .NET 10 Desktop + ASP.NET Core runtimes ship inside {app}, so no system-wide
  // .NET install is required. Do NOT re-add the Dependency_AddDotNet100* installers.
  Result := True;
end;

// =============================================================================
// ARM64 LOCAL ENGINE NOTICE
// =============================================================================

procedure CurPageChanged(CurPageID: Integer);
begin
  // Show ARM64 notice on the Ready page
  if CurPageID = wpReady then
  begin
    WizardForm.ReadyMemo.Lines.Add('');
    WizardForm.ReadyMemo.Lines.Add('ARM64 Note:');
    WizardForm.ReadyMemo.Lines.Add('This ARM64 build includes native ARM64 runtime components.');
    WizardForm.ReadyMemo.Lines.Add('Whisper remains unavailable on ARM64; Parakeet, Qwen3 ASR, Nemotron, and Local LLM use native ARM64 runtimes.');
  end;
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
begin
  if CurUninstallStep = usPostUninstall then begin
    AppDataPath := ExpandConstant('{localappdata}\HyperWhisper');

    // Check if app data folder exists
    if DirExists(AppDataPath) then begin
      if MsgBox('Do you want to remove {#MyAppName} settings and database?' + #13#10 + #13#10 +
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
