using System.ComponentModel;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using HyperWhisper.Data.Entities;
using HyperWhisper.PortableApplication.ViewModels;
using HyperWhisper.LocalApi;
using HyperWhisper.ModelManagement;
using HyperWhisper.Linux.Platform.Desktop;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.CloudPostProcessing;
using HyperWhisper.Linux.Overlay;
using HyperWhisper.CloudAccount;
using System.Diagnostics;
using HyperWhisper.Storage;
using HyperWhisper.ModelReadiness;
using HyperWhisper.PortableApplication.ModelLibrary;
using HyperWhisper.Diagnostics;
using System.Reflection;
using HyperWhisper.Linux.Localization;
using HyperWhisper.Linux.Platform.Audio;
using HyperWhisper.Linux.Platform.Injection;
using HyperWhisper.Linux.Platform.Input;
using HyperWhisper.PortableApplication.Audio;

namespace HyperWhisper.Linux;

/// <summary>
/// The in-process UI smoke suite that <c>--smoke-test</c> runs under xvfb (App.axaml.cs,
/// app/linux/scripts/run-ui-smoke.sh). No production code calls any of it; it lives in its own
/// partial so new probes stop landing in the production window file (#2053).
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Issue #1777, on the real page: a multi-line text entered into the word box (the text-input
    /// path a paste takes) keeps all its lines, Enter adds one word per line, and Enter in the
    /// replacement box is a line break, not a submit. The rows it adds are removed again.
    /// </summary>
    private async Task<bool> SmokeVocabularyPasteIsOneWordPerLineAsync()
    {
        _viewModel.Navigate("vocabulary");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        if (FindNamed<TextBox>("VocabularyWordInput") is not { } word
            || FindNamed<TextBox>("VocabularyReplacementInput") is not { } replacement) return false;
        var vocabulary = _viewModel.Vocabulary;
        string[] words = ["smoke1777alpha", "smoke1777beta", "smoke1777gamma"];
        bool Fail(string reason) { Console.Error.WriteLine($"Smoke: vocabulary paste (#1777): {reason}"); return false; }

        word.Focus();
        word.RaiseEvent(new TextInputEventArgs { RoutedEvent = TextInputEvent, Text = string.Join("\n", words) });
        if (vocabulary.Word != string.Join("\n", words))
            return Fail($"the word box kept '{vocabulary.Word.ReplaceLineEndings("\\n")}' of a three-line paste");
        word.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Enter });
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        for (var attempt = 0; attempt < 50 && vocabulary.Word.Length > 0; attempt++)
            await Task.Delay(20);
        var added = vocabulary.Items.Where(item => item.Word.StartsWith("smoke1777", StringComparison.Ordinal)).ToList();
        try
        {
            if (vocabulary.Word.Length > 0) return Fail($"Enter did not add the pasted words (the box holds '{vocabulary.Word.ReplaceLineEndings("\\n")}')");
            if (!added.Select(item => item.Word).Order(StringComparer.Ordinal).SequenceEqual(words))
                return Fail($"Enter saved [{string.Join(" | ", added.Select(item => item.Word.ReplaceLineEndings("\\n")))}]");

            ShowVocabularyReplacement();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            replacement.Focus();
            replacement.RaiseEvent(new TextInputEventArgs { RoutedEvent = TextInputEvent, Text = "Best," });
            replacement.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Enter });
            replacement.RaiseEvent(new TextInputEventArgs { RoutedEvent = TextInputEvent, Text = "Ray" });
            if (FindNamed<Border>("VocabularyReplacementPanel")?.IsVisible != true
                || vocabulary.Replacement.ReplaceLineEndings("\n") != "Best,\nRay")
                return Fail($"Enter in the replacement box gave '{vocabulary.Replacement.ReplaceLineEndings("\\n")}', not a line break");
            return true;
        }
        finally
        {
            foreach (var item in added) await vocabulary.DeleteAsync(item);
            OnVocabularyCancelEdit(this, new RoutedEventArgs());
            _viewModel.Navigate("home");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
    }

    internal async Task<int> RunSmokeTestAsync()
    {
        try
        {
            await EnsureInitializedAsync();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (Bounds.Width <= 0 || Bounds.Height <= 0 || !IsVisible) return 2;
            var expectedCulture = AvaloniaLocalizationBridge.ResolveStartupCulture(
                Environment.GetEnvironmentVariable("HYPERWHISPER_UI_CULTURE"));
            if (!string.Equals(_localization.Culture.Name, expectedCulture.Name,
                    StringComparison.OrdinalIgnoreCase)) return 20;
            if (FlowDirection != _localization.FlowDirection) return 21;
            if (Navigation.Items.OfType<ListBoxItem>().FirstOrDefault()?.Content?.ToString()
                != _localization.GetRequired("sidebar.home")) return 22;
            if (!Path.IsPathFullyQualified(_platformServices.Paths.DataDirectory)
                || _platformServices.PrivateFiles is null
                || _platformServices.GlobalShortcuts is null
                || !_platformServices.ProbeSharedCore()) return 4;
            if (_viewModel.Status.HasError)
            {
                Console.Error.WriteLine($"Smoke startup failed: {_viewModel.Status.ErrorCode} · "
                    + $"settings={_viewModel.Settings.Status.ErrorCode}, home={_viewModel.Home.Status.ErrorCode}, "
                    + $"history={_viewModel.History.Status.ErrorCode}, vocabulary={_viewModel.Vocabulary.Status.ErrorCode}, "
                    + $"modes={_viewModel.Modes.Status.ErrorCode}");
                return 5;
            }
            if (_viewModel.Recording is null || string.IsNullOrWhiteSpace(_viewModel.Recording.Message)) return 8;

            await SeedSmokeDataAsync();
            // History and Model Library hide their detail pane until a row is picked, the way the
            // Windows app does. Pick the first row on each so the detail controls are on screen.
            _viewModel.History.Selected ??= _viewModel.History.Items.FirstOrDefault();
            if (_viewModel.Models is not null)
                _viewModel.Models.Selected ??= _viewModel.Models.Items.FirstOrDefault();
            var expectedControls = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["home"] = "HomeStartRecordingButton",
                // The mode editor is a dialog now, so the page itself is only the card grid.
                ["modes"] = "ModeGrid",
                ["history"] = "HistoryDeleteButton",
                ["vocabulary"] = "VocabularyAddButton",
                // The Model Library lost its bottom detail strip: the row actions moved onto the
                // rows, so the search pill is the control that is always on this page.
                ["models"] = "ModelSearch",
                ["backup"] = "BackupExportButton",
                ["account"] = "AccountActivateButton",
                ["credentials"] = "CredentialSaveButton",
                ["settings"] = "SettingsLaunchMinimized"
            };
            foreach (var page in expectedControls)
            {
                _viewModel.Navigate(page.Key);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                // Windows shows a page-independent "Ready" here, so the message no longer names
                // the page; the page identity is asserted by PageTitle and the visible control.
                if (!HasVisibleControl(page.Value)
                    || string.IsNullOrWhiteSpace(_viewModel.PageTitle)
                    || !string.Equals(_viewModel.Status.Message, "Ready", StringComparison.Ordinal))
                    return 3;
            }

            // The typing speed moved from a combo row under the page into the gear on the stats
            // bar, which is the only control that still reaches that setting from Home.
            _viewModel.Navigate("home");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!HasVisibleControl("HomeTypingSpeed")) return 17;

            if (!await SmokeVocabularyPasteIsOneWordPerLineAsync()) return 18;

            // The recording and file-import controls have no Windows equivalent anywhere, so they
            // live at the END of Home, below Recent Updates. They were on the Modes page, which
            // Windows ends at the mode grid, so keeping them there changed the shape of a page
            // both apps have. They bind past Home's own context to the shell.
            // The audio-input combo is deliberately NOT checked: it hides itself when the machine
            // reports no capture device, which is the normal state on a headless test box.
            // Issue #1187: while not recording the row offers Start, enabled, and no Stop or Cancel.
            // Stop and Cancel must still be in the tree, hidden, so the Recording state can show them.
            if (IsHomeRecordingActive || _viewModel.Recording is not { CanStop: false, CanCancel: false }
                || !HasVisibleControl("HomeRecordStartButton")
                || !this.GetLogicalDescendants().OfType<Control>().Any(c => c.Name == "HomeRecordStartButton" && c.IsEnabled)
                || !HasControl("HomeStopRecordingButton") || HasVisibleControl("HomeStopRecordingButton")
                || !HasControl("HomeCancelRecordingButton") || HasVisibleControl("HomeCancelRecordingButton")
                || !HasVisibleControl("HomeAudioFileInput")
                || !HasVisibleControl("HomeTranscribeFileButton")) return 9;

            _viewModel.Navigate("modes");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

            // The ~25 mode fields moved into the editor dialog, so the coverage moves with them:
            // the dialog is built (never shown) and its own tree is checked instead.
            var modes = _viewModel.Modes;
            var editorSnapshot = modes.CaptureEditorState();
            var editor = new ModeEditorWindow(modes, false, editorSnapshot);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

            // Every field must exist in the dialog's tree. Presence, not visibility: the editor
            // now follows the Windows reveal rules, so most of these are hidden for any one
            // source. The reveal rules themselves are exercised below.
            string[] modeEditorControls =
            [
                "ModeNameInput", "ModeLanguageInput", "ModeLocalEngine",
                "ModeTranscriptionModel", "ModeCloudTranscriptionModel", "ModeCloudProvider",
                "ModeCloudAccuracyTier", "ModeCloudDomain", "ModeGeminiPrompt", "ModeCustomVocabulary",
                "ModePreset", "ModePostProcessingEnabled", "ModePostProcessingProvider",
                "ModePostProcessingModel", "ModeHyperWhisperCloudModel", "ModeLocalPostProcessingModel",
                "ModeCustomEndpointName", "ModeCustomEndpointUrl", "ModeCustomEndpointApiKey",
                "ModeCustomEndpointModel", "ModeCustomInstructions", "ModeUserSystemPrompt",
                "ModeUserPromptEnabled", "ModeClearUserPrompt",
                "ModePunctuation", "ModeCapitalization", "ModeProfanityFilter",
                "ModeRemoveTrailingPeriod", "ModeEnglishSpelling", "ModeEnableScreenOcr",
                "ModeSourceOnDevice", "ModeSourceHwCloud", "ModeSourceYourProvider",
                "ModePpSourceOnDevice", "ModePpSourceHwCloud", "ModePpSourceYourProvider",
                "ModeSaveButton", "ModeCancelButton", "ModeDeleteButton",
            ];
            foreach (var control in modeEditorControls)
                if (!HasControl(control, editor))
                {
                    Console.Error.WriteLine($"Smoke: {control} is missing from the mode editor.");
                    editor.Close();
                    return 9;
                }

            // The three transcription sources reveal three different sets of controls, which is
            // the single biggest behaviour the Linux editor was missing. Assert each one.
            if (await ModeEditorRevealFailureAsync(editor, () => modes.IsOnDeviceSource = true,
                    ["ModeLocalEngine", "ModeTranscriptionModel"],
                    ["ModeCloudProvider", "ModeCloudAccuracyTier", "ModeCloudTranscriptionModel"])
                || await ModeEditorRevealFailureAsync(editor, () => modes.IsHwCloudSource = true,
                    ["ModeCloudAccuracyTier"],
                    ["ModeLocalEngine", "ModeCloudProvider", "ModeCloudTranscriptionModel"])
                || await ModeEditorRevealFailureAsync(editor, () => modes.IsYourProviderSource = true,
                    ["ModeCloudProvider", "ModeCloudTranscriptionModel"],
                    ["ModeLocalEngine", "ModeCloudAccuracyTier"]))
            {
                editor.Close();
                return 9;
            }

            // Post-processing off hides the whole settings block and both punctuation options,
            // exactly as Windows does (ModeEditorWindow.xaml.cs:1354-1355, :1836).
            if (await ModeEditorRevealFailureAsync(editor, () => modes.PostProcessingEnabled = false,
                    ["ModePostProcessingEnabled"],
                    ["ModePreset", "ModePunctuation", "ModeCapitalization", "ModeUserPromptEnabled"])
                || await ModeEditorRevealFailureAsync(editor, () => { modes.PostProcessingEnabled = true; modes.Preset = "custom"; },
                    ["ModePreset", "ModeCustomInstructions", "ModePunctuation"],
                    ["ModeUserSystemPrompt"]))
            {
                editor.Close();
                return 9;
            }

            // Cancel must put every field back, which is what the editor's snapshot buys.
            modes.RestoreEditorState(editorSnapshot);
            editor.Close();

            // A mode name is free user text with no cap anywhere, and this window is a fixed
            // 1000 that cannot resize. Both surfaces that draw it are MEASURED, not grepped.
            if (await ModeNameLayoutFailureAsync()) return 23;

            _viewModel.Navigate("history");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            // Two classes of control on this page now, because it follows the Windows page.
            // These are always on screen once a row is picked.
            foreach (var control in new[]
                     { "HistorySearchInput", "HistoryPeriod", "HistoryList", "HistoryDetailText",
                       "HistoryCopyButton", "HistoryDeleteButton" })
                if (!HasVisibleControl(control))
                {
                    Console.Error.WriteLine($"Smoke: {control} is not visible on the history page.");
                    return 11;
                }
            // These are deliberately conditional, exactly as Windows draws them: the transport
            // is hidden when the audio cannot play, Retry only appears on a retryable row, the
            // raw toggle only on a post-processed one, and the two menus are popups. Presence in
            // the tree is what the check can assert; visibility is the view models job.
            foreach (var control in new[]
                     { "HistoryPlayButton", "HistoryPlaybackSlider", "HistoryRetryButton",
                       "HistoryShowRawTranscript" })
                if (!HasControl(control))
                {
                    Console.Error.WriteLine($"Smoke: {control} is missing from the history page.");
                    return 11;
                }
            // The Retry With… submenu hangs off the list ContextMenu, which is its own popup tree
            // and never a logical descendant of the window, so it is reached through its owner.
            if (FindNamed<ListBox>("HistoryList")?.ContextMenu is not { } historyMenu
                || !historyMenu.GetLogicalDescendants().OfType<Control>()
                    .Any(control => control.Name == "HistoryRetryMode"))
            {
                Console.Error.WriteLine("Smoke: HistoryRetryMode is missing from the history context menu.");
                return 11;
            }
            // Transcript.Mode is a COPY of the mode name, so History draws that free user text
            // twice more. Both surfaces are MEASURED, not grepped, in the real window.
            if (await HistoryModeNameLayoutFailureAsync(historyMenu)) return 24;
            // The Delete flyout and its opt-in audio checkbox are gone: Windows raises a YesNo
            // MessageBox whose text already promises the audio file goes too, so both the single
            // and the multi-row Delete now run OnHistoryConfirmDelete. What has to exist is the
            // multi-selection Delete, which used to fire its command with no confirmation at all.
            if (!HasControl("HistoryDeleteSelectedButton"))
            {
                Console.Error.WriteLine("Smoke: HistoryDeleteSelectedButton is missing.");
                return 11;
            }
            _viewModel.Navigate("vocabulary");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!HasVisibleControl("VocabularyWordInput") || !HasVisibleControl("VocabularyReplacementInput")
                || !HasVisibleControl("VocabularyList")) return 12;
            // Windows has no import/export row on the Vocabulary page and no vocabulary TSV
            // transfer anywhere: vocabulary travels inside the backup archive, selected by the
            // Vocabulary checkbox on each of the two Backup cards. Those are what is asserted.
            _viewModel.Navigate("backup");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!HasVisibleControl("BackupExportVocabulary") || !HasVisibleControl("BackupExportButton")
                || !HasVisibleControl("BackupChooseImportButton")) return 12;
            // Settings is ten sections now, as on Windows, so each one is checked on its own page.
            var settingsSections = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                // Windows closes General with the auto-update row, a Need help? card and the app
                // version, and has no language field on the page at all: that moved to Text Output.
                ["settings"] = ["SettingsAutostart", "SettingsLaunchMinimized", "SettingsMinimizeToTray",
                    "SettingsShowRecordingWindow", "SettingsEnableErrorLogging", "SettingsShareSpeedData",
                    "SettingsSpeedComparisonLink", "SettingsAutoUpdate", "SettingsSupportButton",
                    "SettingsVersionText"],
                // Three Windows cards and nothing after them: the sound-effect volume slider and
                // the silence-trim switch are gone with the Linux-only card that held them.
                ["sound"] = ["SettingsSoundEffects", "SettingsAudioEnvironmentPolicy", "SettingsBoostMicrophone",
                    "SettingsKeepMicrophoneWarm"],
                // Likewise the keep-app-owned-audio switch and the usage readout.
                ["storage"] = ["SettingsRecordingsDirectory", "SettingsChooseRecordingsDirectory",
                    "SettingsStoreAsM4A", "SettingsAutoDeleteEnabled", "SettingsStorageOpenFolder"],
                // Likewise the word-timestamp switch and the default-language box.
                ["output"] = ["SettingsPasteResultText", "SettingsRemoveFillerWords", "SettingsAutocapitalizeInsert",
                    "SettingsRestoreClipboard", "SettingsHideClipboardHistory"],
                ["localapi"] = ["SettingsLocalApiEnabled", "SettingsLocalApiStatus"],
                ["shortcuts"] = ["SettingsToggleKey", "SettingsCancelKey", "SettingsChangeModeKey",
                    "SettingsStreamingShortcutKey", "SettingsPushToTalkMode", "SettingsResetShortcuts"],
                ["appearance"] = ["SettingsThemeMode"],
                // The local Whisper and LLM runtime pickers moved off Appearance, which Windows
                // draws as one Theme card, onto the end of the Model Library page.
                ["models"] = ["SettingsLocalWhisperBackend", "SettingsLocalWhisperCpuFallback",
                    "SettingsLocalWhisperRuntimeStatus", "SettingsLocalLlmBackend", "SettingsLocalLlmCpuFallback"],
                ["streaming"] = ["StreamingEnabledToggle", "StreamingProviderChoice", "StreamingLanguageInput"]
            };
            // The Streaming page hides its provider rows until streaming is on, as Windows does.
            _viewModel.Settings.StreamingEnabled = true;
            foreach (var section in settingsSections)
            {
                _viewModel.Navigate(section.Key);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                foreach (var control in section.Value)
                    if (!HasVisibleControl(control))
                    {
                        Console.Error.WriteLine($"Smoke: {control} is not visible on the {section.Key} page.");
                        return 10;
                    }
            }
            // These are deliberately conditional, exactly as Windows draws them. Auto-delete hides
            // its retention field and Delete Now until the toggle is on; the clipboard restore
            // delay follows the restore switch; and the whole Local API body — the tab strip, the
            // connection rows and both snippets — stays collapsed until the server is enabled.
            // The smoke profile leaves all three off, so presence in the tree is what can be
            // asserted here. Visibility is the page's own job, checked by the bindings above.
            var conditionalSections = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["storage"] = ["SettingsAutoDeleteDays", "SettingsStorageDeleteNow"],
                ["output"] = ["SettingsClipboardRestoreDelay"],
                ["localapi"] = ["SettingsLocalApiPort", "SettingsLocalApiToken", "SettingsLocalApiDiscoveryPath",
                    "SettingsLocalApiMcpSnippet", "SettingsLocalApiCurlSnippet"],
            };
            foreach (var section in conditionalSections)
            {
                _viewModel.Navigate(section.Key);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                foreach (var control in section.Value)
                    if (!HasControl(control))
                    {
                        Console.Error.WriteLine($"Smoke: {control} is missing from the {section.Key} page.");
                        return 10;
                    }
            }

            // The preferred port is the one field on this head whose value leaves the process: it
            // is what the Local API server binds and what local-api.json advertises to every MCP
            // client. #692 had it committing per keystroke and swallowing an out-of-range entry,
            // so the field and the running server disagreed with nothing on screen to say so.
            // Typed into the REAL control, for the reasons in the probe's own summary. 26 is the
            // regression; 27 says the probe could not be set up, which is an environment fault and
            // not evidence that #692 is back.
            if (await LocalApiPortCommitFailureAsync() is var portCode && portCode != 0) return portCode;
            // #653: a Copy or Regenerate report on the shared action line must not outlive a
            // change of server state. 28 is the regression, 29 a probe that could not be set up.
            if (await LocalApiActionStatusClearFailureAsync() is var statusCode && statusCode != 0) return statusCode;
            // #643: Reveal must show the whole token, not a revealed value cut off with an
            // ellipsis. 30 is the regression, 31 a probe that could not be set up.
            if (await LocalApiRevealedTokenFailureAsync() is var tokenCode && tokenCode != 0) return tokenCode;

            _viewModel.Navigate("account");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            // Windows shows ONE of two sibling views here. The smoke profile has no key, so the
            // unlicensed view is the one on screen: activation field, Activate, Get Credits. The
            // licensed controls (refresh, Manage Billing, Deactivate) are correctly not visible.
            if (!HasVisibleControl("AccountKeyInput")
                || !HasVisibleControl("AccountActivateButton")
                || !HasVisibleControl("AccountPurchaseButton")) return 16;

            if (_viewModel.History.Items.Count == 0
                || _viewModel.Vocabulary.Items.Count == 0
                || _viewModel.Modes.Items.Count == 0) return 6;

            // The error toast is the only surface ~25 failure sites reach, its message is free
            // text of any length, and both its readability and its placement are MEASURED in the
            // real window. Run BEFORE the overlay below is ever shown: the fallback branch of the
            // toast's placement is the one that applies while no overlay is on screen, and the
            // probe brings its own anchor for the other branch.
            if (await ErrorToastLayoutFailureAsync()) return 25;

            _overlay.RecordingStarted(LinuxOverlayModeLabel.Create("Smoke Mode"));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (_overlay.Snapshot is not { State: LinuxRecordingOverlayState.Recording, IsVisible: true }) return 13;
            _overlay.Transcribing();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (_overlay.Snapshot is not { State: LinuxRecordingOverlayState.Transcribing, IsVisible: true }) return 14;
            _overlay.Completed(LinuxRecordingOverlayCompletion.Pasted);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (_overlay.Snapshot is not { State: LinuxRecordingOverlayState.Pasted, IsVisible: true }) return 15;
            _viewModel.Settings.ResetShortcuts();
            _viewModel.Settings.Language = "en";
            _viewModel.Settings.Save();
            if (_viewModel.Settings.Status.HasError)
            {
                Console.Error.WriteLine($"Smoke settings save failed: {_viewModel.Settings.Status.ErrorCode}");
                return 7;
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Smoke failed: {exception}");
            return 1;
        }
    }

    private bool HasVisibleControl(string name) => HasVisibleControl(name, this);

    /// <summary>Present in the tree, whether or not its own binding is showing it right now.</summary>
    private bool HasControl(string name) => HasControl(name, this);

    private static bool HasControl(string name, Visual root)
        => root.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == name);

    /// <summary>
    /// Issue #526, the Linux half of #492/#521. A mode name is free user text with no cap
    /// anywhere — not in the editor, not in the entity, not in the column — and two surfaces
    /// draw it: the card on the Modes page and the status bar. This lays a 300-character name
    /// out in the REAL window and measures what came back.
    ///
    /// MEASURED, not grepped, for the same two reasons the Windows suite gives: the attribute
    /// can be put on the wrong TextBlock, and a later container change can reintroduce the
    /// clip with the attribute still present. Every comparison below is against the width the
    /// string itself wants, so no pixel number is hard-coded and a font change cannot fake it.
    ///
    /// Returns true when the check FAILED, so the caller can bail with a smoke exit code.
    ///
    /// The probe modes are inserted and then REMOVED again, and the selection is captured
    /// BEFORE the insert and put back after. Every other seed on this path is guarded by an
    /// "only if the table is empty" test, because run-ui-smoke.sh runs against the developer's
    /// own profile as well as a throwaway CI one, and CI runs it twice (X11 and XWayland). An
    /// unconditional insert would leave a 300-character mode behind on a real install, once
    /// per run, and selecting one persists its id to settings.json.
    /// </summary>
    private async Task<bool> ModeNameLayoutFailureAsync()
    {
        var longName = "LongName" + new string('X', 292);
        // Captured before anything is inserted: RefreshAsync re-picks the selection from the
        // row set, so a selection read after the insert can already BE a probe.
        var restoreSelected = _viewModel.Modes.Selected;
        Guid cloudProbeId;
        Guid localProbeId;
        await using (var context = _database.CreateContext())
        {
            // Both configurations of the status bar, because the post-processing column is
            // what makes the budget tight: it is hidden on a cloud mode and visible on a
            // local post-processing one, and only the second case was ever near the edge.
            // The Windows suite loops the same pair.
            var cloud = new Mode { Name = longName, Language = "en" };
            var local = new Mode
            {
                Name = longName,
                Language = "en",
                // The shape ApplicationShellViewModel.UpdateLocalPostProcessingStatus reads:
                // mode 2 is "local", and only local_llm runs a model on this machine.
                PostProcessingMode = 2,
                PostProcessingProvider = "local_llm",
                LocalPostProcessingModel = "Llama 3.2 3B Instruct Q4_K_M",
            };
            context.Modes.AddRange(cloud, local);
            await context.SaveChangesAsync();
            cloudProbeId = cloud.Id;
            localProbeId = local.Id;
        }
        try
        {
            return await ModeNameLayoutFailureCoreAsync(longName, cloudProbeId, localProbeId);
        }
        finally
        {
            if (restoreSelected is not null) _viewModel.Modes.Selected = restoreSelected;
            await using (var context = _database.CreateContext())
            {
                foreach (var id in new[] { cloudProbeId, localProbeId })
                    if (await context.Modes.FindAsync(id) is { } probe)
                        context.Modes.Remove(probe);
                await context.SaveChangesAsync();
            }
            // Re-resolves Selected against the rows that are left, so a settings.json still
            // pointing at a deleted probe falls back to a real mode instead of nothing.
            await _viewModel.Modes.RefreshAsync();
        }
    }

    private async Task<bool> ModeNameLayoutFailureCoreAsync(string longName, Guid cloudProbeId, Guid localProbeId)
    {
        await _viewModel.Modes.RefreshAsync();
        _viewModel.Navigate("modes");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

        // THE CARD. The header is a *,Auto Grid with the gear beside the name, so the star
        // column already stops the name pushing the gear off the card; what it does not do is
        // say the name was cut.
        //
        // Found by the row it draws, never by its text: a lookup keyed on the text reports a
        // name truncated in DATA as a missing control, which points the reader at the wrong
        // thing entirely. The two are separate assertions below, as they are on Windows.
        var card = this.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(block => block.Name == "ModeNameText"
                                     && block.DataContext is Mode mode && mode.Id == cloudProbeId);
        if (card is null)
        {
            Console.Error.WriteLine("Smoke: the long mode's card has no ModeNameText.");
            return true;
        }
        // The row itself is untouched: this is a display fix, and the card must still carry the
        // whole name so the tooltip and any copy of it are right.
        if (card.Text != longName)
        {
            Console.Error.WriteLine($"Smoke: the card bound {card.Text?.Length} characters of a "
                + $"{longName.Length}-character name — the name is being truncated in data, "
                + "which is not the fix.");
            return true;
        }
        if (card.TextTrimming != TextTrimming.CharacterEllipsis)
        {
            Console.Error.WriteLine($"Smoke: the mode card trims with {card.TextTrimming}, so a long "
                + "name is cut mid-glyph under the gear with nothing to say it continues.");
            return true;
        }
        // Prove the check exercises the overflow at all: if the card were ever wide enough for
        // 300 characters, everything above would pass while proving nothing.
        if (card.Bounds.Width <= 0 || UnconstrainedTextWidth(card) <= card.Bounds.Width)
        {
            Console.Error.WriteLine($"Smoke: the {longName.Length}-character name wanted "
                + $"{UnconstrainedTextWidth(card):F0}px and got {card.Bounds.Width:F0}px, so it never "
                + "overflowed and this check proves nothing.");
            return true;
        }
        if (card.Parent is not Grid header
            || header.GetLogicalDescendants().OfType<Button>().FirstOrDefault() is not { } gear
            || card.TranslatePoint(new Point(card.Bounds.Width, 0), header) is not { } nameEnd
            || gear.TranslatePoint(default, header) is not { } gearStart)
        {
            Console.Error.WriteLine("Smoke: the mode card header is no longer the name/gear Grid.");
            return true;
        }
        if (nameEnd.X > gearStart.X + 0.5 || gearStart.X + gear.Bounds.Width > header.Bounds.Width + 0.5)
        {
            Console.Error.WriteLine($"Smoke: the name ends at {nameEnd.X:F1} and the gear runs "
                + $"{gearStart.X:F1}..{gearStart.X + gear.Bounds.Width:F1} across a "
                + $"{header.Bounds.Width:F1} wide header — the name is under or past the gear.");
            return true;
        }

        // THE STATUS BAR, which is the half the issue actually complained about. The mode name
        // sits in an Auto column, an Auto column grows to whatever it is given, and one
        // unbounded item in a row this window cannot widen pushes the others out of it.
        // The caller puts the previous selection back.
        foreach (var (label, probeId) in new[]
                 { ("cloud mode", cloudProbeId), ("with post-processing", localProbeId) })
        {
            if (await StatusBarRowFailureAsync(longName, label, probeId)) return true;
        }
        return false;
    }

    /// <summary>
    /// Selects one probe mode and measures the whole status-bar row it produces.
    /// Returns true when the check FAILED.
    /// </summary>
    private async Task<bool> StatusBarRowFailureAsync(string longName, string label, Guid probeId)
    {
        _viewModel.Modes.Selected = _viewModel.Modes.Items.FirstOrDefault(mode => mode.Id == probeId);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

        var modeName = FindNamed<TextBlock>("StatusModeName");
        var modelName = FindNamed<TextBlock>("StatusModelName");
        var postProcessing = FindNamed<TextBlock>("StatusPostProcessingText");
        if (StatusText is null || modeName is null || modelName is null || postProcessing is null
            || modeName.Parent?.Parent is not Grid row)
        {
            Console.Error.WriteLine($"Smoke: {label}: the status bar row is missing a named item.");
            return true;
        }
        // Without this the whole-hint check below is vacuous: StatusText has no XAML binding
        // and is composed imperatively by UpdateShortcutHint, so an empty one would want 0px
        // and satisfy every width comparison while the star column was in fact collapsed.
        if (string.IsNullOrWhiteSpace(StatusText.Text))
        {
            Console.Error.WriteLine($"Smoke: {label}: the status hint is empty, so this check "
                + "cannot tell a whole hint from a hint the mode name has squeezed out.");
            return true;
        }
        if (modeName.Text != longName)
        {
            Console.Error.WriteLine($"Smoke: {label}: the status bar bound {modeName.Text?.Length} "
                + $"characters of a {longName.Length}-character name — it is being truncated in "
                + "data, which is not the fix.");
            return true;
        }
        var withPostProcessing = label == "with post-processing";
        if (withPostProcessing != postProcessing.IsEffectivelyVisible)
        {
            Console.Error.WriteLine($"Smoke: {label}: the post-processing column is "
                + $"{(postProcessing.IsEffectivelyVisible ? "visible" : "hidden")}, so this case is "
                + "not the configuration it claims to be.");
            return true;
        }

        // THE BUG, STATED AS THE BUG: nothing may be pushed out of the row. Before the fix the
        // name's Auto column took the whole width and column 0 measured nothing at all.
        foreach (var (name, block) in new[]
                 { ("the status hint", StatusText), ("the model status", modelName) })
        {
            if (block.Bounds.Width <= 0)
            {
                Console.Error.WriteLine($"Smoke: {label}: {name} measured 0px — the mode name has "
                    + "taken the whole status bar.");
                return true;
            }
        }
        // On a cloud mode — the issue's own repro, and the common case, since the
        // post-processing column only appears for a LOCAL LLM — both must be WHOLE and not
        // merely present. A hint trimmed to "Ready - Press Ct..." would satisfy the check
        // above while still hiding the hotkey.
        //
        // The tight case is deliberately not held to this. Four items in a fixed row, two of
        // them long local model names, genuinely do not all fit; the caps decide who gives
        // way, and every one of them now says so with an ellipsis instead of vanishing.
        if (!withPostProcessing)
        {
            foreach (var (name, block) in new[]
                     { ("the status hint", StatusText), ("the model status", modelName) })
            {
                var wanted = UnconstrainedTextWidth(block);
                if (block.Bounds.Width + 0.5 < wanted)
                {
                    Console.Error.WriteLine($"Smoke: {label}: {name} rendered {block.Bounds.Width:F1}px "
                        + $"for text that needs {wanted:F1}px — the mode name has taken the status bar.");
                    return true;
                }
            }
        }
        else if (postProcessing.Bounds.Width > 150.5)
        {
            Console.Error.WriteLine($"Smoke: {label}: the post-processing status rendered "
                + $"{postProcessing.Bounds.Width:F1}px against its 150px cap.");
            return true;
        }
        if (modelName.TranslatePoint(new Point(modelName.Bounds.Width, 0), row) is not { } modelEnd
            || modelEnd.X > row.Bounds.Width + 0.5)
        {
            Console.Error.WriteLine($"Smoke: {label}: the model status runs past the right edge of "
                + $"the {row.Bounds.Width:F1}px status bar.");
            return true;
        }
        // And the mode name is the item that gives way, with an ellipsis.
        if (modeName.TextTrimming != TextTrimming.CharacterEllipsis
            || modeName.Bounds.Width >= UnconstrainedTextWidth(modeName))
        {
            Console.Error.WriteLine($"Smoke: {label}: the status bar mode name trims with "
                + $"{modeName.TextTrimming} and rendered {modeName.Bounds.Width:F1}px of "
                + $"{UnconstrainedTextWidth(modeName):F1}px — it was not bounded at all.");
            return true;
        }
        return false;
    }

    /// <summary>
    /// The width this TextBlock's own text wants on ONE line, with nothing constraining it.
    /// Comparing that against the arranged width is how a layout check tells "it fits" from "it
    /// was cut", without a hard-coded pixel number that a font or a theme change would invalidate.
    ///
    /// One line of <see cref="TextProbe"/>, not a second probe of its own. It carried four font
    /// properties where TextProbe carries eight, so the two measured the same TextBlock
    /// differently -- LetterSpacing alone is enough to make this one report a string NARROWER
    /// than it renders, which understates every "did it fit" comparison below. There is one
    /// definition of what the text wants, and a property added to it reaches every check at once.
    /// </summary>
    private static double UnconstrainedTextWidth(TextBlock block)
        => TextProbe(block, double.PositiveInfinity).Width;

    /// <summary>
    /// Issue #669's regression guard, the error-toast sibling of #526's check above. The toast is
    /// the only surface the app's ~25 failure sites become visible on, and its message was capped
    /// to 200px: "A HyperWhisper account key is required. Open Settings to…" reached the user as
    /// "A HyperWhisper account key is r…", which says nothing about what to do.
    ///
    /// MEASURED in a REAL LinuxErrorToastWindow, not grepped, for #526's two reasons and for a
    /// third this bug has of its own: the cap can come back from at least four directions that an
    /// assertion on the message's XAML attributes cannot see at all — a style Setter, a
    /// &lt;TextBlock.MaxWidth&gt; child element, a "_message.MaxWidth = 200" in the constructor
    /// (this window already sets SizeToContent from C#, to dodge the localization regex), and the
    /// Grid's star column changed to Auto. Every comparison below is against the size the string
    /// itself wants, so no pixel number is hard-coded and a font change cannot fake it.
    ///
    /// It measures the PLACEMENT as well, because the wrapped message is what made the toast's
    /// height a per-message value: both branches of PlaceOnScreen subtract that height from a
    /// downward anchor, the window is RE-USED across failures (MainWindow caches one and
    /// DismissImmediately only hides it), and the overlay it anchors to is draggable to the very
    /// top of the screen. So the second message must be placed with ITS OWN height, and no
    /// message may be placed above the top of the work area, where the user would see no error
    /// feedback at all.
    ///
    /// Returns true when the check FAILED, so the caller can bail with a smoke exit code.
    ///
    /// The window is built and shown here rather than through MainWindow.ShowErrorToast, which
    /// early-returns under --smoke-test. Both windows this opens are closed again in the finally,
    /// and the anchor is a LOCAL overlay with an in-memory placement store: run-ui-smoke.sh runs
    /// against the developer's own profile, and moving the app's own overlay would persist a new
    /// position into it.
    /// </summary>
    private async Task<bool> ErrorToastLayoutFailureAsync()
    {
        // The issue's own string, not lorem: this is the sentence the 200px cap cut to five words.
        const string failure = "A HyperWhisper account key is required. Open Settings to sign in to "
            + "HyperWhisper Cloud, or switch this mode to a local model.";
        // Deliberately one short line, shown FIRST and on the SAME window, so that the measured
        // message below is the second one a re-used toast has had to size itself for.
        const string firstFailure = "Microphone unavailable.";

        var toast = new LinuxErrorToastWindow();
        LinuxRecordingOverlayWindow? anchor = null;
        try
        {
            // Shown first and never measured, so that every check below runs against a RE-USED
            // window — which is the only kind this app ever has.
            toast.ShowError(firstFailure);
            await ToastSettledAsync(toast);

            // BOTH configurations of the row, because the Open Settings button is what makes the
            // budget tight and the two cases fail differently. Measured: the button takes 82px of
            // a 334px row, so the message is offered 191px beside it and 273px without it. A
            // MaxWidth of 200 — the exact cap #669 was — therefore clips NOTHING in the first case
            // and a third of the sentence in the second. A check that only ever looked at the
            // button case would pass the bug straight through. The Windows suite loops the
            // equivalent pair on the status bar, and #526's check above does the same.
            foreach (var (label, action) in new[]
                     { ("notice only", LinuxErrorToastAction.None),
                       ("with Open Settings", LinuxErrorToastAction.ApiKeys) })
            {
                toast.DismissImmediately();
                toast.ShowError(failure, action);
                await ToastSettledAsync(toast);
                if (ErrorToastMessageFailure(toast, failure, label, action)) return true;
            }

            // PLACEMENT, with no overlay on screen yet: the fallback branch, which sits the toast
            // FallbackBottomMarginDip above the bottom of the work area. Measured against the
            // height of THIS message, which is what a re-used window gets wrong.
            if ((toast.Screens.ScreenFromWindow(toast) ?? toast.Screens.Primary) is not { } screen)
            {
                Console.Error.WriteLine("Smoke: the toast is on no screen, so its placement cannot "
                    + "be measured.");
                return true;
            }
            var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
            var work = screen.WorkingArea.Width > 0 && screen.WorkingArea.Height > 0
                ? screen.WorkingArea : screen.Bounds;

            // Can a window be placed on this platform AT ALL? Under Weston — the compositor
            // run-ui-smoke-xwayland.sh runs, and the second of the two legs CI runs — the
            // compositor places X11 toplevels itself and discards the client's request: it centres
            // this toast on the output, 205px from where it asked to be. Wayland gives a client no
            // say over its own toplevel's position, so that is a platform fact and not something
            // the toast can fix or work around.
            //
            // So prove positioning is honoured BEFORE asserting anything that rests on it, with a
            // point no placement rule here would produce. A check that CANNOT run is reported as
            // exactly that: it is not a pass, and it is not a failure of the code under test.
            //
            // A window that never stopped moving must NOT reach that conclusion: it reads as "the
            // position is not what was asked for" and would skip the placement half on a platform
            // that does honour placement, silently, and only on the loaded runs. A timeout is a
            // failure of this check, not a property of the compositor.
            var poke = new PixelPoint(work.X + 17, work.Y + 23);
            toast.Position = poke;
            if (!await ToastSettledAsync(toast))
            {
                Console.Error.WriteLine($"Smoke: the toast was still moving when the settle budget "
                    + $"ran out — it is at {toast.Position} having asked for {poke}, which cannot "
                    + "be told apart from a compositor that places windows itself. Failing rather "
                    + "than skipping the placement checks on a guess.");
                return true;
            }
            var placeable = toast.Position == poke;
            if (!placeable)
            {
                Console.Error.WriteLine($"Smoke: this compositor put the toast at {toast.Position} "
                    + $"when it asked for {poke}, so it places windows itself and the toast's "
                    + "placement is not the app's to measure here. The readability checks above "
                    + "ran; the placement checks are skipped.");
                return false;
            }

            // The stale-height case, set up in the order that produces it and measured at the one
            // moment it is visible: settle the window on a ONE-LINE failure, then hand it the long
            // one. The height it is placed with must be the height it has now, not the height it
            // had a moment ago. Anything that re-shows the long message first — including the poke
            // above — sizes the window for it in advance and quietly stops testing this at all.
            toast.DismissImmediately();
            toast.ShowError(firstFailure);
            await ToastSettledAsync(toast);
            var shortHeight = toast.Bounds.Height;
            toast.DismissImmediately();
            toast.ShowError(failure, LinuxErrorToastAction.ApiKeys);
            await ToastSettledAsync(toast);

            var height = (int)Math.Round(toast.Bounds.Height * scale);
            if (toast.Bounds.Height <= shortHeight)
            {
                Console.Error.WriteLine($"Smoke: the long failure made the toast "
                    + $"{toast.Bounds.Height:F1}px against the one-line failure's {shortHeight:F1}px, "
                    + "so a stale height would be indistinguishable and this check proves nothing.");
                return true;
            }
            var expected = Math.Max(work.Y, work.Bottom - height - (int)Math.Round(80 * scale));
            if (Math.Abs(toast.Position.Y - expected) > 1)
            {
                Console.Error.WriteLine($"Smoke: the toast sits at y={toast.Position.Y} where a "
                    + $"{height}px toast belongs at y={expected} — it was placed with a height that "
                    + "is not this message's.");
                return true;
            }

            // PLACEMENT against an overlay dragged to the very top of the work area, which the
            // user can do: the overlay's position is persisted as a ratio clamped to [0,1], so 0
            // puts it at exactly work.Y. Without a clamp the toast lands entirely off the top of
            // the screen and the failure is never seen at all.
            anchor = new LinuxRecordingOverlayWindow(new LinuxRecordingOverlayViewModel(),
                new InMemoryOverlayPlacementStore());
            anchor.Show();
            anchor.Position = new PixelPoint(work.X, work.Y);
            await ToastSettledAsync(anchor);
            toast.DismissImmediately();
            toast.ShowError(failure, LinuxErrorToastAction.ApiKeys);
            await ToastSettledAsync(toast);
            height = (int)Math.Round(toast.Bounds.Height * scale);
            // Where the toast would land with no clamp. That it is off screen is ARITHMETIC, not
            // something to assert: the anchor is at work.Y, and the gate above already proved this
            // message is taller than the one-line one, so this is work.Y less a positive height
            // less the gap. An earlier form of this guard asserted it anyway, as a second
            // disjunct — which could only be reached when the first disjunct was false, i.e. when
            // anchor.Position.Y == work.Y, which is precisely when the arithmetic makes it
            // impossible. It never fired, and a clause that cannot fire is not evidence that the
            // clamp is load-bearing. The value is printed below instead, where it says something.
            var unclamped = anchor.Position.Y - height - (int)Math.Round(12 * scale);
            // What genuinely is NOT settled: whether the anchor went where it was put. The poke
            // above proves this platform honours a position for the TOAST; the overlay is a
            // different window, with its own placement restore on Opened, so it is checked here.
            if (anchor.Position.Y != work.Y)
            {
                Console.Error.WriteLine($"Smoke: the anchor overlay was put at y={work.Y}, the top "
                    + $"of the work area, and settled at y={anchor.Position.Y} — the overlay is not "
                    + "where this case needs it, so nothing below is a measurement of the clamp.");
                return true;
            }
            if (toast.Position.Y != work.Y)
            {
                Console.Error.WriteLine($"Smoke: with the overlay at the top of the work area the "
                    + $"toast was placed at y={toast.Position.Y}, not clamped to y={work.Y} — "
                    + $"unclamped it belongs at y={unclamped}, so a failure shown here is off the "
                    + "top of the screen and never seen.");
                return true;
            }

            // PLACEMENT against an overlay on a monitor ABOVE this one, which PlaceOnScreen
            // deliberately does NOT clamp: such an overlay has a legitimately negative Y, and
            // Windows leaves it alone rather than dragging the toast down onto this screen.
            //
            // This is the case the clamp above destroys if the clamp is applied unconditionally to
            // every Position write instead of to the targets that asked for one. It needs no
            // second monitor to drive: the branch turns on the anchor being above work.Y, not on
            // Screens.ScreenCount. Without the distinction the toast is forced to work.Y with its
            // X still centred over an overlay that is not on this screen — and because it can then
            // never leave, the next placement re-reads this screen's top and pins it there.
            var above = new PixelPoint(work.X, work.Y - 400);
            anchor.Position = above;
            await ToastSettledAsync(anchor);
            if (anchor.Position != above)
            {
                Console.Error.WriteLine($"Smoke: the anchor overlay asked to sit at {above}, above "
                    + $"the work area, and settled at {anchor.Position} — this platform will not "
                    + "put a window above the work area, so the other-monitor case cannot be set "
                    + "up here.");
                return true;
            }
            toast.DismissImmediately();
            toast.ShowError(failure, LinuxErrorToastAction.ApiKeys);
            await ToastSettledAsync(toast);
            height = (int)Math.Round(toast.Bounds.Height * scale);
            // 12px above the anchor, exactly as on this screen. The anchor is 400px above work.Y
            // and the toast is taller than nothing, so this is unambiguously above work.Y and a
            // clamp to work.Y cannot produce it.
            var expectedAbove = anchor.Position.Y - height - (int)Math.Round(12 * scale);
            if (toast.Position.Y != expectedAbove)
            {
                Console.Error.WriteLine($"Smoke: the overlay sits {work.Y - anchor.Position.Y}px "
                    + $"above the work area and the toast was placed at y={toast.Position.Y}, not "
                    + $"at y={expectedAbove} — the deliberate skip of the top clamp for an anchor "
                    + "on another monitor is being overruled"
                    + $"{(toast.Position.Y == work.Y ? $" by a clamp to y={work.Y}" : "")}, so the "
                    + "toast is stranded on this screen centred over an overlay that is not on it.");
                return true;
            }
            return false;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Smoke: the error toast check threw: {exception}");
            return true;
        }
        finally
        {
            try { toast.DismissImmediately(); toast.Close(); } catch { }
            // Dispose, not Close. Moving the anchor fires PositionChanged -> SavePosition ->
            // SaveDebounced, which arms a 350ms timer; only LinuxRecordingOverlayWindow's
            // IDisposable tears that down, and it Closes the window itself. A bare Close leaves
            // the timer to flush a placement for a window that is already gone.
            try { (anchor as IDisposable)?.Dispose(); } catch { }
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
    }

    /// <summary>
    /// PART_TextBox is the content of the templated ButtonSpinner, so it reaches the visual tree
    /// on a layout pass and not on ApplyTemplate alone. One render yield is enough on an idle
    /// machine and is not always enough on a contended runner — CI runs this smoke twice, under
    /// xvfb-run with no window manager — so yield a few times before giving up. The caller then
    /// reports "the probe could not run" rather than "#692 is back".
    /// </summary>
    /// <summary>
    /// The three controls the port probe drives, once they are all on the page. Retried for the
    /// same reason the templated text box is: the Local API page is a DataTemplate and one Render
    /// yield is not a promise that it has been built, and a lookup that loses that race must not
    /// be reported as the #692 regression.
    /// </summary>
    private async Task<(NumericUpDown Port, StackPanel Tabs, CheckBox Elsewhere)?> LocalApiPortRowAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (FindNamed<NumericUpDown>("SettingsLocalApiPort") is { } port
                && FindNamed<StackPanel>("LocalApiTabsRoot") is { } tabs
                && FindNamed<CheckBox>("SettingsLocalApiEnabled") is { } elsewhere)
                return (port, tabs, elsewhere);
        }
        return null;
    }

    private static async Task<TextBox?> TemplatedPortBoxAsync(NumericUpDown port)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            port.ApplyTemplate();
            if (port.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } box) return box;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        return null;
    }

    /// <summary>
    /// Puts LocalApiTabsRoot back where its IsVisible binding wants it. SetCurrentValue, not
    /// ClearValue: the probe's override has to be undone by writing the bound value, because a
    /// cleared override leaves the last value the property held until the source next changes.
    /// </summary>
    private static void RestoreLocalApiTabs(StackPanel tabs, bool enabled)
        => tabs.SetCurrentValue(Visual.IsVisibleProperty, enabled);

    /// <summary>One typed prefix per render tick, so every partial number gets the chance to
    /// commit that it had before #692 was fixed.</summary>
    private static async Task TypePortAsync(TextBox box, params string[] steps)
    {
        foreach (var typed in steps)
        {
            box.Text = typed;
            box.CaretIndex = typed.Length;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
    }

    /// <summary>
    /// Drives the REAL Settings -> Local API preferred-port field through every way a person can
    /// finish an edit, and asks the two questions #692 answered wrongly: how many times did
    /// Settings.LocalApiPort move while the digits were going in, and does the field agree with
    /// the port that was committed once the edit is done?
    ///
    /// Typed into the templated TextBox and finished with a REAL focus move, a REAL Enter key and
    /// a REAL spin, never by assigning Value: the whole fault lives in NumericUpDown's own text
    /// path — ConvertTextToValue throws through ValidateMinMax into a swallowing catch — and a
    /// direct Value assignment never reaches it. Counting SOURCE property changes rather than
    /// server restarts keeps the check off the wall clock: the restart chain hangs off
    /// Settings.LocalApiPort through a 500ms auto-save timer, so the property is the cause and the
    /// restart is only its consequence.
    ///
    /// The starting port is set explicitly. On a profile that already stored 65535 every write
    /// below would land on the value already held, ViewModelBase.Set would raise nothing, and a
    /// counter-based check would pass without the fix.
    ///
    /// Nothing here may outlive the probe on the developer's own profile. The settings listener is
    /// detached for the duration, so no probe write arms the 500ms auto-save; a save that was
    /// already armed is stopped so it cannot tick mid-probe and persist the test port, and it is
    /// re-armed on the way out. Step 8 does flush ONE real save, because that is the half of the
    /// close path the issue is about and nothing else can prove it runs; the window's own settings
    /// fan-out is detached around it so the flush cannot restart a live Local API host on the
    /// probe's port, and the original port is saved back before the fan-out returns.
    ///
    /// Returns 0 when the field behaves and 26 for the #692 regression, which is only ever
    /// returned by an explicit check with its own message. Everything else — a control torn down
    /// mid-run, a page that never appeared, a save its own validation refused — returns 27, "the
    /// probe could not run". An environment fault must never read as the regression.
    /// </summary>
    private async Task<int> LocalApiPortCommitFailureAsync()
    {
        const int regression = 26;
        const int cannotRun = 27;
        const int start = 51671;

        _viewModel.Navigate("localapi");
        if (await LocalApiPortRowAsync() is not { } row)
        {
            Console.Error.WriteLine("Smoke: the Local API preferred-port row never appeared on the "
                + "page, so the port commit could not be measured. Setup failure, not the #692 "
                + "regression.");
            return cannotRun;
        }
        var (port, tabs, elsewhere) = row;

        var settings = _viewModel.Settings;
        var original = settings.LocalApiPort;
        var enabled = settings.LocalApiEnabled;
        var saveWasArmed = _settingsAutoSave is { IsEnabled: true };
        var commits = 0;
        var saves = 0;
        void CountCommit(object? sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, nameof(SettingsViewModel.LocalApiPort), StringComparison.Ordinal))
                commits++;
        }
        void CountSave(object? sender, EventArgs e) => saves++;

        _settingsAutoSave?.Stop();
        settings.PropertyChanged -= OnSettingsPropertyChanged;
        // Step 8 flushes a REAL save, because that is the half of the close path #692 is about and
        // the only way to prove it runs. The window's own fan-out is detached while it does: the
        // flush raises LocalApiSettingsChanged, and on a profile with the server enabled that
        // would restart the live host on the probe's port and rewrite local-api.json.
        settings.LocalApiSettingsChanged -= OnLocalApiSettingsChanged;
        settings.DesktopSettingsChanged -= OnDesktopSettingsChanged;
        settings.TelemetrySettingsChanged -= OnTelemetrySettingsChanged;
        settings.StorageSettingsChanged -= OnStorageSettingsChanged;
        settings.TelemetrySettingsChanged += CountSave;
        try
        {
            // SetCurrentValue, not a plain assignment: a local value REPLACES the IsVisible
            // binding to Settings.LocalApiEnabled, and ClearValue does not put a replaced binding
            // back — the Local API body would stay expanded for the rest of the smoke run. An
            // invisible control cannot take focus, so the body has to be open for this probe.
            tabs.SetCurrentValue(Visual.IsVisibleProperty, true);
            settings.LocalApiPort = start;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (await TemplatedPortBoxAsync(port) is not { } box)
            {
                Console.Error.WriteLine("Smoke: the preferred-port field never grew its templated text "
                    + "box, so nothing could be typed into it. Setup failure, not the #692 regression.");
                return cannotRun;
            }
            if (port.GetVisualDescendants().OfType<Spinner>().FirstOrDefault() is not { } spinner)
            {
                Console.Error.WriteLine("Smoke: the preferred-port field has no templated spinner, so "
                    + "the spin path cannot be driven. Setup failure, not the #692 regression.");
                return cannotRun;
            }
            box.Focus();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!box.IsFocused)
            {
                Console.Error.WriteLine("Smoke: the preferred-port field would not take focus, so a "
                    + "commit-on-blur check cannot run. Setup failure, not the #692 regression.");
                return cannotRun;
            }

            settings.PropertyChanged += CountCommit;

            // 1. The issue's own walk: Ctrl+A then 70000, one digit at a time. Nothing may reach
            //    the source until the edit is finished, and what lands must be the clamped port,
            //    in the box as well as behind it.
            var commitCalls = _localApiPortCommits;
            await TypePortAsync(box, "7", "70", "700", "7000", "70000");
            if (commits != 0)
            {
                Console.Error.WriteLine($"Smoke: typing 70000 into the preferred port moved "
                    + $"Settings.LocalApiPort {commits} time(s) before the edit was finished — it is "
                    + $"now {settings.LocalApiPort}. Every move can restart the Local API server on a "
                    + "partial number and rewrite local-api.json with a port that is already dead.");
                return regression;
            }
            elsewhere.Focus();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (settings.LocalApiPort != 65535 || port.Value != 65535m || box.Text != "65535")
            {
                Console.Error.WriteLine($"Smoke: 70000 was committed as port {settings.LocalApiPort} "
                    + $"with the field reading '{box.Text}' (Value={port.Value}) — expected 65535 "
                    + "everywhere. The field and the running server disagree, which is the silent "
                    + "truncation #692 reported.");
                return regression;
            }
            if (commits != 1)
            {
                Console.Error.WriteLine($"Smoke: committing 70000 moved Settings.LocalApiPort "
                    + $"{commits} time(s), not once.");
                return regression;
            }
            // ...and ONE commit, not one visible commit. Two handlers see the same bubbling
            // LostFocus — the one on the templated text box and the one on the NumericUpDown — and
            // a second commit of the same number moves nothing, raises no PropertyChanged and is
            // invisible to the counter above. Counting the calls is what makes the commit's
            // idempotence a tested promise instead of a silent requirement.
            if (_localApiPortCommits - commitCalls != 1)
            {
                Console.Error.WriteLine($"Smoke: one blur out of the preferred port ran "
                    + $"CommitLocalApiPort {_localApiPortCommits - commitCalls} time(s), not once. "
                    + "Every non-idempotent step ever added to the commit — a clamp message, a "
                    + "counter, an explicit restart — would fire that many times per focus change.");
                return regression;
            }

            // 2. Again, now that the stored port IS the ceiling. This is the case that decides
            //    WHERE the clamp lives. Leaning on SettingsViewModel.LocalApiPort's own Math.Clamp
            //    instead — by raising Maximum above 65535 and letting 70000 reach the setter —
            //    looks identical above. It is not identical here: the setter clamps 70000 to the
            //    65535 already stored, ViewModelBase.Set raises no PropertyChanged for a value
            //    that did not move, nothing comes back down the binding, and the field is left
            //    reading 70000 over a server bound to 65535 — #692 in a narrower window.
            box.Focus();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await TypePortAsync(box, "7", "70", "700", "7000", "70000");
            elsewhere.Focus();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (port.Value != 65535m || box.Text != "65535" || settings.LocalApiPort != 65535 || commits != 1)
            {
                Console.Error.WriteLine($"Smoke: retyping 70000 over a stored port of 65535 left the "
                    + $"field reading '{box.Text}' (Value={port.Value}) against a stored port of "
                    + $"{settings.LocalApiPort} after {commits} change(s) — a clamp that changes "
                    + "nothing must still put the corrected number back on screen.");
                return regression;
            }

            // 3. Enter finishes an edit. A binding trigger cannot see it: NumericUpDown's own
            //    Return handling syncs Value from Text and stops there, so with
            //    UpdateSourceTrigger=LostFocus the field showed the new port while the server
            //    stayed on the old one until focus happened to move.
            settings.LocalApiPort = start;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            box.Focus();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await TypePortAsync(box, "6", "60", "600", "6000", "60000");
            if (commits != 2)
            {
                Console.Error.WriteLine($"Smoke: typing 60000 moved Settings.LocalApiPort before "
                    + $"Enter was pressed ({commits} change(s) in all, expected 2).");
                return regression;
            }
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Return });
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (settings.LocalApiPort != 60000 || box.Text != "60000" || commits != 3)
            {
                Console.Error.WriteLine($"Smoke: Enter left the preferred port on "
                    + $"{settings.LocalApiPort} with the field reading '{box.Text}' after "
                    + $"{commits} change(s) — expected 60000, committed there and then.");
                return regression;
            }
            elsewhere.Focus();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);

            // 4. A negative entry is REFUSED, not clamped. Clamping the low end lands on port 0,
            //    which the Local API does not treat as a refused port: it passes 0 straight to
            //    Listen, takes a random ephemeral port and rewrites local-api.json on every
            //    launch. Before #692 the control refused the parse and kept the stored port; a
            //    stray minus sign must not buy a different feature.
            box.Focus();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await TypePortAsync(box, "-", "-1");
            elsewhere.Focus();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (settings.LocalApiPort != 60000 || box.Text != "60000" || commits != 3)
            {
                Console.Error.WriteLine($"Smoke: entering -1 left the preferred port on "
                    + $"{settings.LocalApiPort} with the field reading '{box.Text}' after "
                    + $"{commits} change(s) — expected the stored 60000 back, refused. Port 0 is "
                    + "not a refused port: it binds a random one on every launch and breaks every "
                    + "MCP client pinned to the old number.");
                return regression;
            }

            // 5. The spinner finishes an edit too, and it never touches the text, so the text
            //    path above cannot see it. It must reach the source AND leave an integer in the
            //    box — a decimal point left under the caret is the push-back failing silently.
            box.Focus();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            spinner.RaiseEvent(new SpinEventArgs(Spinner.SpinEvent, SpinDirection.Increase));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (settings.LocalApiPort != 60001 || box.Text != "60001" || port.Value != 60001m || commits != 4)
            {
                Console.Error.WriteLine($"Smoke: one click of the preferred-port spinner left the "
                    + $"port on {settings.LocalApiPort} with the field reading '{box.Text}' "
                    + $"(Value={port.Value}) after {commits} change(s) — expected 60001 everywhere.");
                return regression;
            }

            // 6. Focus moving INSIDE the field is not a finished edit. LostFocus is raised on the
            //    text box and bubbles, so it also fires when the user clicks the spinner buttons a
            //    few pixels away to carry on editing. Restoring the stored port there wrote it
            //    back over a box the user had just cleared to retype.
            box.Focus();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            box.Text = string.Empty;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (spinner.GetVisualDescendants().OfType<Button>().FirstOrDefault() is { } increase
                && increase.Focus())
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                if (box.Text?.Length > 0 || commits != 4)
                {
                    Console.Error.WriteLine($"Smoke: clicking into the preferred-port spinner while "
                        + $"the box was cleared refilled it with '{box.Text}' after {commits} "
                        + "change(s) — a focus move that stays inside the field is not a finished "
                        + "edit, and the next spin then counts from the old port.");
                    return regression;
                }
            }

            // 7. ...but leaving the field with an empty box IS a finished edit, and empty is not a
            //    port. The stored one has to come back, or the box sits blank over a live server.
            elsewhere.Focus();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (box.Text != "60001" || port.Value != 60001m || settings.LocalApiPort != 60001 || commits != 4)
            {
                Console.Error.WriteLine($"Smoke: emptying the preferred port left the field reading "
                    + $"'{box.Text}' (Value={port.Value}) against a stored port of "
                    + $"{settings.LocalApiPort} after {commits} change(s) — expected the stored "
                    + "60001 back in the box, committed nowhere.");
                return regression;
            }

            // 8. Quitting with the caret still in the field. This is the path a binding trigger
            //    cannot reach at all: there is no focus change, so the source never sees the port
            //    and OnClosing shuts the host down on the old one. CommitPendingSettingsEdits is
            //    what OnClosing calls, and it also flushes the debounced save that the commit
            //    itself arms — a save that can never tick, because the window is going.
            box.Focus();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await TypePortAsync(box, "9", "90", "900", "9000");
            if (commits != 4)
            {
                Console.Error.WriteLine($"Smoke: typing 9000 moved Settings.LocalApiPort before the "
                    + $"edit was finished ({commits} change(s) in all, expected 4).");
                return regression;
            }
            //    The save is ARMED first, the way a real edit arms it. Without that the flush half
            //    of CommitPendingSettingsEdits returns at its own `IsEnabled` guard and this step
            //    measures only that the port reached the view model: the flush could be deleted
            //    with every gate still green, and a port typed at quit would reach the view model
            //    and never be written to disk — the original complaint, one layer down.
            OnSettingsPropertyChanged(settings, new PropertyChangedEventArgs(nameof(SettingsViewModel.LocalApiPort)));
            if (_settingsAutoSave is not { IsEnabled: true })
            {
                Console.Error.WriteLine("Smoke: the settings auto-save would not arm, so the close "
                    + "path's flush cannot be measured. Setup failure, not the #692 regression.");
                return cannotRun;
            }
            CommitPendingSettingsEdits();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (saves == 0 && settings.Status.HasError)
            {
                Console.Error.WriteLine("Smoke: the flushed save was refused by its own validation "
                    + $"({settings.Status.ErrorCode}), so the close path could not be measured. "
                    + "Setup failure, not the #692 regression.");
                return cannotRun;
            }
            if (settings.LocalApiPort != 9000 || commits != 5 || saves != 1
                || _settingsAutoSave is not { IsEnabled: false })
            {
                Console.Error.WriteLine($"Smoke: closing the window with 9000 still under the caret "
                    + $"left the preferred port on {settings.LocalApiPort} after {commits} "
                    + $"change(s) and {saves} save(s), with the debounced save "
                    + $"{(_settingsAutoSave is { IsEnabled: true } ? "still armed" : "stopped")} — "
                    + "a typed port must survive quitting the app, and the 500ms save the commit "
                    + "arms can never tick once the window has gone.");
                return regression;
            }

            // 9. The field is still BOUND after all of that: the write-backs went through the
            //    binding (SetCurrentValue) rather than replacing it with a local value.
            elsewhere.Focus();
            settings.LocalApiPort = start;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (port.Value != start)
            {
                Console.Error.WriteLine($"Smoke: after the edits above the preferred-port field reads "
                    + $"{port.Value} while the view model holds {settings.LocalApiPort} — a "
                    + "write-back replaced the binding instead of going through it.");
                return regression;
            }

            // 10. The spin again, and now on an entry the control REFUSED. This is #692 itself,
            //     alive through the one path the text handlers cannot see, and it is why the spin
            //     commits what was TYPED rather than Value: NumericUpDown swallows "70000"
            //     (ConvertTextToValue throws through ValidateMinMax into a catch), leaves Value on
            //     the last in-range prefix 7000, and a spin then committed 7000 — the exact port
            //     the issue reported binding — while Enter and blur on the same text clamped to
            //     65535. Raised on the spinner, which is the entry point the DISABLED buttons do
            //     not gate: ButtonSpinner.OnPointerWheelChanged reaches it straight from the mouse
            //     wheel. Step 12 drives the button click, which is gated and fails differently.
            box.Focus();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await TypePortAsync(box, "7", "70", "700", "7000", "70000");
            if (commits != 6)
            {
                Console.Error.WriteLine($"Smoke: typing 70000 before a spin moved "
                    + $"Settings.LocalApiPort ({commits} change(s) in all, expected 6).");
                return regression;
            }
            spinner.RaiseEvent(new SpinEventArgs(Spinner.SpinEvent, SpinDirection.Increase));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (settings.LocalApiPort != 65535 || box.Text != "65535" || port.Value != 65535m || commits != 7)
            {
                Console.Error.WriteLine($"Smoke: clicking the spinner after typing 70000 committed "
                    + $"port {settings.LocalApiPort} with the field reading '{box.Text}' "
                    + $"(Value={port.Value}) after {commits} change(s) — expected 65535 everywhere. "
                    + "The swallowed prefix is what #692 reported binding, and the spinner is the "
                    + "one way of finishing an edit that can still reach it.");
                return regression;
            }

            // 11. ...and the same click after a NEGATIVE entry must not land on port 1. Clamping
            //     the low end would spin "-1" up from a committed port 0; the refusal has to
            //     survive the spin exactly as it survives a blur, so the stored port comes back.
            box.Focus();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await TypePortAsync(box, "-", "-1");
            spinner.RaiseEvent(new SpinEventArgs(Spinner.SpinEvent, SpinDirection.Increase));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (settings.LocalApiPort != 65535 || box.Text != "65535" || port.Value != 65535m || commits != 7)
            {
                Console.Error.WriteLine($"Smoke: clicking the spinner after typing -1 left the "
                    + $"preferred port on {settings.LocalApiPort} with the field reading "
                    + $"'{box.Text}' (Value={port.Value}) after {commits} change(s) — expected the "
                    + "stored 65535 back, refused. Port 0 and port 1 are both different features "
                    + "from the port the user was editing.");
                return regression;
            }

            // 12. And the click on the spinner BUTTONS, which is a different path again. The
            //     control disables those buttons while it is refusing UI text
            //     (ButtonSpinner.SetButtonUsage), so that click cannot spin at all — but it still
            //     moves focus out of the text box, and NumericUpDown then rewrites the box from
            //     the prefix it kept. Measured on a real screen at e137b474: typing 70000 over a
            //     stored 51671 and clicking the up arrow left the field reading "7000" over a
            //     server still bound to 51671, committed nowhere. Driven here as what it is, a
            //     focus move to something inside the field.
            settings.LocalApiPort = start;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            box.Focus();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await TypePortAsync(box, "7", "70", "700", "7000", "70000");
            var movedInside = spinner.Focus()
                || (spinner.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.IsEnabled)
                    is { } spinButton && spinButton.Focus());
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!movedInside || !port.IsKeyboardFocusWithin)
            {
                Console.Error.WriteLine("Smoke: nothing inside the preferred-port field would take "
                    + "focus away from its text box, so a click on the spinner cannot be driven. "
                    + "Setup failure, not the #692 regression.");
                return cannotRun;
            }
            if (settings.LocalApiPort != 65535 || box.Text != "65535" || commits != 9)
            {
                Console.Error.WriteLine($"Smoke: clicking the spinner with 70000 under the caret "
                    + $"left the preferred port on {settings.LocalApiPort} with the field reading "
                    + $"'{box.Text}' after {commits} change(s) — expected 65535 in both. A refused "
                    + "entry is about to be rewritten from the prefix the control kept, so it has "
                    + "to commit on the way out of the box, not be left on screen over a server "
                    + "bound to something else.");
                return regression;
            }

            // 13. And the probe leaves the page as it found it. The Local API body was opened to
            //     get at the field — an invisible control cannot take focus — and putting it back
            //     is not free: a plain assignment overrides the IsVisible binding to
            //     Settings.LocalApiEnabled, and ClearValue does NOT restore it, so the tab strip,
            //     the connection rows, the token, the discovery path and both snippets would stay
            //     on screen for the rest of the smoke run. Asserted here rather than left to the
            //     finally below, which only repeats the same restore as a safety net.
            RestoreLocalApiTabs(tabs, enabled);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (tabs.IsVisible != enabled)
            {
                Console.Error.WriteLine($"Smoke: the port probe left the Local API body "
                    + $"{(tabs.IsVisible ? "expanded" : "collapsed")} against a server that is "
                    + $"{(enabled ? "on" : "off")} — every panel it opened is still on screen for "
                    + "the rest of the run.");
                return regression;
            }
            return 0;
        }
        catch (Exception exception)
        {
            // NOT the regression code. Every #692 regression this probe can prove is proved by an
            // explicit check above, each with its own message; an exception here is a control torn
            // down mid-run, a dispatcher shutting down, a focus call on a window that lost its
            // X connection. Reporting those as "#692 is back" reddens unrelated pull requests with
            // a sentence that is not true.
            Console.Error.WriteLine($"Smoke: the Local API port check could not finish: {exception} "
                + "Setup failure, not the #692 regression.");
            return cannotRun;
        }
        finally
        {
            settings.PropertyChanged -= CountCommit;
            settings.TelemetrySettingsChanged -= CountSave;
            settings.LocalApiPort = original;
            RestoreLocalApiTabs(tabs, enabled);
            // Step 8 flushed a REAL save, so the profile on disk is holding the probe's port.
            // Put the original back the same way, while the window's own fan-out is still
            // detached, so nothing restarts on a port this probe invented.
            if (saves > 0 && settings.SaveCommand.CanExecute(null)) settings.SaveCommand.Execute(null);
            settings.LocalApiSettingsChanged += OnLocalApiSettingsChanged;
            settings.DesktopSettingsChanged += OnDesktopSettingsChanged;
            settings.TelemetrySettingsChanged += OnTelemetrySettingsChanged;
            settings.StorageSettingsChanged += OnStorageSettingsChanged;
            settings.PropertyChanged += OnSettingsPropertyChanged;
            // Give back the save this probe interrupted. Its own writes are not saved: the
            // listener was detached while they happened and the original port is back.
            _settingsAutoSave?.Stop();
            if (saveWasArmed) _settingsAutoSave?.Start();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
    }

    /// <summary>
    /// Issue #653: a failed Copy left its red line under the status card after the server was
    /// turned off, which hides every button that line can report on, and again after it was turned
    /// back on. This puts a real failure report on the line through the Copy path, flips the
    /// page's own checkbox off and on, and reads the line after each. 28 is the regression; 29
    /// says the probe could not be set up.
    /// </summary>
    private async Task<int> LocalApiActionStatusClearFailureAsync()
    {
        const int regression = 28;
        const int cannotRun = 29;

        _viewModel.Navigate("localapi");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        if (FindNamed<TextBlock>("LocalApiActionStatus") is not { } line
            || FindNamed<CheckBox>("SettingsLocalApiEnabled") is not { } toggle)
        {
            Console.Error.WriteLine("Smoke: the Local API action line or the server checkbox is "
                + "missing from the page. Setup failure, not the #653 regression.");
            return cannotRun;
        }

        var settings = _viewModel.Settings;
        var enabled = settings.LocalApiEnabled;
        var saveWasArmed = _settingsAutoSave is { IsEnabled: true };
        // A flip of the checkbox is a settings change, and an armed auto-save would answer it with
        // a real save that starts or stops the live host. Only the line is under test here.
        _settingsAutoSave?.Stop();
        settings.PropertyChanged -= OnSettingsPropertyChanged;
        settings.LocalApiSettingsChanged -= OnLocalApiSettingsChanged;
        try
        {
            // The Copy path's own failure report: no port to copy is the one failure that does not
            // depend on which clipboard helper this box has.
            async Task<bool> ShowFailureAsync()
            {
                await CopyLocalApiTextAsync(null, "settings.localApi.action.portUnavailable");
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                return line.IsVisible && !string.IsNullOrEmpty(line.Text);
            }
            async Task<bool> FlipAsync(bool value)
            {
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, value);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                return settings.LocalApiEnabled == value;
            }

            if (!await ShowFailureAsync())
            {
                Console.Error.WriteLine("Smoke: a Copy with nothing to copy put no report on the Local "
                    + "API action line. Setup failure, not the #653 regression.");
                return cannotRun;
            }
            foreach (var value in new[] { !enabled, enabled })
            {
                if (!await FlipAsync(value))
                {
                    Console.Error.WriteLine("Smoke: setting the server checkbox did not reach "
                        + "Settings.LocalApiEnabled. Setup failure, not the #653 regression.");
                    return cannotRun;
                }
                if (line.IsVisible || !string.IsNullOrEmpty(line.Text))
                {
                    Console.Error.WriteLine($"Smoke: after the server was turned {(value ? "on" : "off")} "
                        + $"the Local API action line still read '{line.Text}' (visible: {line.IsVisible}). "
                        + "A change of server state must start on a clean line (#653).");
                    return regression;
                }
            }
            if (!await ShowFailureAsync())
            {
                Console.Error.WriteLine("Smoke: after the server was turned off and on, a Copy put "
                    + "no report on the Local API action line — the clear must not silence the next "
                    + "action (#653).");
                return regression;
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Smoke: the Local API action line check could not finish: {exception} "
                + "Setup failure, not the #653 regression.");
            return cannotRun;
        }
        finally
        {
            if (settings.LocalApiEnabled != enabled) settings.LocalApiEnabled = enabled;
            line.Text = string.Empty;
            line.IsVisible = false;
            settings.LocalApiSettingsChanged += OnLocalApiSettingsChanged;
            settings.PropertyChanged += OnSettingsPropertyChanged;
            _settingsAutoSave?.Stop();
            if (saveWasArmed) _settingsAutoSave?.Start();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
    }

    /// <summary>
    /// Issue #643: after Reveal the button read Hide, but the token row stayed one trimmed line, so
    /// the end of the token was an ellipsis and the value could not be read off the page. This
    /// turns the server on for real, presses the real Reveal button, and MEASURES the token block
    /// in the real window: its arranged height must hold the whole token wrapped at its arranged
    /// width. 30 is the regression; 31 says the probe could not be set up.
    /// </summary>
    private async Task<int> LocalApiRevealedTokenFailureAsync()
    {
        const int regression = 30;
        const int cannotRun = 31;

        var settings = _viewModel.Settings;
        var enabled = settings.LocalApiEnabled;
        var saveWasArmed = _settingsAutoSave is { IsEnabled: true };
        var page = _currentPageId;
        // The server is started and stopped by this probe directly, so neither an armed auto-save
        // nor the settings listener may start a second host behind it.
        _settingsAutoSave?.Stop();
        settings.PropertyChanged -= OnSettingsPropertyChanged;
        settings.LocalApiSettingsChanged -= OnLocalApiSettingsChanged;
        try
        {
            settings.LocalApiEnabled = true;
            await ApplyLocalApiSettingsAsync(_lifetime.Token);
            // Through the sidebar's own entry point, so the Settings sub-navigation takes its
            // column exactly as it does for a user; a bare Navigate leaves it out and widens the row.
            GoTo("localapi");
            _localApiTokenRevealed = false;
            UpdateLocalApiConnectionUi();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (TryReadLocalApiToken() is not { Length: > 0 } token
                || FindNamed<TextBlock>("SettingsLocalApiToken") is not { IsEffectivelyVisible: true } block
                || FindNamed<Button>("SettingsLocalApiReveal") is not { IsEffectivelyVisible: true } reveal)
            {
                Console.Error.WriteLine("Smoke: with the Local API server on, the page shows no token "
                    + "row or the host has no token. Setup failure, not the #643 regression.");
                return cannotRun;
            }

            reveal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!_localApiTokenRevealed || !string.Equals(block.Text, token, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Smoke: pressing Reveal did not put the token in the row. "
                    + "Setup failure, not the #643 regression.");
                return cannotRun;
            }
            // The check is only evidence when the token is wider than its row on one line, which
            // is the case at the smoke window's size; otherwise it would pass on any layout.
            var oneLine = TextProbe(block, double.PositiveInfinity).Width;
            if (oneLine <= block.Bounds.Width)
            {
                Console.Error.WriteLine($"Smoke: the {token.Length}-character token ({oneLine:F1}px) fits its "
                    + $"{block.Bounds.Width:F1}px row on one line, so nothing can be cut. Setup failure, "
                    + "not the #643 regression.");
                return cannotRun;
            }
            var needed = TextProbe(block, block.Bounds.Width);
            if (block.Bounds.Height + 0.5 < needed.Height)
            {
                Console.Error.WriteLine($"Smoke: the revealed Local API token is cut: its row is "
                    + $"{block.Bounds.Width:F1}x{block.Bounds.Height:F1}px, and the whole "
                    + $"{token.Length}-character token needs {needed.Height:F1}px at that width "
                    + $"(wrapping {block.TextWrapping}, trimming {block.TextTrimming}). Reveal must show "
                    + "the full value (#643).");
                return regression;
            }

            reveal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (_localApiTokenRevealed || string.Equals(block.Text, token, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Smoke: pressing Hide left the token revealed (#643).");
                return regression;
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Smoke: the revealed Local API token check could not finish: {exception} "
                + "Setup failure, not the #643 regression.");
            return cannotRun;
        }
        finally
        {
            _localApiTokenRevealed = false;
            settings.LocalApiEnabled = enabled;
            await ApplyLocalApiSettingsAsync(_lifetime.Token);
            GoTo(page);
            settings.LocalApiSettingsChanged += OnLocalApiSettingsChanged;
            settings.PropertyChanged += OnSettingsPropertyChanged;
            _settingsAutoSave?.Stop();
            if (saveWasArmed) _settingsAutoSave?.Start();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
    }

    /// <summary>
    /// Measures ONE configuration of the toast's row: is the whole failure on screen, and is it
    /// using the room the pill gives it? Returns true when the check FAILED.
    /// </summary>
    private static bool ErrorToastMessageFailure(Window toast, string failure, string label,
        LinuxErrorToastAction action)
    {
        if (Descendant<TextBlock>(toast, "ToastMessage") is not { } message
            || Descendant<Border>(toast, "ToastBorder") is not { } border
            || Descendant<TextBlock>(toast, "ToastCountdown") is not { } countdown
            || Descendant<Button>(toast, "ToastSettingsButton") is not { } settings
            || countdown.Parent is not Border pill
            || message.Parent is not Grid row
            || Grid.GetColumn(message) >= row.ColumnDefinitions.Count)
        {
            Console.Error.WriteLine($"Smoke: {label}: the error toast is no longer the icon/message/"
                + "countdown/button Grid this check knows how to measure.");
            return true;
        }
        // The row is untouched by the fix: the toast must still carry the WHOLE failure, so that a
        // screen reader and any copy of it are right even where the pill is not.
        if (message.Text != failure)
        {
            Console.Error.WriteLine($"Smoke: {label}: the toast bound {message.Text?.Length} "
                + $"characters of a {failure.Length}-character failure — it is being truncated in "
                + "data, which is not the fix.");
            return true;
        }
        if (!message.IsEffectivelyVisible
            || settings.IsEffectivelyVisible != (action != LinuxErrorToastAction.None))
        {
            Console.Error.WriteLine($"Smoke: {label}: the message is "
                + $"{(message.IsEffectivelyVisible ? "visible" : "hidden")} and Open Settings is "
                + $"{(settings.IsEffectivelyVisible ? "visible" : "hidden")}, so this case is not "
                + "the configuration it claims to be.");
            return true;
        }

        // HORIZONTAL. The star column hands the message every pixel between the warning triangle
        // and the countdown pill, and the message must take all of them. A MaxWidth — from the
        // markup, from a style Setter, from a child element or from the constructor — shows up
        // here and only here, as a message rendered narrower than the column it sits in.
        var allotted = row.ColumnDefinitions[Grid.GetColumn(message)].ActualWidth;
        if (message.Bounds.Width + 0.5 < allotted)
        {
            Console.Error.WriteLine($"Smoke: {label}: the message rendered {message.Bounds.Width:F1}px "
                + $"inside the {allotted:F1}px column it was given — it is width-capped again, so a "
                + "failure reaches the user clipped (#669).");
            return true;
        }
        // Prove the case exercises the overflow at all: were the pill ever wide enough for the
        // whole sentence on one line, everything here would pass while proving nothing. This is
        // also what catches the star column being changed to Auto, which measures the message
        // against infinity, so it never wraps and simply runs out of the toast instead.
        var wanted = TextProbe(message, double.PositiveInfinity).Width;
        if (message.Bounds.Width <= 0 || wanted <= message.Bounds.Width + 0.5)
        {
            Console.Error.WriteLine($"Smoke: {label}: the failure wanted {wanted:F0}px on one line "
                + $"and was given {message.Bounds.Width:F0}px, so it never had to wrap and this "
                + "check proves nothing.");
            return true;
        }
        // Nothing may be pushed out of the pill by the message beside it.
        foreach (var (name, control) in new[]
                 { ("the message", (Control)message), ("the countdown", pill),
                   ("Open Settings", settings) })
        {
            if (!control.IsEffectivelyVisible) continue;
            if (control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), border)
                is not { } corner)
            {
                Console.Error.WriteLine($"Smoke: {label}: {name} is not inside the toast border.");
                return true;
            }
            if (corner.X > border.Bounds.Width + 0.5 || corner.Y > border.Bounds.Height + 0.5)
            {
                Console.Error.WriteLine($"Smoke: {label}: {name} ends at {corner.X:F1},{corner.Y:F1} "
                    + $"in a {border.Bounds.Width:F1}x{border.Bounds.Height:F1} toast — it runs past "
                    + "the edge of the pill and is cut off there.");
                return true;
            }
        }

        // VERTICAL. The message wraps, so the remaining question is whether every line it wrapped
        // onto was drawn — up to the cap, because unlike the width cap #669 was, the HEIGHT cap is
        // this fix's own and is deliberate: it is what bounds how far a long failure pushes the
        // toast up the screen. Asserting past it would make this check report the fix as the bug.
        // The two are only in agreement today because the probe's sentence happens to fit in the
        // cap; a longer failure, added guidance, or a locale whose "Open Settings" is wider —
        // which narrows the star column, so the message wraps onto more lines — would make the
        // drawn height saturate while the wanted height ran past it, and this would have exited 25
        // blaming #669 for the bound #669's own fix introduced. A regressed MaxWidth is caught by
        // the horizontal check above, which is where a width bug belongs.
        //
        // The cap is read off the real block, so a MaxHeight arriving by ANY of the four routes is
        // the one compared against — and is then held to the value this fix ships, because a cap
        // of 9 would otherwise satisfy the comparison as easily as a cap of 90 while clipping the
        // message to a sliver. The Overlay suite pins the same 90 in the markup; this catches it
        // changed from a style Setter or the constructor, which markup cannot see.
        const double intendedMaxHeight = 90;
        if (message.MaxHeight != intendedMaxHeight)
        {
            Console.Error.WriteLine($"Smoke: {label}: the message is capped at "
                + $"{(double.IsInfinity(message.MaxHeight) ? "nothing" : $"{message.MaxHeight}px")}"
                + $", not the {intendedMaxHeight}px this fix ships — a smaller cap clips the "
                + "failure to a sliver, a larger one lets it walk off the top of the screen.");
            return true;
        }
        var wrapped = TextProbe(message, message.Bounds.Width);
        var oneLine = TextProbe(message, double.PositiveInfinity).Height;
        if (wrapped.Height <= oneLine + 0.5)
        {
            Console.Error.WriteLine($"Smoke: {label}: the failure occupies {wrapped.Height:F1}px "
                + $"against a {oneLine:F1}px line, so it never wrapped onto a second line and this "
                + "check proves nothing.");
            return true;
        }
        // A shortfall against what the text wants is only a DEFECT when the cap was not the thing
        // that caused it. The test is therefore not "did it draw every line" but "was there room
        // for one more line and it drew fewer anyway", which is the same question the horizontal
        // check asks about the column.
        //
        // Stated at whole-line granularity because the cap admits whole lines only, and because
        // the exact pixel is not reconstructible from a probe: measured here, a message wanting
        // 132px inside the 90px cap draws 88px — six lines of the theme's 14.6311 LineHeight,
        // ceil'd — so comparing against 90 reports that 2px as the message being cut off, and
        // comparing against a cap rebuilt from the one-line probe's 15px reports the same, since
        // a single line is ceil'd to 15 and six are not ceil'd to 90. Asking whether ANOTHER LINE
        // would have fitted needs neither the ceil nor the exact pitch to be modelled.
        var linePitch = double.IsNaN(message.LineHeight) || message.LineHeight <= 0.5
            ? oneLine : message.LineHeight;
        if (message.Bounds.Height + 0.5 < wrapped.Height
            && message.Bounds.Height + linePitch <= intendedMaxHeight)
        {
            Console.Error.WriteLine($"Smoke: {label}: the toast drew {message.Bounds.Height:F1}px of "
                + $"a failure that needs {wrapped.Height:F1}px to be read, with room for another "
                + $"{linePitch:F1}px line inside the {intendedMaxHeight}px cap — the message is "
                + "being cut off vertically (#669).");
            return true;
        }
        return false;
    }

    /// <summary>Nothing is written anywhere, so the probe above leaves no placement behind.</summary>
    private sealed class InMemoryOverlayPlacementStore : ILinuxOverlayPlacementStore
    {
        private LinuxOverlayPlacement? _placement;
        public LinuxOverlayPlacement? Load() => _placement;
        public void Save(LinuxOverlayPlacement placement) => _placement = placement;
    }

    private static T? Descendant<T>(Visual root, string name) where T : Control
        => root.GetLogicalDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    /// <summary>
    /// Waits for the platform to answer. The toast is SizeToContent, its placement follows the
    /// size settle, and the slide in runs for 200ms after that — none of which has happened by
    /// the time the dispatcher next goes idle. Polls until the size and the position have both
    /// stopped moving rather than sleeping a fixed amount.
    ///
    /// Returns FALSE when the budget ran out with the window still moving, so a caller can tell
    /// "this is what the platform decided" from "this run never finished". They are not the same
    /// answer, and the difference is invisible in the reading alone.
    /// </summary>
    private static async Task<bool> ToastSettledAsync(Window window)
    {
        var last = (window.Bounds.Height, window.Position.Y);
        var stable = 0;
        for (var attempt = 0; attempt < 150 && stable < 6; attempt++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(10);
            var now = (window.Bounds.Height, window.Position.Y);
            stable = now == last ? stable + 1 : 0;
            last = now;
        }
        return stable >= 6;
    }

    /// <summary>
    /// The size this TextBlock's own text wants at a given width, measured through a copy that
    /// carries every property the layout of the text depends on — including LineHeight, which the
    /// app's theme sets on every TextBlock and a bare copy would therefore measure short.
    /// Comparing that against the arranged size is how the check above tells "it fits" from "it
    /// was cut", with no hard-coded pixel number for a font change to invalidate.
    /// </summary>
    private static Size TextProbe(TextBlock block, double width)
    {
        var probe = new TextBlock
        {
            Text = block.Text,
            FontFamily = block.FontFamily,
            FontSize = block.FontSize,
            FontWeight = block.FontWeight,
            FontStyle = block.FontStyle,
            FontStretch = block.FontStretch,
            LineHeight = block.LineHeight,
            LetterSpacing = block.LetterSpacing,
            TextAlignment = block.TextAlignment,
            // The measurement is of the text the fix asks for: wrapped, and never trimmed or
            // capped, so that a MaxHeight or a TextTrimming on the real block shows up as a
            // shortfall against this rather than being reproduced by it.
            TextWrapping = double.IsInfinity(width) ? TextWrapping.NoWrap : TextWrapping.Wrap,
        };
        probe.Measure(new Size(width, double.PositiveInfinity));
        return probe.DesiredSize;
    }

    /// <summary>
    /// Issue #560, the Linux half of the pair #557 fixed on Windows. <c>Transcript.Mode</c> is a
    /// COPY of the mode name taken when the recording was made, so History draws that free user
    /// text twice more than the Modes page does: on the detail header's grey badge, and once per
    /// mode in the "Retry With…" submenu. Neither had a cap.
    ///
    /// MEASURED in the real window, not grepped, for the same two reasons #526's check gives:
    /// the attribute can be put on the wrong TextBlock, and a later container change can
    /// reintroduce the clip with the attribute still present. Every comparison is against the
    /// width the string itself wants, so no pixel number is hard-coded.
    ///
    /// Returns true when the check FAILED, so the caller can bail with a smoke exit code.
    ///
    /// The probe mode and the probe transcript are inserted and then REMOVED again, and both
    /// selections are captured BEFORE the insert and put back after: <c>run-ui-smoke.sh</c> runs
    /// against the developer's own profile as well as a throwaway CI one, CI runs it twice, and
    /// selecting a mode persists its id to settings.json.
    /// </summary>
    private async Task<bool> HistoryModeNameLayoutFailureAsync(ContextMenu historyMenu)
    {
        var longName = "LongName" + new string('X', 292);
        var restoreMode = _viewModel.Modes.Selected;
        var restoreTranscript = _viewModel.History.Selected;
        Guid modeProbeId;
        Guid transcriptProbeId;
        await using (var context = _database.CreateContext())
        {
            // The badge reads Transcript.Mode, the submenu reads Mode.Name, and they are two
            // different columns — so the probe has to be both a mode and a recording made
            // under it, or only one of the two surfaces would be exercised.
            var mode = new Mode { Name = longName, Language = "en" };
            // Failed, with an audio path, because CanRetry is
            // `{ Status: Failed, AudioFilePath: not null }` (HistoryViewModel.cs). A Completed
            // row disables the Retry With… item, and the case would then be measuring a popup
            // in a state no user can open — passing today, but on a template that gates the
            // popup on IsEffectivelyEnabled it would go red on a healthy build.
            var transcript = new Transcript
            {
                Text = "The badge above this transcript is the surface under test.",
                Mode = longName,
                Date = DateTime.UtcNow,
                Duration = 3,
                Status = TranscriptStatus.Failed,
                AudioFilePath = "/nonexistent/smoke-probe.wav",
            };
            context.Modes.Add(mode);
            context.Transcripts.Add(transcript);
            await context.SaveChangesAsync();
            modeProbeId = mode.Id;
            transcriptProbeId = transcript.Id;
        }
        try
        {
            return await HistoryModeNameLayoutFailureCoreAsync(
                historyMenu, longName, transcriptProbeId);
        }
        finally
        {
            historyMenu.Close();
            await using (var context = _database.CreateContext())
            {
                if (await context.Modes.FindAsync(modeProbeId) is { } probeMode)
                    context.Modes.Remove(probeMode);
                if (await context.Transcripts.FindAsync(transcriptProbeId) is { } probeTranscript)
                    context.Transcripts.Remove(probeTranscript);
                await context.SaveChangesAsync();
            }
            await _viewModel.Modes.RefreshAsync();
            await _viewModel.History.RefreshAsync();
            // Restored AFTER the refresh and matched by id, never by holding the old instance:
            // both RefreshAsync calls clear their collection and refill it with entities from a
            // new context, and History's re-picks the first row unconditionally. Putting the
            // captured object back before that would be overwritten, and putting it back after
            // would leave a detached entity selected.
            if (restoreTranscript is not null
                && _viewModel.History.Items.FirstOrDefault(item => item.Id == restoreTranscript.Id)
                    is { } sameTranscript)
                _viewModel.History.Selected = sameTranscript;
            if (restoreMode is not null
                && _viewModel.Modes.Items.FirstOrDefault(item => item.Id == restoreMode.Id)
                    is { } sameMode)
                _viewModel.Modes.Selected = sameMode;
            // SetRetryModes is only called once, at startup, so the submenu keeps whatever list
            // it was given. Put the real one back after the probe has gone.
            _viewModel.History.SetRetryModes(_viewModel.Modes.Items);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
    }

    private async Task<bool> HistoryModeNameLayoutFailureCoreAsync(
        ContextMenu historyMenu, string longName, Guid transcriptProbeId)
    {
        await _viewModel.Modes.RefreshAsync();
        await _viewModel.History.RefreshAsync();
        _viewModel.History.SetRetryModes(_viewModel.Modes.Items);
        _viewModel.History.Selected = _viewModel.History.Items
            .FirstOrDefault(item => item.Id == transcriptProbeId);
        _viewModel.Navigate("history");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        if (_viewModel.History.Selected is null)
        {
            Console.Error.WriteLine("Smoke: the probe transcript did not come back from the history query.");
            return true;
        }

        // THE BADGE. It sits in a horizontal StackPanel, and a StackPanel measures its children
        // with infinite width, so TextTrimming on its own changes nothing here — MaxWidth is
        // what bounds it. The pane cannot be widened: the window is a fixed 1000, CanResize
        // "False", and this page is a 300,1,* Grid.
        if (FindNamed<TextBlock>("HistoryDetailModeText") is not { } badge
            || FindNamed<Grid>("HistoryScreen") is not { } page)
        {
            Console.Error.WriteLine("Smoke: the history detail has no HistoryDetailModeText badge.");
            return true;
        }
        // Display only: the row still carries the whole name, so the tip and any copy of it
        // are right. A name truncated in DATA is not the fix.
        if (badge.Text != longName)
        {
            Console.Error.WriteLine($"Smoke: the history badge bound {badge.Text?.Length} characters "
                + $"of a {longName.Length}-character mode name — it is being truncated in data.");
            return true;
        }
        if (badge.TextTrimming != TextTrimming.CharacterEllipsis)
        {
            Console.Error.WriteLine($"Smoke: the history badge trims with {badge.TextTrimming}, so a "
                + "long mode name is hard-clipped at the pane edge with nothing to say it continues.");
            return true;
        }
        // Prove the case exercises the overflow at all: the name has to want more than the page
        // can ever give it, or everything below would pass while proving nothing. Compared
        // against the PAGE and not against the badge's own arranged width, because an unbounded
        // badge is given everything it asks for — that is the bug, not the absence of one.
        var wantedBadge = UnconstrainedTextWidth(badge);
        if (badge.Bounds.Width <= 0 || wantedBadge <= page.Bounds.Width)
        {
            Console.Error.WriteLine($"Smoke: the {longName.Length}-character mode name wanted "
                + $"{wantedBadge:F0}px on a {page.Bounds.Width:F0}px page, so it never overflowed "
                + "and this check proves nothing.");
            return true;
        }
        // THE BUG, STATED AS THE BUG: the badge must end inside the page. Before the fix its
        // right edge sat far outside it and the pill was cut off by the header border.
        if (badge.Parent is not StackPanel { Parent: Border pill }
            || pill.TranslatePoint(new Point(pill.Bounds.Width, 0), page) is not { } pillEnd)
        {
            Console.Error.WriteLine("Smoke: the history mode badge is no longer an hBadge Border.");
            return true;
        }
        if (pillEnd.X > page.Bounds.Width + 0.5)
        {
            Console.Error.WriteLine($"Smoke: the history mode badge ends at {pillEnd.X:F1} on a "
                + $"{page.Bounds.Width:F1} wide page — it runs off the detail pane.");
            return true;
        }
        // And the mode name is what gave way, with an ellipsis rather than a hard clip.
        if (badge.Bounds.Width + 0.5 >= wantedBadge)
        {
            Console.Error.WriteLine($"Smoke: the history badge rendered {badge.Bounds.Width:F1}px of "
                + $"{wantedBadge:F1}px — it was not bounded at all.");
            return true;
        }

        // THE RETRY WITH… SUBMENU. Its items are built in code behind, one per mode, and the
        // header was the mode name verbatim. What happens to an over-long one is NOT what the
        // Windows tray does — this popup does not run off the desktop, because the Fluent
        // flyout presenter carries a theme maximum of its own. It is what the mode card did in
        // #526: the name is HARD-CLIPPED mid-glyph at that maximum, with no ellipsis and no way
        // to read the rest, and every other entry in the list is stretched out to meet it. The
        // numbers for both are measured below, in the same window, in the same popup.
        OnHistoryContextMenuOpening(historyMenu, new CancelEventArgs());
        if (historyMenu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Name == "HistoryRetryMode")
                is not { } retryWith
            || retryWith.ItemsSource?.OfType<MenuItem>().ToList() is not { Count: > 0 } retryItems)
        {
            Console.Error.WriteLine("Smoke: Retry With… built no items for the probe mode.");
            return true;
        }
        // A string header is drawn by the MenuItem template's own ContentPresenter, and there is
        // nothing in there to trim or to cap. That is the pre-fix state, stated as itself.
        if (retryItems.Select(item => item.Header).OfType<TextBlock>()
                .FirstOrDefault(block => block.Text == longName) is not { } label)
        {
            Console.Error.WriteLine("Smoke: the Retry With… item for the long mode has no bounded "
                + "TextBlock header — its header is "
                + $"{retryItems.FirstOrDefault()?.Header?.GetType().Name ?? "null"}, which cannot trim.");
            return true;
        }
        // Display only, again: the item still carries the whole name for its tip and its command.
        if (label.Text != longName)
        {
            Console.Error.WriteLine($"Smoke: the Retry With… label carries {label.Text?.Length} "
                + $"characters of a {longName.Length}-character name — it is truncated in data.");
            return true;
        }
        if (label.TextTrimming != TextTrimming.CharacterEllipsis)
        {
            Console.Error.WriteLine($"Smoke: the Retry With… label trims with {label.TextTrimming}, so "
                + "the name is cut mid-glyph at the flyout's own maximum instead of ellipsising.");
            return true;
        }
        // The accessible name, read off the REAL automation peer rather than off the property we
        // set. Moving the header from a string to a Control is what makes this necessary: the
        // peer reads a string header directly, and has nothing to read in a Control one. A
        // screen-reader user would then hear the same thing for every mode in the list. Asserted
        // through the peer because that is the thing AT-SPI actually asks.
        if (retryItems.FirstOrDefault(item => ReferenceEquals(item.Header, label)) is not { } labelled
            || ControlAutomationPeer.CreatePeerForElement(labelled).GetName() != longName)
        {
            Console.Error.WriteLine("Smoke: the Retry With… item reports the accessible name "
                + $"[{ControlAutomationPeer.CreatePeerForElement(retryItems[0]).GetName()}] instead "
                + "of the mode name, so a screen reader cannot tell the entries apart.");
            return true;
        }
        if (FindNamed<ListBox>("HistoryList") is not { } list)
        {
            Console.Error.WriteLine("Smoke: HistoryList is gone, so the context menu cannot be opened.");
            return true;
        }
        // LAID OUT FOR REAL. An attribute assertion proves nothing on its own: it can be put on
        // the wrong TextBlock, and a template change can spend the budget elsewhere. The menu is
        // opened on the list, the submenu opened with it, and the arranged widths read back off
        // the real containers.
        var boundedWidth = await SubmenuWidthAsync(historyMenu, retryWith, list, retryItems);
        if (boundedWidth <= 0)
        {
            Console.Error.WriteLine("Smoke: the Retry With… popup never laid out, so it is unmeasured.");
            return true;
        }
        // Measured AFTER the popup has opened once, never before. A TextBlock that is not yet in
        // the tree has not inherited the menu's font, so it probes in the framework default and
        // returns a width for the wrong typeface — which is not a fixed bias, and for a name of
        // 16-19 characters it reads WIDER than the attached one and silently suppresses the
        // assertion below. The badge above never had this problem: FindNamed returns a control
        // that is already parented.
        var wanted = UnconstrainedTextWidth(label);
        if (label.Bounds.Width <= 0 || label.Bounds.Width + 0.5 >= wanted
            || label.Bounds.Width > RetryModeLabelMaxWidth + 0.5)
        {
            Console.Error.WriteLine($"Smoke: the Retry With… label laid out at {label.Bounds.Width:F1}px "
                + $"of the {wanted:F0}px it wants, against a {RetryModeLabelMaxWidth:F0}px cap — it was "
                + "not bounded at all.");
            return true;
        }
        // Then the SAME popup with the pre-fix header — the raw string, which is the one line
        // this fix changed — so the case measures the defect and the fix side by side rather
        // than against a hard-coded pixel number that a font or a theme change would invalidate.
        // Without this the two assertions above could pass in a layout where nothing overflowed.
        var unboundedWidth = await SubmenuWidthAsync(
            historyMenu, retryWith, list, [new MenuItem { Header = longName }]);
        retryWith.ItemsSource = retryItems;
        if (unboundedWidth <= boundedWidth)
        {
            Console.Error.WriteLine($"Smoke: an unbounded {longName.Length}-character name laid the "
                + $"submenu out at {unboundedWidth:F1}px against {boundedWidth:F1}px bounded, so it "
                + "never overflowed and this check proves nothing.");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Opens the history context menu and its Retry With… submenu on the real list, lets the
    /// layout run, and returns the arranged width of the widest item — which is the popup's
    /// width, because a menu sizes itself to its widest entry. Closes the menu again.
    /// </summary>
    private static async Task<double> SubmenuWidthAsync(
        ContextMenu menu, MenuItem retryWith, Control target, IReadOnlyList<MenuItem> items)
    {
        retryWith.ItemsSource = items;
        menu.Open(target);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        retryWith.Open();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        var width = items.Max(item => item.Bounds.Width);
        menu.Close();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        return width;
    }

    /// <summary>
    /// Applies one mode-editor change and checks the reveal rules it is supposed to drive.
    /// Returns true when the check FAILED, so the caller can bail with a smoke exit code.
    /// </summary>
    private static async Task<bool> ModeEditorRevealFailureAsync(
        Visual editor, Action change, string[] expectVisible, string[] expectHidden)
    {
        change();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        foreach (var control in expectVisible)
            if (!IsRevealed(control, editor))
            {
                Console.Error.WriteLine($"Smoke: {control} should be revealed in the mode editor.");
                return true;
            }
        foreach (var control in expectHidden)
            if (IsRevealed(control, editor))
            {
                Console.Error.WriteLine($"Smoke: {control} should be hidden in the mode editor.");
                return true;
            }
        return false;
    }

    /// <summary>
    /// Effective visibility inside a dialog that has not been shown. Avalonia's own
    /// IsEffectivelyVisible is false for every control while the window is closed, and a plain
    /// IsVisible is true even when the panel wrapping the control is hidden — which is exactly
    /// the case the reveal rules create. So this walks the ancestors itself and stops at the
    /// window, whose own IsVisible says nothing about the layout under test.
    /// </summary>
    private static bool IsRevealed(string name, Visual root)
    {
        var control = root.GetLogicalDescendants().OfType<Control>().FirstOrDefault(item => item.Name == name);
        for (var node = control; node is not null && !ReferenceEquals(node, root); node = node.Parent as Control)
            if (!node.IsVisible) return false;
        return control is not null;
    }

    private static bool HasVisibleControl(string name, Visual root)
        => root.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == name && control.IsVisible);

    private async Task SeedSmokeDataAsync()
    {
        await using (var context = _database.CreateContext())
        {
            if (!context.Transcripts.Any())
                context.Transcripts.Add(new Transcript { Text = "Smoke transcript", Status = TranscriptStatus.Completed, Date = DateTime.UtcNow });
            if (!context.VocabularyItems.Any())
                context.VocabularyItems.Add(new VocabularyItem { Word = "SmokeTerm" });
            if (!context.Modes.Any())
                context.Modes.Add(new Mode { Name = "Smoke Mode", Language = "en" });
            await context.SaveChangesAsync();
        }
        await Task.WhenAll(
            _viewModel.Home.RefreshAsync(),
            _viewModel.History.RefreshAsync(),
            _viewModel.Vocabulary.RefreshAsync(),
            _viewModel.Modes.RefreshAsync());
    }
}
