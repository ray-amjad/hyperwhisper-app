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
/// page and the Streaming settings page store the result themselves, and the
/// onboarding Permissions step hands it to the flow model instead, so first run
/// keeps its one path in and out of state. Three hosts, seven boxes - see
/// ShortcutRecorderBox.xaml for the list, and count them before you change any
/// behaviour here.
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
        if (d is not ShortcutRecorderBox box)
            return;

        box.Field.Text = e.NewValue as string ?? string.Empty;

        // A verdict about the LAST chord cannot outlive the field it was about.
        // ClearError had exactly one caller - the successful-capture path - so a
        // rejected chord left the box red for the rest of the page visit: there is
        // no focus handler, and LoadShortcutSettings and ResetShortcuts_Click both
        // write DisplayText and nothing else. This is the hook they already go
        // through.
        //
        // The host's StandingError goes with it, for a stronger reason: that one is
        // a verdict about the chord that is STORED, so a re-seeded field makes it
        // stale by definition. This is the whole reason StreamingSettingsPage seeds
        // DisplayText BEFORE it renders its load-time conflict, and the reason a
        // successful capture needs no second call to withdraw one - Commit writes
        // DisplayText, and arrives here.
        box._standingError = null;
        box.ClearError();
    }

    /// <summary>
    /// Whether a rejected chord renders its reason under the field, AND the red
    /// border that goes with it. The two are one setting: see
    /// <see cref="ShowError"/> for why they may never be split again.
    ///
    /// Every host now leaves this true. The push-to-talk box used to set it false
    /// so it would "keep looking the same" as the page it came from, which was
    /// true of the text and false of the border; it now explains a rejected chord
    /// like the other six recorders do.
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

    /// <summary>
    /// Puts the keyboard in the capture field. A host that asks the user to
    /// re-record - the Streaming page's "Change shortcut" button - must reach focus
    /// to the inner field, because the UserControl itself is not focusable and
    /// <c>.Focus()</c> on it would silently do nothing.
    /// </summary>
    public void FocusForCapture() => Field.Focus();

    /// <summary>
    /// Whatever sentence is on screen right now, or null - the gesture's own verdict
    /// if there is one, otherwise the host's <see cref="StandingError"/>. Public so a
    /// host can assert on it. It is the RENDERED value, not an input: a host sets
    /// <see cref="StandingError"/>.
    /// </summary>
    public string? ErrorMessage { get; private set; }

    // =========================================================================
    // CAPTURE
    // The rules about WHICH chords are legal came off ShortcutsSettingsPage.xaml.cs
    // and are unchanged. WHEN a chord is committed is not: see below.
    //
    // A chord is typed one key at a time, so a key-down is not a finished gesture.
    // Committing on every key-down made "Ctrl+Shift+Space" three separate captures -
    // Ctrl (rejected), Ctrl+Shift (ACCEPTED AND STORED), Ctrl+Shift+Space (rejected
    // as a duplicate). The user saw the last verdict and kept the middle one, so a
    // refused chord silently rebound the role to a bare two-modifier chord that then
    // fired on any Ctrl+Shift anywhere in Windows, and survived a restart. The
    // modifier-only migration does not undo it either: IsSingleBareModifier is false
    // for two modifiers.
    //
    // So the gesture, not the key-down, is what commits, and it can end two ways:
    //
    //   - a NON-modifier key arrives. That key completes the chord and nothing can
    //     be added to it, so this key-down IS the end. Commit here.
    //   - every key is released with no non-modifier ever arriving. Only then is a
    //     modifier-only chord finished. This app supports those on purpose - the
    //     default Toggle is Ctrl+Alt, and push-to-talk takes them - so "commit on
    //     key-up of a non-modifier" would make them unrecordable.
    //
    // Waiting for the LAST key up, rather than the first, is what keeps a fumbled
    // reach for the final key from committing the prefix: releasing Shift while
    // still holding Ctrl is mid-gesture, not the end of one.
    // =========================================================================

    /// <summary>
    /// Keys held since this gesture began. The gesture ends when it empties, which
    /// is the only point at which a modifier-only chord is known to be finished.
    /// </summary>
    private readonly HashSet<Key> _heldKeys = new();

    /// <summary>
    /// The modifiers seen so far in this gesture, accumulated across key-downs so
    /// the release ORDER cannot shrink the chord: lifting Ctrl before Alt must still
    /// capture Ctrl+Alt. Null once the gesture has had its verdict.
    /// </summary>
    private KeyboardShortcut? _pending;

    /// <summary>
    /// This gesture already got a verdict (a non-modifier key completed it), so the
    /// key-ups that follow are the user letting go, not a second chord.
    /// </summary>
    private bool _gestureClosed;

    private void Field_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        e.Handled = true;

        // A held key repeating adds nothing to the chord, and re-committing on every
        // repeat would rewrite the setting and re-log for as long as a key is down.
        if (e.IsRepeat) return;

        var key = ResolveKey(e);
        if (key == Key.None) return;

        // Which modifiers are physically down RIGHT NOW, plus this key if it is one
        // itself: the key-down arrives before Keyboard's own state is updated.
        HandleKeyDown(
            key,
            control: Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) || key is Key.LeftCtrl or Key.RightCtrl,
            alt: Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt) || key is Key.LeftAlt or Key.RightAlt,
            shift: Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) || key is Key.LeftShift or Key.RightShift,
            win: Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin) || key is Key.LWin or Key.RWin);
    }

    private void Field_PreviewKeyUp(object sender, WpfKeyEventArgs e)
    {
        // Keep the capture field from leaking Win-key releases to WPF text input.
        // The global hook still controls runtime shortcut suppression.
        e.Handled = true;

        HandleKeyUp(ResolveKey(e));
    }

    /// <summary>
    /// The key-down half of the gesture, with the WPF plumbing already off it.
    /// </summary>
    /// <remarks>
    /// internal, not private, so the smoke suite can drive a whole gesture. A real
    /// KeyDown needs a PresentationSource the control only has once it is inside a
    /// shown window, which is why the same suite already pokes
    /// <see cref="ShowError"/> directly. WHEN the recorder commits is the thing that
    /// broke, so WHEN is what has to be assertable.
    /// </remarks>
    internal void HandleKeyDown(Key key, bool control, bool alt, bool shift, bool win)
    {
        if (key == Key.None) return;

        _heldKeys.Add(key);

        // Built from the modifiers held RIGHT NOW, not from the accumulated set: a
        // modifier the user let go of before pressing the final key is not part of
        // what they typed.
        var shortcut = new KeyboardShortcut { Control = control, Alt = alt, Shift = shift, Win = win };
        if (!IsModifierKey(key))
            shortcut.Key = key;

        if (!shortcut.IsModifierOnly)
        {
            // The chord is complete: nothing can be added to a chord that already has
            // its key, so this key-down is the end of the gesture.
            //
            // Forget what is still down with it. Those keys belong to a gesture that
            // is OVER, and leaving them in the set would let them hold the NEXT one
            // open past its own end: press F8 (commits F8) and then, without letting
            // F8 go, tap Alt and tap Shift - two separate taps, never held together -
            // and the still-held F8 keeps the set non-empty until it is released, at
            // which point the merged Alt+Shift commits as though it had been typed.
            _heldKeys.Clear();
            _pending = null;
            _gestureClosed = true;
            Commit(shortcut);
            return;
        }

        // Modifiers alone: the user may still be reaching for the key that finishes
        // the chord. Show what is building, decide nothing, store nothing.
        if (_gestureClosed)
        {
            _gestureClosed = false;
            _pending = null;
        }

        _pending = Merge(_pending, shortcut);
        ClearError();
        Field.Text = _pending.ToDisplayString();
    }

    /// <summary>The key-up half of the gesture. See <see cref="HandleKeyDown"/>.</summary>
    internal void HandleKeyUp(Key key)
    {
        if (key != Key.None)
            _heldKeys.Remove(key);

        // Still holding something: the gesture is not over.
        if (_heldKeys.Count > 0) return;

        if (_gestureClosed)
        {
            // A non-modifier key already ended this gesture and got its verdict.
            // These key-ups are the user letting go of a chord that was, in the case
            // this whole change exists for, REFUSED - so they must not now commit the
            // modifier prefix behind it.
            _gestureClosed = false;
            _pending = null;
            return;
        }

        if (_pending == null) return;

        var captured = _pending;
        _pending = null;
        Commit(captured);
    }

    /// <summary>
    /// The one place a captured chord is validated, rendered and reported. Reached
    /// once per gesture, never once per key.
    /// </summary>
    private void Commit(KeyboardShortcut shortcut)
    {
        // VALIDATE: reject unsafe single bare modifiers, but allow intentional
        // multi-modifier chords such as Ctrl+Win.
        //
        // The RULE and the SENTENCE both belong to ValidateActionShortcut. This
        // used to re-test IsSingleBareModifier here and carry its own copy of the
        // message, which is how one copy stayed English after the other was
        // localized (issue #516) - and how the two could disagree about which
        // chords are legal at all.
        var actionError = ShortcutValidationService.ValidateActionShortcut(shortcut);
        if (actionError != null)
        {
            ShowError(actionError);
            RestoreFieldText();
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
            RestoreFieldText();
            // The chord and the role, not the sentence: that sentence is now a
            // catalogue value, and a support log that changes language with the
            // user's display setting cannot be grepped across a corpus.
            LoggingService.Warn($"ShortcutRecorderBox: shortcut validation failed for {Role}: {shortcut}");
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

        // Unconditionally, because DisplayText may not have CHANGED - re-recording
        // the chord that is already stored is a no-op assignment, OnDisplayTextChanged
        // never fires, and the field would keep showing the half-typed preview.
        RestoreFieldText();

        ShortcutCaptured?.Invoke(this, new ShortcutCapturedEventArgs(Role, shortcut));
    }

    /// <summary>
    /// Puts the field back to the value that is actually configured, discarding the
    /// preview drawn while the chord was being typed. A refused chord must leave no
    /// trace of itself, in the field any more than in the setting.
    /// </summary>
    private void RestoreFieldText() => Field.Text = DisplayText ?? string.Empty;

    /// <summary>
    /// Focusing the field starts a new attempt, so the last one's verdict goes, and
    /// so does any gesture left half-finished by the mouse taking focus away
    /// mid-chord. The other clearing hooks are <see cref="HandleFocusLost"/>, which
    /// puts the field and the line back together when the attempt is abandoned, and
    /// <see cref="OnDisplayTextChanged"/>, for a host that re-seeds or resets the box
    /// without the user touching it.
    ///
    /// What does NOT go is the host's <see cref="StandingError"/>. That one is about
    /// the chord that is STORED, not about the last attempt, and it is still true
    /// while the user types a replacement. Clearing it here erased the Streaming
    /// page's load-time duplicate warning on the first click into the field, with
    /// nothing left to repaint it: the user read "already used by Start/Stop
    /// Recording", clicked to fix it, and the sentence and the red border both went.
    /// </summary>
    private void Field_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        HandleFocusGained();
    }

    /// <summary>
    /// Focus LEAVING ends the gesture too, and abandons it. The preview drawn while
    /// the chord was being typed must not outlive it, so the field goes back to what
    /// is actually configured.
    ///
    /// Without this, <see cref="RestoreFieldText"/> was reachable only from
    /// <see cref="Commit"/>, so a gesture nobody finished left its preview in the
    /// field for good: hold Ctrl+Shift, click another control, and the field reads
    /// "Ctrl+Shift" for the rest of the page visit while settings.json and the
    /// registered hotkey are still the real chord. On the pages this was lifted from
    /// the field was written only on the line that SAVED, so no page could show an
    /// unsaved value; a control that previews mid-gesture can, and has to undo it.
    ///
    /// The GESTURE's verdict goes with it, and the host's <see cref="StandingError"/>
    /// shows through again. Round 1 kept the verdict here, reasoning that a user told
    /// why a chord was refused does not stop needing the reason because they looked
    /// away. The line above is what settles it: this method has ALREADY called
    /// <see cref="RestoreFieldText"/>, so the refused chord is not on screen for that
    /// sentence to be about. What is on screen is the chord that is STORED - and the
    /// verdict about THAT is the standing one, which the stale sentence was sitting on
    /// top of. Streaming and Toggle both Ctrl+Shift+Space, a fumbled lone Ctrl, a click
    /// elsewhere, and the box read "Ctrl+Shift+Space" under "Single modifier shortcuts
    /// are not supported" with the duplicate warning gone, for the rest of the visit.
    ///
    /// The invariant, in one line: THE ERROR LINE AND THE FIELD TEXT DESCRIBE THE SAME
    /// THING. Restoring one without the other is what broke it.
    ///
    /// This is also the only hook that catches the WINDOW losing activation
    /// mid-gesture, where the key-ups never arrive at this control at all.
    /// </summary>
    private void Field_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        HandleFocusLost();
    }

    /// <summary>The focus-arriving half, with the WPF plumbing off it.</summary>
    /// <remarks>
    /// internal, not private, for the same reason as <see cref="HandleKeyDown"/>:
    /// real keyboard focus needs a PresentationSource the control only has inside a
    /// shown window, and WHAT SURVIVES a focus change is exactly what broke.
    /// </remarks>
    internal void HandleFocusGained()
    {
        ResetGesture();
        ClearError();
    }

    /// <summary>The focus-leaving half. See <see cref="HandleFocusGained"/>.</summary>
    internal void HandleFocusLost()
    {
        ResetGesture();

        // Both halves of what is on screen, put back together. RestoreFieldText alone
        // left the line describing a chord it had just taken out of the field.
        RestoreFieldText();
        ClearError();
    }

    private void ResetGesture()
    {
        _heldKeys.Clear();
        _pending = null;
        _gestureClosed = false;
    }

    private static KeyboardShortcut Merge(KeyboardShortcut? accumulated, KeyboardShortcut next) => new()
    {
        Control = (accumulated?.Control ?? false) || next.Control,
        Alt = (accumulated?.Alt ?? false) || next.Alt,
        Shift = (accumulated?.Shift ?? false) || next.Shift,
        Win = (accumulated?.Win ?? false) || next.Win
    };

    /// <summary>Alt chords arrive as <see cref="Key.System"/> with the real key on SystemKey.</summary>
    private static Key ResolveKey(WpfKeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key;

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    // =========================================================================
    // ERROR DISPLAY
    //
    // TWO verdicts want the same line, and they have different lifetimes:
    //
    //   - the GESTURE's, from Commit: "the chord you just typed is refused". It
    //     belongs to one attempt and dies with it - on focus ARRIVING, on focus
    //     LEAVING, on a re-seeded DisplayText, and on the next key-down. Every one of
    //     those is a point at which the field stops showing the chord it was about.
    //   - the host's STANDING one, from StandingError: "the chord that is STORED is
    //     already someone else's". It is true before the user touches anything and
    //     stays true until the stored chord changes.
    //
    // They are separate fields and everything paints through RenderError, so neither
    // can erase the other. Collapsing them into one string is what #794's round-1
    // review found: the Streaming page rendered its load-time duplicate through
    // ShowError, the recorder cleared that on focus as designed, and the only warning
    // this app gives about a conflict already in settings.json vanished on the first
    // click with nothing left to repaint it.
    //
    // The gesture's wins while it exists, because it answers what the user JUST did.
    // =========================================================================

    /// <summary>The verdict about the chord this gesture typed, or null.</summary>
    private string? _gestureError;

    /// <summary>The host's verdict about the chord that is stored, or null.</summary>
    private string? _standingError;

    /// <summary>
    /// A verdict the HOST holds about the chord that is currently STORED, rather than
    /// about one the user just typed. Set null to withdraw it.
    ///
    /// This is the seam a host renders through, and the only one it should touch:
    /// <see cref="ShowError"/> and <see cref="ClearError"/> are the recorder's own,
    /// and are internal for the smoke suite. Production code calling those fought the
    /// control for the line, because the control clears its own verdict on focus and
    /// on a re-seeded DisplayText and cannot tell a host's sentence from its own.
    ///
    /// Withdrawn automatically when <see cref="DisplayText"/> changes: a verdict about
    /// the stored chord cannot outlive the stored chord. There is one producer today -
    /// StreamingSettingsPage's load-time duplicate check, which is the only thing in
    /// this app that reports a conflict that was already in settings.json, because the
    /// recorder validates on capture and not on load.
    /// </summary>
    public string? StandingError
    {
        get => _standingError;
        set
        {
            _standingError = value;
            RenderError();
        }
    }

    /// <summary>
    /// The red border and the reason are ONE thing, and are drawn together.
    ///
    /// They came apart once: the border was painted unconditionally while the text
    /// was gated on <see cref="ShowsInlineError"/>, so the one box declared
    /// ShowsInlineError="False" - push-to-talk custom - turned red with nothing
    /// anywhere saying why. The old page it was lifted from drew NEITHER for that
    /// role, so the field went from silent to unexplained.
    ///
    /// The fix is the pairing, not the suppression: a host that does not want the
    /// line does not want the border either. ShowsInlineError="False" has no user
    /// left (the push-to-talk box now shows its reason like the other six), but the
    /// property stays, and this method is what stops it drifting apart again.
    /// </summary>
    /// <remarks>
    /// internal, not private, so the smoke suite can assert the pairing directly.
    /// Driving it through a real KeyDown needs a PresentationSource the control has
    /// only once it is in a shown window, and the pairing - not the key handling -
    /// is what came apart.
    ///
    /// A HOST must not call this. It sets the GESTURE's verdict, which this control
    /// owns and clears on focus and on a re-seeded DisplayText; a host's sentence
    /// goes on <see cref="StandingError"/> and survives both.
    /// </remarks>
    internal void ShowError(string message)
    {
        _gestureError = message;
        RenderError();
    }

    internal void ClearError()
    {
        _gestureError = null;
        RenderError();
    }

    /// <summary>
    /// The one place either verdict reaches the screen. The gesture's own wins while
    /// it exists; the host's standing one shows through underneath it, and is what is
    /// left when the gesture's is cleared.
    /// </summary>
    private void RenderError()
    {
        var message = _gestureError ?? _standingError;
        ErrorMessage = message;

        if (message == null)
        {
            ErrorText.Text = string.Empty;
            ErrorText.Visibility = Visibility.Collapsed;
            Field.ClearValue(ToolTipProperty);
            // Control.*, not Border.*. The page this was lifted from cleared
            // Border.BorderBrushProperty on a TextBox, which is a DIFFERENT dependency
            // property from the Control.BorderBrush the TextBox actually renders, so the
            // red border it set on a rejected chord never came off again. One line, and
            // the whole reason to extract this rather than copy it twice more.
            Field.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
            Field.ClearValue(System.Windows.Controls.Control.BorderThicknessProperty);
            return;
        }

        if (!ShowsInlineError)
        {
            // No line means no border. The reason still has to be reachable, so it
            // goes on the field itself.
            Field.ToolTip = message;
            return;
        }

        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;

        Field.BorderBrush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xFF, 0x55, 0x55));
        Field.BorderThickness = new Thickness(2);
    }
}
