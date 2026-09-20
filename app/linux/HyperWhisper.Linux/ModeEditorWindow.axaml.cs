using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HyperWhisper.Linux.Localization;
using HyperWhisper.PortableApplication.ViewModels;

namespace HyperWhisper.Linux;

/// <summary>
/// The mode editor as a modal dialog, which is where Windows has always put it. The Modes page
/// keeps only the card grid; the gear on a card and the header's Create Mode both open this.
///
/// Windows builds a fresh window over a copy of the Mode entity and writes it only on Save, so
/// Cancel discards. Linux binds the dialog to the one shared <see cref="ModesViewModel"/> the
/// page uses, so the same contract is met by snapshotting every editor field before the dialog
/// opens and restoring that snapshot on Cancel — without which re-opening the editor on the SAME
/// mode showed the abandoned edits, because the Selected setter short-circuits on an equal value.
/// </summary>
public partial class ModeEditorWindow : Window
{
    private readonly ModesViewModel? _modes;
    private readonly bool _isCreate;
    private ModesViewModel.ModeEditorSnapshot? _snapshot;
    private bool _committed;

    public ModeEditorWindow()
    {
        AvaloniaXamlLoader.Load(this);
        ComboWheelGuard.Attach(this);
    }

    public ModeEditorWindow(ModesViewModel modes) : this(modes, false, null) { }

    public ModeEditorWindow(ModesViewModel modes, bool isCreate, ModesViewModel.ModeEditorSnapshot? snapshot)
        : this()
    {
        _modes = modes ?? throw new ArgumentNullException(nameof(modes));
        _isCreate = isCreate;
        _snapshot = snapshot;
        DataContext = modes;

        // Windows reassigns the title and the Save label for the create dialog and collapses
        // Delete (ModeEditorWindow.xaml.cs:81-85); Delete hides through IsEditing in the XAML.
        Title = L(isCreate ? "mode.editor.title.create" : "mode.editor.title.edit");
        if (this.FindControl<Button>("ModeSaveButton") is { } save)
            save.Content = L(isCreate ? "modes.button.create" : "modes.button.save");

        modes.PropertyChanged += OnModesPropertyChanged;
        Closed += (_, _) => modes.PropertyChanged -= OnModesPropertyChanged;
    }

    /// <summary>
    /// Re-applies the Model selection after the company row changes the list under it.
    ///
    /// A ComboBox drops a selection its NEW ItemsSource does not hold, and it writes that
    /// null back through the binding. The view model keeps the model and raises the change
    /// again, but that notification arrives while the binding is still writing, so the
    /// binding ignores it and the row stays EMPTY. Measured on the Linux head 2026-09-20:
    /// picking Google over ElevenLabs left the Model row blank with
    /// <c>gemini-3.5-transcribe</c> selected in the view model. Post it instead, so the
    /// re-apply lands after the list change has settled.
    /// </summary>
    private void OnModesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ModesViewModel.CloudTierModels)) return;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_modes is null) return;
                if (this.FindControl<ComboBox>("ModeCloudTierModel") is { } combo)
                    combo.SelectedValue = _modes.CloudTierModel;
            },
            DispatcherPriority.Background);
    }

    private static string L(string key) =>
        (Avalonia.Application.Current as App)?.Localization[key] ?? key;

    /// <summary>
    /// Windows marks Cancel IsCancel and Save IsDefault, so Esc discards and Enter saves —
    /// except inside the multi-line boxes, where Enter has to keep inserting a newline.
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelAndClose();
            return;
        }
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        if (FocusManager?.GetFocusedElement() is TextBox { AcceptsReturn: true }) return;
        e.Handled = true;
        Save();
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        if (_modes is null) { Close(); return; }
        if (!_modes.CanSave) return;
        if (_modes.SaveCommand.CanExecute(null)) _modes.SaveCommand.Execute(null);
        // A failed save keeps the dialog open so the error line under the cards is readable.
        if (_modes.Status.HasError) return;
        _committed = true;
        Close();
    }

    /// <summary>
    /// Windows confirms a delete with a YesNo MessageBox, and refuses outright when this is the
    /// last mode (ModeEditorWindow.xaml.cs:2279-2303). Both wordings come from the same catalog
    /// keys, so the two apps read identically by construction.
    /// </summary>
    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_modes is null) { Close(); return; }
        if (_modes.Items.Count <= 1)
        {
            await ConfirmWindow.ShowNoticeAsync(this,
                L("mode.editor.delete.cannotDelete.title"),
                L("mode.editor.delete.cannotDelete.message"));
            return;
        }
        var confirmed = await ConfirmWindow.ShowAsync(this,
            L("mode.editor.delete.confirm.title"),
            LF("mode.editor.delete.confirm.message", _modes.Selected?.Name ?? _modes.Name));
        if (!confirmed) return;
        if (_modes.DeleteCommand.CanExecute(null)) _modes.DeleteCommand.Execute(null);
        if (_modes.Status.HasError) return;
        _committed = true;
        Close();
    }

    private static string LF(string key, params object[] args) =>
        (Avalonia.Application.Current as App)?.Localization.Format(key, args) ?? key;

    private void OnCancel(object? sender, RoutedEventArgs e) => CancelAndClose();

    private void OnClearUserPrompt(object? sender, RoutedEventArgs e)
    {
        if (_modes is not null) _modes.UserSystemPrompt = string.Empty;
    }

    private void CancelAndClose()
    {
        Discard();
        Close();
    }

    /// <summary>
    /// Restores the pre-dialog state. Also runs when the window is closed from the title bar,
    /// which on Windows is the same Cancel path.
    /// </summary>
    private void Discard()
    {
        if (_committed || _modes is null) return;
        _committed = true;
        if (_snapshot is { } state) _modes.RestoreEditorState(state);
        else _modes.ReloadSelected();
        _snapshot = null;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Discard();
        base.OnClosing(e);
    }
}
