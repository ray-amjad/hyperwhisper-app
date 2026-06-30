using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.Services;

namespace HyperWhisper.Views.Windows;

/// <summary>
/// RECORDING OVERLAY WINDOW - WPF Version
///
/// A compact pill-shaped always-on-top window that matches the macOS design.
/// Features:
/// - Stop button (red circle with stop icon)
/// - Mode badge (capsule showing current mode name)
/// - Animated vertical bar waveform (center-peaked)
///
/// WAVEFORM ANIMATION:
/// - 25 vertical bars matching macOS
/// - Center-peaked amplitude envelope
/// - Phase-shifted sine wave animation
/// - Amplitude reacts to audio level
/// - 60 FPS refresh rate
///
/// STATES:
/// - Recording: Stop button + mode badge + animated waveform
/// - Transcribing: Spinner with status text
/// - Success: Checkmark with "Pasted!" text
/// - CancelConfirmation: "Cancel?" text with No/Yes buttons
///
/// ESCAPE KEY HANDLING:
/// - When recording: triggers cancel flow (may show confirmation if > 15s)
/// - When cancel confirmation visible: dismisses confirmation (resumes recording)
///
/// ENTER KEY HANDLING:
/// - When cancel confirmation visible: confirms cancellation
/// </summary>
public partial class RecordingOverlayWindow : Window
{
    // ========================================================================
    // WIN32 API - NON-ACTIVATING WINDOW
    // ========================================================================

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    // ========================================================================
    // WAVEFORM PARAMETERS
    // ========================================================================

    private const int BAR_COUNT = 25;
    private const int BAR_WIDTH = 2;
    private const int BAR_SPACING = 2;
    private const int MAX_BAR_HEIGHT = 20;
    private const int MIN_BAR_HEIGHT = 6;

    // AMPLITUDE BOOST: Increased from 0.70/0.50 to make waveform more responsive
    private const float ATTACK_SMOOTHING = 0.85f;
    private const float RELEASE_SMOOTHING = 0.60f;

    // AudioRecorderService already emits a perceptually-scaled 0..1 level.
    private const float AUDIO_LEVEL_GAIN = 1.0f;

    // ========================================================================
    // STATE
    // ========================================================================

    private readonly DispatcherTimer _animationTimer;
    private readonly WpfRectangle[] _bars;
    private readonly float[] _barValues;
    private readonly float[] _envelope;
    private readonly float[] _phaseOffsets;

    private float _smoothedLevel;
    private float _targetLevel;
    private float _phase;

    // Spinner animation
    private Storyboard? _spinnerStoryboard;

    /// <summary>Event fired when stop button is clicked.</summary>
    public event EventHandler? StopClicked;

    /// <summary>
    /// Event fired when Escape is pressed during recording state.
    /// ViewModel should check duration and decide whether to show confirmation or cancel immediately.
    /// </summary>
    public event EventHandler? EscapePressed;

    /// <summary>
    /// Event fired when user confirms cancellation (clicks Yes or presses Enter on confirmation).
    /// </summary>
    public event EventHandler? CancelConfirmed;

    /// <summary>
    /// Event fired when user dismisses confirmation (clicks No or presses Escape on confirmation).
    /// </summary>
    public event EventHandler? CancelDismissed;

    /// <summary>
    /// Tracks whether the cancel confirmation UI is currently visible.
    /// Used for keyboard event routing.
    /// </summary>
    private bool _isShowingCancelConfirmation;

    /// <summary>
    /// Debounce timer for saving overlay position during drag.
    /// LocationChanged fires per-pixel, so we coalesce writes with a short delay.
    /// </summary>
    private DispatcherTimer? _positionSaveTimer;

    /// <summary>
    /// Event handler for mode selection changes.
    /// Updates the mode badge when user changes mode via tray menu or keyboard shortcut.
    /// </summary>
    private readonly EventHandler<Mode> _modeSelectedHandler;

    public RecordingOverlayWindow()
    {
        InitializeComponent();

        // Initialize waveform arrays
        _bars = new WpfRectangle[BAR_COUNT];
        _barValues = new float[BAR_COUNT];
        _envelope = new float[BAR_COUNT];
        _phaseOffsets = new float[BAR_COUNT];

        InitializeWaveform();

        // Setup animation timer (60 FPS)
        _animationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _animationTimer.Tick += AnimationTimer_Tick;

        // Setup spinner animation
        SetupSpinnerAnimation();

        // Setup keyboard event handling for Escape/Enter
        PreviewKeyDown += RecordingOverlayWindow_PreviewKeyDown;

        // Enable drag-to-move anywhere on the window
        MouseLeftButtonDown += Window_MouseLeftButtonDown;

        // Save position when dragged (debounced)
        LocationChanged += OnLocationChanged;

        // Subscribe to mode selection changes to update badge in real-time
        _modeSelectedHandler = (s, mode) =>
            Dispatcher.Invoke(() =>
            {
                ModeBadge.Text = mode.Name;
                AnimateModeBadge();
            });
        ModeService.Instance.ModeSelected += _modeSelectedHandler;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
        LoggingService.Debug("RecordingOverlayWindow: Set WS_EX_NOACTIVATE");
    }

    private void AnimateModeBadge()
    {
        var badgeBorder = ModeBadge.Parent as System.Windows.Controls.Border;
        if (badgeBorder == null) return;

        var scaleTransform = new ScaleTransform(1.0, 1.0);
        badgeBorder.RenderTransform = scaleTransform;
        badgeBorder.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

        var storyboard = new Storyboard();

        var scaleXUp = new DoubleAnimation(1.0, 1.2, TimeSpan.FromMilliseconds(150))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(scaleXUp, badgeBorder);
        Storyboard.SetTargetProperty(scaleXUp, new PropertyPath("RenderTransform.ScaleX"));

        var scaleYUp = new DoubleAnimation(1.0, 1.2, TimeSpan.FromMilliseconds(150))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(scaleYUp, badgeBorder);
        Storyboard.SetTargetProperty(scaleYUp, new PropertyPath("RenderTransform.ScaleY"));

        var scaleXDown = new DoubleAnimation(1.2, 1.0, TimeSpan.FromMilliseconds(150))
        { BeginTime = TimeSpan.FromMilliseconds(150), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        Storyboard.SetTarget(scaleXDown, badgeBorder);
        Storyboard.SetTargetProperty(scaleXDown, new PropertyPath("RenderTransform.ScaleX"));

        var scaleYDown = new DoubleAnimation(1.2, 1.0, TimeSpan.FromMilliseconds(150))
        { BeginTime = TimeSpan.FromMilliseconds(150), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        Storyboard.SetTarget(scaleYDown, badgeBorder);
        Storyboard.SetTargetProperty(scaleYDown, new PropertyPath("RenderTransform.ScaleY"));

        storyboard.Children.Add(scaleXUp);
        storyboard.Children.Add(scaleYUp);
        storyboard.Children.Add(scaleXDown);
        storyboard.Children.Add(scaleYDown);

        storyboard.Begin();
    }

    private void InitializeWaveform()
    {
        var random = new Random();
        float center = (BAR_COUNT - 1) / 2.0f;

        for (int i = 0; i < BAR_COUNT; i++)
        {
            // Initialize bar values
            _barValues[i] = 0.0f;

            // Center-peaked envelope
            float dist = Math.Abs(i - center) / Math.Max(center, 1);
            float v = 1.0f - dist;
            _envelope[i] = v * v;

            // Phase offsets for wave propagation
            _phaseOffsets[i] = i * 0.30f + (float)(random.NextDouble() * 2 * Math.PI);

            // Create bar rectangle
            var bar = new WpfRectangle
            {
                Width = BAR_WIDTH,
                Height = MIN_BAR_HEIGHT,
                Fill = new SolidColorBrush(WpfColor.FromArgb(102, 255, 255, 255)),
                RadiusX = 1,
                RadiusY = 1
            };

            _bars[i] = bar;
            WaveformCanvas.Children.Add(bar);
        }
    }

    private void PositionOverlay()
    {
        var screen = SystemParameters.WorkArea;
        var xRatio = SettingsService.Instance.RecordingOverlayXRatio;
        var yRatio = SettingsService.Instance.RecordingOverlayYRatio;

        if (xRatio >= 0 && yRatio >= 0)
        {
            // Restore from saved ratios
            double x = xRatio * screen.Width;
            double y = yRatio * screen.Height;

            // Validate: ensure center point is on-screen
            double centerX = x + Width / 2;
            double centerY = y + Height / 2;

            if (centerX >= screen.Left && centerX <= screen.Right &&
                centerY >= screen.Top && centerY <= screen.Bottom)
            {
                Left = x;
                Top = y;
                return;
            }
        }

        // Default: bottom center with 20px margin
        Left = (screen.Width - Width) / 2;
        Top = screen.Height - Height - 20;
    }

    private void SetupSpinnerAnimation()
    {
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(1),
            RepeatBehavior = RepeatBehavior.Forever
        };

        _spinnerStoryboard = new Storyboard();
        _spinnerStoryboard.Children.Add(animation);
        Storyboard.SetTarget(animation, Spinner);
        Storyboard.SetTargetProperty(animation,
            new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
    }

    // ========================================================================
    // PUBLIC METHODS
    // ========================================================================

    /// <summary>
    /// Updates the audio level for waveform amplitude.
    /// </summary>
    public void UpdateAudioLevel(float level)
    {
        // Apply gain boost to make waveform more responsive to speech
        _targetLevel = Math.Clamp(level * AUDIO_LEVEL_GAIN, 0.0f, 1.0f);
    }

    /// <summary>
    /// Sets the mode name displayed in the badge.
    /// </summary>
    public void SetModeName(string modeName)
    {
        ModeBadge.Text = modeName;
    }

    /// <summary>
    /// Shows the overlay in recording state.
    /// </summary>
    public void ShowRecording()
    {
        MainBorder.BorderBrush = new SolidColorBrush(WpfColor.FromArgb(26, 255, 255, 255));
        StreamingDot.Visibility = Visibility.Collapsed;
        _smoothedLevel = 0;
        _targetLevel = 0;
        _phase = 0;

        for (int i = 0; i < BAR_COUNT; i++)
        {
            _barValues[i] = 0.0f;
        }

        RecordingState.Visibility = Visibility.Visible;
        TranscribingState.Visibility = Visibility.Collapsed;
        SuccessState.Visibility = Visibility.Collapsed;
        CopiedState.Visibility = Visibility.Collapsed;

        _spinnerStoryboard?.Stop();
        _animationTimer.Start();

        // Restore saved position (or default) each time we show
        PositionOverlay();

        Show();
        LoggingService.Debug("RecordingOverlayWindow: Shown in recording state");
    }

    /// <summary>
    /// Shows the overlay in streaming state.
    /// </summary>
    public void ShowStreaming(string providerName)
    {
        ShowRecording();
        StreamingDot.Visibility = Visibility.Visible;
        ModeBadge.Text = Loc.S("recording.state.streaming");
        ToolTip = providerName;
        UpdateStreamingConnectionState(StreamingConnectionState.Streaming);
        LoggingService.Debug($"RecordingOverlayWindow: Shown in streaming state ({providerName})");
    }

    /// <summary>
    /// Updates streaming connection indicator color and pulse state.
    /// </summary>
    public void UpdateStreamingConnectionState(StreamingConnectionState state)
    {
        if (StreamingDot.Visibility != Visibility.Visible)
            return;

        var (color, pulse) = state switch
        {
            StreamingConnectionState.Connecting or StreamingConnectionState.Ready =>
                (WpfColor.FromRgb(255, 204, 0), true),
            StreamingConnectionState.Reconnecting =>
                (WpfColor.FromRgb(255, 149, 0), true),
            StreamingConnectionState.Error =>
                (WpfColor.FromRgb(255, 59, 48), false),
            StreamingConnectionState.Disconnecting or StreamingConnectionState.Idle =>
                (WpfColor.FromRgb(142, 142, 147), false),
            _ =>
                (WpfColor.FromRgb(52, 199, 89), false)
        };

        StreamingDot.Fill = new SolidColorBrush(color);
        MainBorder.BorderBrush = new SolidColorBrush(WpfColor.FromArgb(153, color.R, color.G, color.B));

        if (pulse)
        {
            var animation = new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(600))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            StreamingDot.BeginAnimation(UIElement.OpacityProperty, animation);
        }
        else
        {
            StreamingDot.BeginAnimation(UIElement.OpacityProperty, null);
            StreamingDot.Opacity = 1.0;
        }
    }

    /// <summary>
    /// Switches to transcribing state.
    /// </summary>
    public void ShowTranscribing()
    {
        StatusText.Text = Loc.S("recording.state.transcribing");
        ShowStatusState();
    }

    /// <summary>
    /// Shows a custom status message.
    /// </summary>
    public void ShowStatus(string message)
    {
        StatusText.Text = message;
        ShowStatusState();
    }

    private void ShowStatusState()
    {
        Dispatcher.Invoke(() =>
        {
            RecordingState.Visibility = Visibility.Collapsed;
            TranscribingState.Visibility = Visibility.Visible;
            SuccessState.Visibility = Visibility.Collapsed;
            CopiedState.Visibility = Visibility.Collapsed;

            _animationTimer.Stop();
            _spinnerStoryboard?.Begin();
        });

        LoggingService.Debug($"RecordingOverlayWindow: Showing status '{StatusText.Text}'");
    }

    /// <summary>
    /// Shows success state.
    /// </summary>
    public void ShowSuccess()
    {
        Dispatcher.Invoke(() =>
        {
            RecordingState.Visibility = Visibility.Collapsed;
            TranscribingState.Visibility = Visibility.Collapsed;
            SuccessState.Visibility = Visibility.Visible;
            CopiedState.Visibility = Visibility.Collapsed;

            _animationTimer.Stop();
            _spinnerStoryboard?.Stop();
        });

        LoggingService.Debug("RecordingOverlayWindow: Showing success state");
    }

    /// <summary>
    /// Shows copied state (blue clipboard icon).
    /// Used when text was copied to clipboard but not pasted (e.g., password field detected).
    /// </summary>
    public void ShowCopied()
    {
        Dispatcher.Invoke(() =>
        {
            RecordingState.Visibility = Visibility.Collapsed;
            TranscribingState.Visibility = Visibility.Collapsed;
            SuccessState.Visibility = Visibility.Collapsed;
            CopiedState.Visibility = Visibility.Visible;

            _animationTimer.Stop();
            _spinnerStoryboard?.Stop();
        });

        LoggingService.Debug("RecordingOverlayWindow: Showing copied state");
    }

    /// <summary>
    /// Hides the overlay.
    /// </summary>
    public new void Hide()
    {
        _animationTimer.Stop();
        _spinnerStoryboard?.Stop();
        _isShowingCancelConfirmation = false;
        base.Hide();
        LoggingService.Debug("RecordingOverlayWindow: Hidden");
    }

    /// <summary>
    /// Shows the cancel confirmation UI.
    /// Replaces the recording state with "Cancel?" prompt and No/Yes buttons.
    /// Matches macOS RecordingDialog.cancelConfirmationView design.
    /// </summary>
    public void ShowCancelConfirmation()
    {
        Dispatcher.Invoke(() =>
        {
            _isShowingCancelConfirmation = true;

            // Hide recording state, show confirmation
            RecordingState.Visibility = Visibility.Collapsed;
            TranscribingState.Visibility = Visibility.Collapsed;
            SuccessState.Visibility = Visibility.Collapsed;
            CopiedState.Visibility = Visibility.Collapsed;
            CancelConfirmationState.Visibility = Visibility.Visible;

            // Stop waveform animation while showing confirmation
            _animationTimer.Stop();

            // Temporarily remove WS_EX_NOACTIVATE so the window can receive keyboard input for Yes/No
            var helper = new WindowInteropHelper(this);
            int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
            SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle & ~WS_EX_NOACTIVATE);
            Activate();
            SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
        });

        LoggingService.Debug("RecordingOverlayWindow: Showing cancel confirmation");
    }

    /// <summary>
    /// Hides the cancel confirmation UI and returns to recording state.
    /// Called when user dismisses confirmation (presses No or Escape).
    /// </summary>
    public void HideCancelConfirmation()
    {
        Dispatcher.Invoke(() =>
        {
            _isShowingCancelConfirmation = false;

            // Hide confirmation, restore recording state
            CancelConfirmationState.Visibility = Visibility.Collapsed;
            RecordingState.Visibility = Visibility.Visible;
            TranscribingState.Visibility = Visibility.Collapsed;
            SuccessState.Visibility = Visibility.Collapsed;
            CopiedState.Visibility = Visibility.Collapsed;

            // Resume waveform animation
            _animationTimer.Start();
        });

        LoggingService.Debug("RecordingOverlayWindow: Hidden cancel confirmation, resumed recording");
    }

    // ========================================================================
    // ANIMATION
    // ========================================================================

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        UpdateWaveform();
        RenderWaveform();
    }

    private void UpdateWaveform()
    {
        // Attack/Release smoothing
        if (_targetLevel > _smoothedLevel)
        {
            _smoothedLevel = _smoothedLevel * (1 - ATTACK_SMOOTHING) + _targetLevel * ATTACK_SMOOTHING;
        }
        else
        {
            _smoothedLevel = _smoothedLevel * RELEASE_SMOOTHING + _targetLevel * (1 - RELEASE_SMOOTHING);
        }

        // Progress animation phase
        float amp = Math.Clamp(_smoothedLevel, 0, 1);
        float speed = 0.08f + 0.24f * amp;
        _phase += speed;

        // Update each bar value
        for (int i = 0; i < BAR_COUNT; i++)
        {
            float env = _envelope[i];
            float wave = ((float)Math.Sin(_phase + _phaseOffsets[i]) + 1.0f) * 0.5f;

            float minHeight = 0.25f;
            float gain = 0.30f + 0.70f * amp;
            float target = minHeight + env * gain * (0.40f + 0.60f * wave);

            _barValues[i] = _barValues[i] * 0.35f + target * 0.65f;
        }
    }

    private void RenderWaveform()
    {
        double canvasWidth = WaveformCanvas.ActualWidth;
        double canvasHeight = WaveformCanvas.ActualHeight;

        if (canvasWidth <= 0 || canvasHeight <= 0) return;

        int totalBarWidth = BAR_WIDTH + BAR_SPACING;
        int visibleBars = Math.Min(BAR_COUNT, (int)(canvasWidth / totalBarWidth));

        for (int i = 0; i < BAR_COUNT; i++)
        {
            if (i >= visibleBars)
            {
                _bars[i].Visibility = Visibility.Collapsed;
                continue;
            }

            _bars[i].Visibility = Visibility.Visible;

            float value = _barValues[i];
            double barHeight = Math.Max(MIN_BAR_HEIGHT, value * MAX_BAR_HEIGHT);

            _bars[i].Height = barHeight;
            Canvas.SetLeft(_bars[i], i * totalBarWidth);
            Canvas.SetTop(_bars[i], (canvasHeight - barHeight) / 2);
        }
    }

    // ========================================================================
    // EVENT HANDLERS
    // ========================================================================

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        var workArea = SystemParameters.WorkArea;
        if (workArea.Width <= 0 || workArea.Height <= 0) return;

        // Debounce: restart timer on each move to coalesce rapid updates
        if (_positionSaveTimer == null)
        {
            _positionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _positionSaveTimer.Tick += (s, args) =>
            {
                _positionSaveTimer.Stop();

                var wa = SystemParameters.WorkArea;
                if (wa.Width <= 0 || wa.Height <= 0) return;

                SettingsService.Instance.RecordingOverlayXRatio = Left / wa.Width;
                SettingsService.Instance.RecordingOverlayYRatio = Top / wa.Height;
            };
        }

        _positionSaveTimer.Stop();
        _positionSaveTimer.Start();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopClicked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Handles keyboard events for cancel flow.
    /// - Escape during recording: triggers cancel request
    /// - Escape during confirmation: dismisses confirmation
    /// - Enter during confirmation: confirms cancellation
    /// </summary>
    private void RecordingOverlayWindow_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;

            if (_isShowingCancelConfirmation)
            {
                // Escape on confirmation = dismiss (resume recording)
                LoggingService.Debug("RecordingOverlayWindow: Escape pressed on confirmation - dismissing");
                CancelDismissed?.Invoke(this, EventArgs.Empty);
            }
            else if (RecordingState.Visibility == Visibility.Visible)
            {
                // Escape during recording = trigger cancel flow
                LoggingService.Debug("RecordingOverlayWindow: Escape pressed during recording");
                EscapePressed?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            if (_isShowingCancelConfirmation)
            {
                e.Handled = true;
                // Enter on confirmation = confirm cancellation
                LoggingService.Debug("RecordingOverlayWindow: Enter pressed on confirmation - confirming");
                CancelConfirmed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Called when No button is clicked on cancel confirmation.
    /// Dismisses confirmation and resumes recording.
    /// </summary>
    private void NoButton_Click(object sender, RoutedEventArgs e)
    {
        LoggingService.Debug("RecordingOverlayWindow: No button clicked - dismissing confirmation");
        CancelDismissed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called when Yes button is clicked on cancel confirmation.
    /// Confirms cancellation and discards recording.
    /// </summary>
    private void YesButton_Click(object sender, RoutedEventArgs e)
    {
        LoggingService.Debug("RecordingOverlayWindow: Yes button clicked - confirming cancellation");
        CancelConfirmed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClosed(EventArgs e)
    {
        // CLEANUP: Stop timers and animations when window closes
        // This prevents resource leaks and ensures clean shutdown
        _animationTimer.Stop();
        _spinnerStoryboard?.Stop();
        _positionSaveTimer?.Stop();

        // Unsubscribe from mode events to prevent memory leaks
        ModeService.Instance.ModeSelected -= _modeSelectedHandler;

        base.OnClosed(e);
    }
}
