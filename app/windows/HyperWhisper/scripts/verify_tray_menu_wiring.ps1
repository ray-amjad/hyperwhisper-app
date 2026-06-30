param()

$ErrorActionPreference = "Stop"

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $ProjectRoot "..\..\..")
$MainWindowPath = Join-Path $ProjectRoot "Views\Windows\MainWindow.xaml.cs"
$MacMainAppViewPath = Join-Path $RepoRoot "app\macos\hyperwhisper\Views\MainAppView.swift"

function Assert-True {
    param(
        [bool] $Condition,
        [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-MethodBody {
    param(
        [string] $Source,
        [string] $SignaturePattern
    )

    $match = [regex]::Match($Source, $SignaturePattern)
    Assert-True $match.Success "Could not find method matching: $SignaturePattern"

    $braceStart = $Source.IndexOf("{", $match.Index)
    Assert-True ($braceStart -ge 0) "Could not find method opening brace: $SignaturePattern"

    $depth = 0
    for ($i = $braceStart; $i -lt $Source.Length; $i++) {
        $char = $Source[$i]
        if ($char -eq "{") {
            $depth++
        }
        elseif ($char -eq "}") {
            $depth--
            if ($depth -eq 0) {
                return $Source.Substring($braceStart, $i - $braceStart + 1)
            }
        }
    }

    throw "Could not find method closing brace: $SignaturePattern"
}

function Assert-Contains {
    param(
        [string] $Text,
        [string] $Needle,
        [string] $Message
    )

    Assert-True ($Text.Contains($Needle)) $Message
}

$main = Get-Content -LiteralPath $MainWindowPath -Raw
$mac = Get-Content -LiteralPath $MacMainAppViewPath -Raw

$initializeTray = Get-MethodBody $main "private\s+void\s+InitializeSystemTray\s*\("
$refreshRecording = Get-MethodBody $main "private\s+void\s+RefreshRecordingMenu\s*\("
$toggleRecording = Get-MethodBody $main "private\s+async\s+void\s+ToggleRecordingFromTray\s*\("
$refreshMode = Get-MethodBody $main "private\s+void\s+RefreshModeMenu\s*\("
$refreshFileTranscription = Get-MethodBody $main "private\s+void\s+RefreshFileTranscriptionMenu\s*\("
$openUrl = Get-MethodBody $main "private\s+static\s+void\s+OpenUrl\s*\("

Assert-Contains $initializeTray "_recordingMenu.Click += (s, e) => Dispatcher.Invoke(ToggleRecordingFromTray);" "Tray recording item must call ToggleRecordingFromTray."
Assert-Contains $initializeTray 'Loc.S("menu.history")' "Tray must include History."
Assert-Contains $initializeTray 'Loc.S("menu.settings")' "Tray must include Settings."
Assert-Contains $initializeTray 'Loc.S("menu.microphone")' "Tray must include microphone submenu."
Assert-Contains $initializeTray 'Loc.S("menu.select.mode")' "Tray must include mode submenu."
Assert-Contains $initializeTray 'Loc.S("menu.transcribe.file")' "Tray must include transcribe-file submenu."
Assert-Contains $initializeTray 'Loc.S("settings.resources.help.center")' "Tray must include Help Center."
Assert-Contains $initializeTray 'Loc.S("settings.resources.contact.support")' "Tray must include Contact Support."
Assert-Contains $initializeTray 'Loc.S("settings.resources.feedback")' "Tray must include Feedback."
Assert-Contains $initializeTray 'Loc.S("settings.about.checkUpdates")' "Tray must include Check for Updates."
Assert-Contains $initializeTray "CheckForUpdatesFromTrayAsync" "Tray update item must call CheckForUpdatesFromTrayAsync."
Assert-Contains $initializeTray 'Loc.S("menu.version.label", version)' "Tray must include version label."
Assert-Contains $initializeTray 'Loc.S("common.quit")' "Tray must include Quit."
Assert-Contains $initializeTray "ShowMainWindow()" "Tray double-click must open the main window."

Assert-Contains $refreshRecording 'Loc.S("menu.recording.stop")' "Recording menu must show Stop while recording."
Assert-Contains $refreshRecording 'Loc.S("menu.recording.toggle")' "Recording menu must show toggle text while idle."
Assert-Contains $refreshRecording "_viewModel.IsRecording ||" "Recording menu must remain enabled while recording so stop is reachable."
Assert-Contains $refreshRecording "!_viewModel.IsTranscribing" "Recording menu must disable while transcribing."
Assert-Contains $refreshRecording "!_viewModel.IsModelLoading" "Recording menu must disable while model is loading."
Assert-Contains $refreshRecording "_viewModel.SelectedAudioDevice != null" "Recording menu must require an audio device before start."
Assert-Contains $refreshRecording "_viewModel.SelectedMode != null" "Recording menu must require a selected mode before start."

Assert-Contains $refreshMode "_modeMenu.DropDownItems.Clear();" "Mode submenu must rebuild from current modes."
Assert-Contains $refreshMode 'Loc.S("menu.mode.none")' "Mode submenu must show a disabled empty state."
Assert-Contains $refreshMode "modes.Count == 0" "Mode submenu must handle no modes."
Assert-Contains $refreshMode "foreach (var mode in modes)" "Mode submenu must enumerate all modes."
Assert-Contains $refreshMode "selectedMode != null && selectedMode.Id == mode.Id" "Mode submenu must check the selected mode."
Assert-Contains $refreshMode 'Loc.S("menu.mode.unnamed")' "Mode submenu must use unnamed-mode fallback."
Assert-Contains $refreshMode "Checked = isSelected" "Mode submenu must display selected checkmark."
Assert-Contains $refreshMode "Tag = mode" "Mode submenu items must carry the mode."
Assert-Contains $refreshMode "_viewModel.SelectedMode = m;" "Mode submenu click must select the clicked mode."

Assert-Contains $refreshFileTranscription "_fileTranscriptionMenu.DropDownItems.Clear();" "File transcription submenu must rebuild from current modes."
Assert-Contains $refreshFileTranscription 'Loc.S("menu.mode.none")' "File transcription submenu must show a disabled empty state."
Assert-Contains $refreshFileTranscription "foreach (var mode in modes)" "File transcription submenu must enumerate all modes."
Assert-Contains $refreshFileTranscription 'Loc.S("menu.mode.unnamed")' "File transcription submenu must use unnamed-mode fallback."
Assert-Contains $refreshFileTranscription "Enabled = !_viewModel.IsRecording && !_viewModel.IsTranscribing && !_viewModel.IsModelLoading" "File transcription submenu items must disable during recording, transcribing, and model loading."
Assert-Contains $refreshFileTranscription "Tag = mode" "File transcription submenu items must carry the mode."
Assert-Contains $refreshFileTranscription "TranscribeFileWithModeAsync(m)" "File transcription submenu click must route to mode-specific file transcription."

Assert-Contains $toggleRecording "StopRecordingAndTranscribeAsync()" "Tray recording stop remains a live transcription gate."
Assert-Contains $toggleRecording "StartRecordingAsync()" "Tray recording start remains a live microphone gate."
Assert-Contains $openUrl "Process.Start" "Tray resource links remain external browser gates."
Assert-Contains $openUrl "settings.general.support.openFailed" "Tray resource link failures must show a localized user-visible error."
Assert-Contains $openUrl "MessageBox.Show" "Tray resource link failures must not be log-only."

Assert-Contains $mac 'Text(localized: "menu.select.mode")' "macOS menu bar must include mode selection."
Assert-Contains $mac 'Text(localized: "menu.transcribe.file")' "macOS menu bar must include transcribe-file submenu."
Assert-Contains $mac 'NSWorkspace.shared.open' "macOS resource links must open externally."
Assert-Contains $mac 'NSApplication.shared.terminate(nil)' "macOS menu bar must include Quit."

function Remove-HarnessRoot {
    param([string] $PathToRemove)

    if ([string]::IsNullOrWhiteSpace($PathToRemove) -or -not (Test-Path -LiteralPath $PathToRemove)) {
        return
    }

    $fullPath = [System.IO.Path]::GetFullPath($PathToRemove)
    $tempRoot = [System.IO.Path]::GetFullPath($env:TEMP)
    Assert-True ($fullPath.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) `
        "Refusing to remove non-temp harness path: $fullPath"

    Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction SilentlyContinue
}

$RunId = [guid]::NewGuid().ToString("N")
$HarnessRoot = Join-Path $env:TEMP "hyperwhisper-tray-menu-verifier-$RunId"
New-Item -ItemType Directory -Force -Path $HarnessRoot | Out-Null

try {
    $HarnessProject = Join-Path $HarnessRoot "TrayMenuWiringVerifier.csproj"
    $HarnessProgram = Join-Path $HarnessRoot "Program.cs"
    $ProjectReference = [System.Security.SecurityElement]::Escape($ProjectRoot)

    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$ProjectReference\HyperWhisper.csproj" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $HarnessProject -Encoding UTF8

    @'
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Services;
using HyperWhisper.ViewModels;
using HyperWhisper.Views.Windows;

static FieldInfo Field(Type type, string name) =>
    type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
    ?? throw new MissingFieldException(type.FullName, name);

static MethodInfo Method(Type type, string name) =>
    type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
    ?? throw new MissingMethodException(type.FullName, name);

static void SetField(object instance, string name, object? value) =>
    Field(instance.GetType(), name).SetValue(instance, value);

static void Invoke(object instance, string name) =>
    Method(instance.GetType(), name).Invoke(instance, null);

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static MainWindow CreateWindow(MainViewModel viewModel, ToolStripMenuItem recording, ToolStripMenuItem mode, ToolStripMenuItem file)
{
    var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
    SetField(window, "_viewModel", viewModel);
    SetField(window, "_recordingMenu", recording);
    SetField(window, "_modeMenu", mode);
    SetField(window, "_fileTranscriptionMenu", file);
    return window;
}

static MainViewModel CreateViewModel()
{
    return (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
}

static void ConfigureViewModel(
    MainViewModel viewModel,
    bool isRecording,
    bool isTranscribing,
    bool isModelLoading,
    AudioDeviceService.AudioDevice? selectedAudioDevice,
    Mode? selectedMode,
    List<Mode>? modes)
{
    SetField(viewModel, "_isRecording", isRecording);
    SetField(viewModel, "_isTranscribing", isTranscribing);
    SetField(viewModel, "_isModelLoading", isModelLoading);
    SetField(viewModel, "_selectedAudioDevice", selectedAudioDevice);
    SetField(viewModel, "_selectedMode", selectedMode);
    SetField(viewModel, "_modes", modes ?? new List<Mode>());
}

var named = new Mode { Id = Guid.NewGuid(), Name = "Alpha", SortOrder = 1 };
var unnamed = new Mode { Id = Guid.NewGuid(), Name = "   ", SortOrder = 2 };
var modes = new List<Mode> { named, unnamed };
var device = new AudioDeviceService.AudioDevice(3, "Verifier Microphone");
var recordingMenu = new ToolStripMenuItem();
var modeMenu = new ToolStripMenuItem();
var fileMenu = new ToolStripMenuItem();
var viewModel = CreateViewModel();
var window = CreateWindow(viewModel, recordingMenu, modeMenu, fileMenu);

ConfigureViewModel(viewModel, false, false, false, null, named, modes);
Invoke(window, "RefreshRecordingMenu");
Assert(recordingMenu.Text == Loc.S("menu.recording.toggle"), "Idle recording menu must show toggle text.");
Assert(!recordingMenu.Enabled, "Recording menu must be disabled without an audio device.");

ConfigureViewModel(viewModel, false, false, false, device, named, modes);
Invoke(window, "RefreshRecordingMenu");
Assert(recordingMenu.Text == Loc.S("menu.recording.toggle"), "Ready recording menu must show toggle text.");
Assert(recordingMenu.Enabled, "Recording menu must be enabled when idle with device and mode.");

ConfigureViewModel(viewModel, false, true, false, device, named, modes);
Invoke(window, "RefreshRecordingMenu");
Assert(!recordingMenu.Enabled, "Recording menu must disable while transcribing.");

ConfigureViewModel(viewModel, false, false, true, device, named, modes);
Invoke(window, "RefreshRecordingMenu");
Assert(!recordingMenu.Enabled, "Recording menu must disable while model is loading.");

ConfigureViewModel(viewModel, true, false, true, null, null, modes);
Invoke(window, "RefreshRecordingMenu");
Assert(recordingMenu.Text == Loc.S("menu.recording.stop"), "Recording menu must show stop text while recording.");
Assert(recordingMenu.Enabled, "Recording menu must stay enabled while recording so stop is reachable.");

ConfigureViewModel(viewModel, false, false, false, device, unnamed, modes);
Invoke(window, "RefreshModeMenu");
Assert(modeMenu.DropDownItems.Count == 2, "Mode submenu must include all modes.");
Assert(((ToolStripMenuItem)modeMenu.DropDownItems[0]).Text == "Alpha", "Mode submenu must preserve named mode text.");
Assert(!((ToolStripMenuItem)modeMenu.DropDownItems[0]).Checked, "Unselected named mode must not be checked.");
Assert(((ToolStripMenuItem)modeMenu.DropDownItems[1]).Text == Loc.S("menu.mode.unnamed"), "Mode submenu must use unnamed fallback.");
Assert(((ToolStripMenuItem)modeMenu.DropDownItems[1]).Checked, "Selected unnamed mode must be checked.");
Assert(ReferenceEquals(((ToolStripMenuItem)modeMenu.DropDownItems[1]).Tag, unnamed), "Mode submenu item must carry the mode in Tag.");

ConfigureViewModel(viewModel, false, false, false, device, null, new List<Mode>());
Invoke(window, "RefreshModeMenu");
Assert(modeMenu.DropDownItems.Count == 1, "Mode submenu must show one empty-state item when there are no modes.");
Assert(modeMenu.DropDownItems[0].Text == Loc.S("menu.mode.none"), "Mode empty-state text must be localized.");
Assert(!modeMenu.DropDownItems[0].Enabled, "Mode empty-state item must be disabled.");

ConfigureViewModel(viewModel, false, false, false, device, named, modes);
Invoke(window, "RefreshFileTranscriptionMenu");
Assert(fileMenu.DropDownItems.Count == 2, "File transcription submenu must include all modes.");
Assert(fileMenu.DropDownItems[0].Text == "Alpha", "File transcription submenu must preserve named mode text.");
Assert(fileMenu.DropDownItems[1].Text == Loc.S("menu.mode.unnamed"), "File transcription submenu must use unnamed fallback.");
Assert(fileMenu.DropDownItems[0].Enabled, "File transcription submenu item must be enabled while idle.");
Assert(ReferenceEquals(((ToolStripMenuItem)fileMenu.DropDownItems[0]).Tag, named), "File transcription submenu item must carry the mode in Tag.");

ConfigureViewModel(viewModel, true, false, false, device, named, modes);
Invoke(window, "RefreshFileTranscriptionMenu");
Assert(!fileMenu.DropDownItems[0].Enabled, "File transcription submenu item must disable while recording.");

ConfigureViewModel(viewModel, false, true, false, device, named, modes);
Invoke(window, "RefreshFileTranscriptionMenu");
Assert(!fileMenu.DropDownItems[0].Enabled, "File transcription submenu item must disable while transcribing.");

ConfigureViewModel(viewModel, false, false, true, device, named, modes);
Invoke(window, "RefreshFileTranscriptionMenu");
Assert(!fileMenu.DropDownItems[0].Enabled, "File transcription submenu item must disable while model loading.");

ConfigureViewModel(viewModel, false, false, false, device, null, new List<Mode>());
Invoke(window, "RefreshFileTranscriptionMenu");
Assert(fileMenu.DropDownItems.Count == 1, "File transcription submenu must show one empty-state item when there are no modes.");
Assert(fileMenu.DropDownItems[0].Text == Loc.S("menu.mode.none"), "File transcription empty-state text must be localized.");
Assert(!fileMenu.DropDownItems[0].Enabled, "File transcription empty-state item must be disabled.");

Console.WriteLine("Tray menu refresh reflection verification passed.");
'@ | Set-Content -LiteralPath $HarnessProgram -Encoding UTF8

    dotnet run --project $HarnessProject --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Tray menu reflection harness failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-HarnessRoot $HarnessRoot
}

Write-Host "Tray menu wiring verification passed."
