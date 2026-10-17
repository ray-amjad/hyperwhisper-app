using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HyperWhisper.Services;
using HyperWhisper.Utilities;

namespace HyperWhisper.Views.Windows;

/// <summary>
/// FILE TRANSCRIPTION PROGRESS WINDOW
///
/// A compact pill-shaped window that displays file transcription progress.
/// Features:
/// - File name display with truncation
/// - Animated progress bar (0-100%)
/// - Percentage text
/// - Cancel button
///
/// ANIMATION:
/// - 60 FPS refresh rate using DispatcherTimer
/// - Smooth easeOutCubic easing
/// - Variable duration based on progress delta
/// - A ramp started with thenIndeterminate (the open-ended transcribing wait)
///   turns the bar indeterminate when it reaches its target, so a long job
///   shows a moving bar with no percentage instead of sitting at 80% (#1911)
///
/// WINDOW BEHAVIOR:
/// - Non-activating (doesn't steal focus) using Win32 API
/// - Centered on screen
/// - Always on top
/// - Dark theme matching RecordingOverlayWindow
/// </summary>
public partial class FileTranscriptionProgressWindow : Window
{
    // ========================================================================
    // ANIMATION PARAMETERS
    // ========================================================================

    private const double ANIMATION_FPS = 60;
    private const double ANIMATION_INTERVAL_MS = 1000.0 / ANIMATION_FPS;

    // ========================================================================
    // STATE
    // ========================================================================

    private readonly DispatcherTimer _animationTimer;
    private float _currentProgress = 0f;
    private float _targetProgress = 0f;
    private DateTime _animationStartTime;
    private double _animationDuration = 0;
    private double _progressBarWidth = 0;

    // INDETERMINATE: no engine reports progress while it transcribes, so once the
    // timed ramp ends the bar sweeps instead of claiming a number (#1911, as #1437 on macOS)
    private const double INDETERMINATE_SWEEP_PERIOD_S = 1.4;
    private const double INDETERMINATE_PULSE_PERIOD_S = 2.0;
    private const double INDETERMINATE_SEGMENT_FRACTION = 0.3;
    private readonly DispatcherTimer _indeterminateTimer;
    private readonly TranslateTransform _fillOffset = new();
    private DateTime _indeterminateStartTime;
    private bool _becomesIndeterminateAtTarget = false;
    private bool _isIndeterminate = false;

    /// <summary>Event fired when cancel button is clicked.</summary>
    public event EventHandler? CancelRequested;

    /// <summary>Current progress value (0.0 to 1.0) for external access.</summary>
    public float CurrentProgress => _currentProgress;

    /// <summary>Whether the bar no longer shows how far along the job is.</summary>
    public bool IsIndeterminate => _isIndeterminate;

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
    // CONSTRUCTOR
    // ========================================================================

    public FileTranscriptionProgressWindow()
    {
        InitializeComponent();

        // Setup animation timer (60 FPS)
        _animationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(ANIMATION_INTERVAL_MS)
        };
        _animationTimer.Tick += AnimationTimer_Tick;

        _indeterminateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(ANIMATION_INTERVAL_MS)
        };
        _indeterminateTimer.Tick += (s, e) => UpdateIndeterminateUI();
        ProgressFill.RenderTransform = _fillOffset;

        // Centred on the primary monitor when shown (ShowProgress), and again
        // when its DPI changes: a long job can outlive a display-scale change, and
        // this window cannot be dragged (issue #1501). Deferred, because WPF moves
        // the window to the rectangle Windows suggests after it raises DpiChanged.
        DpiChanged += (s, e) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (IsVisible) PositionWindow();
        }));

        // Make window non-activating after it's loaded
        Loaded += (s, e) => MakeNonActivating();

        // Update progress bar width after layout
        ProgressBarTrack.SizeChanged += (s, e) =>
        {
            _progressBarWidth = ProgressBarTrack.ActualWidth;
            // Clip to the track's rounded shape, so the indeterminate segment
            // enters and leaves through the rounded ends, not square ones
            ProgressBarTrack.Clip = new RectangleGeometry(
                new Rect(0, 0, ProgressBarTrack.ActualWidth, ProgressBarTrack.ActualHeight),
                ProgressBarTrack.CornerRadius.TopLeft, ProgressBarTrack.CornerRadius.TopLeft);
        };
    }

    // ========================================================================
    // PUBLIC METHODS
    // ========================================================================

    /// <summary>
    /// Shows the progress window with file name and cancel handler.
    /// </summary>
    public void ShowProgress(string fileName, Action onCancel)
    {
        FileNameText.Text = fileName;
        StopIndeterminate();
        _currentProgress = 0f;
        _targetProgress = 0f;
        UpdateProgressUI(0f);

        // Store cancel handler
        CancelRequested = null;
        if (onCancel != null)
        {
            CancelRequested += (s, e) => onCancel();
        }

        PositionWindow();
        Show();
        LoggingService.Debug($"FileTranscriptionProgressWindow: Shown - {fileName}");
    }

    /// <summary>
    /// Animates progress to target value over specified duration.
    /// </summary>
    /// <param name="thenIndeterminate">Switch the bar to indeterminate when the target is
    /// reached before a later call moves it on (the open-ended transcribing wait).</param>
    public void AnimateProgress(float targetProgress, double duration, bool thenIndeterminate = false)
    {
        StopIndeterminate();
        _becomesIndeterminateAtTarget = thenIndeterminate;
        _targetProgress = Math.Clamp(targetProgress, 0f, 1f);
        _animationDuration = duration;
        _animationStartTime = DateTime.Now;

        if (_animationTimer.IsEnabled)
        {
            _animationTimer.Stop();
        }

        if (duration > 0 && Math.Abs(_targetProgress - _currentProgress) > 0.001f)
        {
            _animationTimer.Start();
            LoggingService.Debug($"FileTranscriptionProgressWindow: Animating to {_targetProgress * 100:F0}% over {duration:F1}s");
        }
        else
        {
            // Instant update for zero duration or no change
            _currentProgress = _targetProgress;
            UpdateProgressUI(_currentProgress);
            EnterIndeterminateIfRequested();
        }
    }

    /// <summary>
    /// Instantly sets progress to target value (no animation).
    /// </summary>
    public void SetProgress(float progress)
    {
        _animationTimer.Stop();
        StopIndeterminate();
        _currentProgress = Math.Clamp(progress, 0f, 1f);
        _targetProgress = _currentProgress;
        UpdateProgressUI(_currentProgress);
    }

    /// <summary>
    /// Dismisses the progress window.
    /// </summary>
    public void Dismiss()
    {
        _animationTimer.Stop();
        StopIndeterminate();
        CancelRequested = null;
        Hide();
        LoggingService.Debug("FileTranscriptionProgressWindow: Dismissed");
    }

    // ========================================================================
    // PRIVATE METHODS
    // ========================================================================

    private void PositionWindow()
    {
        // The primary monitor's work area in its own DIP, not
        // SystemParameters.WorkArea, which keeps the DPI the app started at.
        MonitorWorkArea.Place(this, MonitorWorkArea.Primary, screen =>
            new System.Windows.Point((screen.Width - Width) / 2, (screen.Height - Height) / 2));
    }

    private void MakeNonActivating()
    {
        var helper = new WindowInteropHelper(this);
        if (helper.Handle != IntPtr.Zero)
        {
            int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
            SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
            LoggingService.Debug("FileTranscriptionProgressWindow: Set WS_EX_NOACTIVATE");
        }
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e) => AdvanceAnimation(DateTime.Now);

    /// <summary>One animation frame at <paramref name="now"/>; the timer passes the clock.</summary>
    internal void AdvanceAnimation(DateTime now)
    {
        if (!_animationTimer.IsEnabled) return;
        double elapsed = (now - _animationStartTime).TotalSeconds;
        double normalizedTime = Math.Min(elapsed / _animationDuration, 1.0);
        double easedTime = EaseOutCubic(normalizedTime);

        float newProgress = _currentProgress + (float)((_targetProgress - _currentProgress) * easedTime);
        UpdateProgressUI(newProgress);

        if (normalizedTime >= 1.0)
        {
            _animationTimer.Stop();
            _currentProgress = _targetProgress;
            UpdateProgressUI(_currentProgress);
            EnterIndeterminateIfRequested();
        }
    }

    private void EnterIndeterminateIfRequested()
    {
        if (!_becomesIndeterminateAtTarget) return;
        _becomesIndeterminateAtTarget = false;
        _isIndeterminate = true;
        _indeterminateStartTime = DateTime.Now;
        PercentageText.Text = string.Empty;
        UpdateIndeterminateUI();
        _indeterminateTimer.Start();
        LoggingService.Debug("FileTranscriptionProgressWindow: Ramp ended with the job still running, bar is indeterminate");
    }

    private void StopIndeterminate()
    {
        _becomesIndeterminateAtTarget = false;
        if (!_isIndeterminate) return;
        _isIndeterminate = false;
        _indeterminateTimer.Stop();
        _fillOffset.X = 0;
        ProgressFill.Opacity = 1.0;
        UpdateProgressUI(_currentProgress);
    }

    /// <summary>
    /// A segment sweeps across the track. With Windows animation effects off, the
    /// full track pulses in place instead.
    /// </summary>
    private void UpdateIndeterminateUI()
    {
        double elapsed = (DateTime.Now - _indeterminateStartTime).TotalSeconds;
        if (SystemParameters.ClientAreaAnimation)
        {
            double phase = (elapsed % INDETERMINATE_SWEEP_PERIOD_S) / INDETERMINATE_SWEEP_PERIOD_S;
            double segmentWidth = _progressBarWidth * INDETERMINATE_SEGMENT_FRACTION;
            ProgressFill.Opacity = 1.0;
            ProgressFill.Width = segmentWidth;
            _fillOffset.X = -segmentWidth + phase * (_progressBarWidth + segmentWidth);
        }
        else
        {
            double phase = (elapsed % INDETERMINATE_PULSE_PERIOD_S) / INDETERMINATE_PULSE_PERIOD_S;
            ProgressFill.Width = _progressBarWidth;
            _fillOffset.X = 0;
            ProgressFill.Opacity = 0.45 + 0.35 * (0.5 + 0.5 * Math.Cos(phase * 2 * Math.PI));
        }
    }

    private void UpdateProgressUI(float progress)
    {
        _currentProgress = progress;

        // Update progress bar width
        if (_progressBarWidth > 0)
        {
            ProgressFill.Width = _progressBarWidth * progress;
        }

        // Update percentage text
        int percentage = (int)Math.Round(progress * 100);
        PercentageText.Text = $"{percentage}%";
    }

    /// <summary>
    /// Easing function for smooth animation (ease-out cubic).
    /// </summary>
    private static double EaseOutCubic(double t)
    {
        return 1.0 - Math.Pow(1.0 - t, 3);
    }

    // ========================================================================
    // EVENT HANDLERS
    // ========================================================================

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        LoggingService.Debug("FileTranscriptionProgressWindow: Cancel button clicked");
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClosed(EventArgs e)
    {
        // CLEANUP: Stop timers when window closes
        _animationTimer.Stop();
        _indeterminateTimer.Stop();
        CancelRequested = null;
        base.OnClosed(e);
    }
}
