using Avalonia.Controls;
using Avalonia.VisualTree;
using HyperWhisper.ModelManagement;

// Issue #1629: in the mode editor, On-device, switching the engine from Whisper to Parakeet left the
// model combo BLANK while the view model held parakeet-v2, and Save wrote that unseen model. The
// #482 view model test passed while the bug was live, because the fault is in the binding round trip
// at a real ComboBox. So this opens the real ModeEditorWindow, under the real App (its resources,
// converters and theme), on a real X server: the harness re-runs itself under xvfb-run with
// ProbeArgument, the probe drives the dialog's own controls, and the parent reads its exit code.
static class ModeEditorEngineSwitchRenderTests
{
    public const string ProbeArgument = "--probe-mode-editor-engine-switch";

    public static Task RunAsync() =>
        XvfbProbe.RunAsync(ProbeArgument, "the #1629 mode editor engine-switch probe", "en", "1280x1024");

    /// <summary>The child half: runs on the X server xvfb-run started. Exit 0 only if every step showed.</summary>
    public static Task<int> RunProbeAsync() => ModeEditorProbe.RunAsync("1629", (modes, window) =>
    {
        T Find<T>(string name) where T : Control =>
            window.FindControl<T>(name) ?? throw new InvalidOperationException($"ModeEditorWindow has no {name}");
        var onDevice = Find<RadioButton>("ModeSourceOnDevice");
        var engine = Find<ComboBox>("ModeLocalEngine");
        var model = Find<ComboBox>("ModeTranscriptionModel");

        var failures = new List<string>();
        void Expect(string step, string id, string label)
        {
            ModeEditorProbe.Pump(window);
            // What the closed combo draws is SelectionBoxItem through the ItemTemplate, so read
            // the rendered TextBlock too, not only the SelectedItem property.
            var shownText = string.Join("|", model.GetVisualDescendants().OfType<TextBlock>()
                .Select(block => block.Text).Where(value => !string.IsNullOrEmpty(value)));
            var selected = model.SelectedItem as string ?? "<blank>";
            var box = model.SelectionBoxItem as string ?? "<blank>";
            Console.WriteLine($"{step}: view model {modes.TranscriptionModel}, SelectedItem {selected}, SelectionBoxItem {box}, text '{shownText}'");
            if (modes.TranscriptionModel != id) failures.Add($"{step}: the view model holds {modes.TranscriptionModel}, expected {id}");
            if (selected != id) failures.Add($"{step}: the model combo's SelectedItem is {selected}, expected {id}");
            if (box != id) failures.Add($"{step}: the model combo's SelectionBoxItem is {box}, expected {id}");
            if (shownText != label) failures.Add($"{step}: the model combo draws '{shownText}', expected '{label}'");
        }

        onDevice.IsChecked = true;
        Expect("On-device", "base", "Base");
        engine.SelectedItem = "parakeet";
        Expect("Whisper -> Parakeet", "parakeet-v2", "Parakeet v2 (English)");
        // Back on Whisper, NormalizeLocalModel picks the engine's FIRST catalog entry (tiny), not
        // the base the dialog opened on; that choice is deliberate and out of scope here. What
        // #1629 requires is that the combo shows whichever id the view model holds.
        var firstWhisper = PortableModelCatalog.All.First(entry => entry.Kind == ManagedModelKind.Whisper);
        engine.SelectedItem = "whisper";
        Expect("Parakeet -> Whisper", firstWhisper.Id, firstWhisper.DisplayName);
        return failures;
    });
}
