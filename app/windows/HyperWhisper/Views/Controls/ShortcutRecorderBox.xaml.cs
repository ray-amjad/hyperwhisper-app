// THE SHORTCUT RECORDER
//
// See ShortcutRecorderBox.xaml for why this exists as a control rather than as a
// second copy of ShortcutsSettingsPage's key handlers.

using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using HyperWhisper.Models;
using HyperWhisper.Services;

namespace HyperWhisper.Views.Controls;

/// <summary>What a recorder captured, and under which role it was validated.</summary>
public sealed class ShortcutCapturedEventArgs : EventArgs
{
    public ShortcutCapturedEventArgs(string role, KeyboardShortcut shortcut)
    {
        Role = role;
        Shortcut = shortcut;
    }

    /// <summary>The <see cref="ShortcutRecorderBox.Role"/> of the box that captured it.</summary>
    public string Role { get; }

    /// <summary>The captured chord. Already validated; the host only has to store it.</summary>
    public KeyboardShortcut Shortcut { get; }

    /// <summary>
    /// The round-trippable form, for a host that cannot take a WPF type - the
    /// onboarding seam is unit-tested with no WPF loaded at all.
    /// </summary>
    public string Persisted => Shortcut.ToPersistedString();
}

/// <summary>
/// A read-only field that records the next key chord pressed into it.
///
/// It validates and it reports. It never writes a setting: the Shortcuts settings
/// page stores the result itself, and the onboarding Permissions step hands it to
/// the flow model instead, so first run keeps its one path in and out of state.
/// </summary>
public partial class ShortcutRecorderBox : WpfUserControl
{
    public ShortcutRecorderBox()
    {
        InitializeComponent();
    }

    // =========================================================================
    // API
    // =========================================================================

    /// <summary>
    /// Which shortcut this box edits: "Toggle", "Cancel", "ChangeMode", "Streaming"
    /// or "PushToTalkCustom". Passed straight to
    /// <see cref="ShortcutValidationService.ValidateDuplicate"/>, which uses it to
    /// exclude the box's own current value from the duplicate check.
    /// </summary>
    public static readonly DependencyProperty RoleProperty =
        DependencyProperty.Register(nameof(Role), typeof(string), typeof(ShortcutRecorderBox),
            new PropertyMetadata(string.Empty));

    public string Role
    {
        get => (string)GetValue(RoleProperty);
        set => SetValue(RoleProperty, value);
    }

    /// <summary>
    /// What the field shows. The control writes it after a successful capture, and
    /// the host may write it to seed or reset the box - which is what
    /// <c>LoadShortcutSettings()</c> and <c>MigrateModifierOnlyShortcuts()</c> do.
    /// </summary>
    public static readonly DependencyProperty DisplayTextProperty =
        DependencyProperty.Register(nameof(DisplayText), typeof(string), typeof(ShortcutRecorderBox),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnDisplayTextChanged));

    public string DisplayText
    {
        get => (string)GetValue(DisplayTextProperty);
        set => SetValue(DisplayTextProperty, value);
    }

    private static void OnDisplayTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ShortcutRecorderBox box)
            box.Field.Text = e.NewValue as string ?? string.Empty;
    }

    /// <summary>
    /// Whether a rejected chord renders its reason under the field. The Shortcuts
    /// page and the onboarding step both want it; the push-to-talk box historically
    /// did not have an error line, so it can turn it off and keep looking the same.
    /// </summary>
    public static readonly DependencyProperty ShowsInlineErrorProperty =
        DependencyProperty.Register(nameof(ShowsInlineError), typeof(bool), typeof(ShortcutRecorderBox),
            new PropertyMetadata(true));

    public bool ShowsInlineError
    {
        get => (bool)GetValue(ShowsInlineErrorProperty);
        set => SetValue(ShowsInlineErrorProperty, value);
    }

    /// <summary>Raised only for a chord that passed both validations.</summary>
    public event EventHandler<ShortcutCapturedEventArgs>? ShortcutCaptured;

    /// <summary>The current inline error, or null. Public so a host can assert on it.</summary>
    public string? ErrorMessage { get; private set; }

    // =========================================================================
    // CAPTURE
    // Lifted from ShortcutsSettingsPage.xaml.cs; the rules are unchanged.
    // =========================================================================

    private void Field_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        e.Handled = true;

        var shortcut = BuildShortcutFromKeyEvent(e);
        if (shortcut == null) return;

        // VALIDATE: reject unsafe single bare modifiers, but allow intentional
        // multi-modifier chords such as Ctrl+Win.
        if (shortcut.IsSingleBareModifier)
        {
            const string message =
                "Single modifier shortcuts such as Ctrl, Alt, Shift, or Win are not supported. "
                + "Use a key with modifiers or a multi-modifier shortcut such as Ctrl+Win.";
            ShowError(message);
            LoggingService.Debug($"ShortcutRecorderBox: rejected single-modifier shortcut for {Role}: {shortcut}");
            return;
        }

        // VALIDATE: check for duplicates against the other three global shortcuts.
        // Reading SettingsService here rather than taking the four as properties is
        // deliberate: it is what the settings page already did, and a recorder that
        // could be told a stale set of siblings would let two roles claim one chord.
        var settings = SettingsService.Instance;
        var validationError = ShortcutValidationService.ValidateDuplicate(
            shortcut,
            Role,
            settings.ToggleShortcut,
            settings.CancelShortcut,
            settings.ChangeModeShortcut,
            settings.StreamingShortcut);

        if (validationError != null)
        {
            ShowError(validationError);
            LoggingService.Warn($"ShortcutRecorderBox: shortcut validation failed for {Role}: {validationError}");
            return;
        }

        ClearError();

        // Never stamp a local value over a binding. WPF DROPS a OneWay binding the
        // moment its target takes a local value, so the settings page's imperative
        // ".DisplayText =" is safe but the onboarding step's
        // "DisplayText={Binding ShortcutDisplay}" would stop following the flow model
        // after the very first capture.
        //
        // Leaving the bound case alone is also the honest rendering: the host is the
        // only thing that knows whether the chord was actually stored, so a host that
        // refuses it leaves the field showing what is really configured rather than
        // what was typed.
        if (BindingOperations.GetBindingExpression(this, DisplayTextProperty) is null)
            DisplayText = shortcut.ToDisplayString();

        ShortcutCaptured?.Invoke(this, new ShortcutCapturedEventArgs(Role, shortcut));
    }

    private void Field_PreviewKeyUp(object sender, WpfKeyEventArgs e)
    {
        // Keep the capture field from leaking Win-key releases to WPF text input.
        // The global hook still controls runtime shortcut suppression.
        e.Handled = true;
    }

    private static KeyboardShortcut? BuildShortcutFromKeyEvent(WpfKeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.None) return null;

        var shortcut = new KeyboardShortcut
        {
            Control = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) || key is Key.LeftCtrl or Key.RightCtrl,
            Alt = Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt) || key is Key.LeftAlt or Key.RightAlt,
            Shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) || key is Key.LeftShift or Key.RightShift,
            Win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin) || key is Key.LWin or Key.RWin
        };

        if (!IsModifierKey(key))
            shortcut.Key = key;

        return shortcut;
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    // =========================================================================
    // ERROR DISPLAY
    // =========================================================================

    private void ShowError(string message)
    {
        ErrorMessage = message;

        if (ShowsInlineError)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        Field.BorderBrush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xFF, 0x55, 0x55));
        Field.BorderThickness = new Thickness(2);
    }

    private void ClearError()
    {
        ErrorMessage = null;
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
        // Control.*, not Border.*. The page this was lifted from cleared
        // Border.BorderBrushProperty on a TextBox, which is a DIFFERENT dependency
        // property from the Control.BorderBrush the TextBox actually renders, so the
        // red border it set on a rejected chord never came off again. One line, and
        // the whole reason to extract this rather than copy it twice more.
        Field.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        Field.ClearValue(System.Windows.Controls.Control.BorderThicknessProperty);
    }
}
