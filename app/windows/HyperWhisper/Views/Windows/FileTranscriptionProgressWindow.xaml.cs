using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using HyperWhisper.Services;

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

    /// <summary>Event fired when cancel button is clicked.</summary>
    public event EventHandler? CancelRequested;

    /// <summary>Current progress value (0.0 to 1.0) for external access.</summary>
    public float CurrentProgress => _currentProgress;

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

        // Position window at center of screen
        PositionWindow();

        // Make window non-activating after it's loaded
        Loaded += (s, e) => MakeNonActivating();

        // Update progress bar width after layout
        ProgressBarTrack.SizeChanged += (s, e) =>
        {
            _progressBarWidth = ProgressBarTrack.ActualWidth;
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
        _currentProgress = 0f;
        _targetProgress = 0f;
        UpdateProgressUI(0f);

        // Store cancel handler
        CancelRequested = null;
        if (onCancel != null)
        {
            CancelRequested += (s, e) => onCancel();
        }

        Show();
        LoggingService.Debug($"FileTranscriptionProgressWindow: Shown - {fileName}");
    }

    /// <summary>
    /// Animates progress to target value over specified duration.
    /// </summary>
    public void AnimateProgress(float targetProgress, double duration)
    {
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
        }
    }

    /// <summary>
    /// Instantly sets progress to target value (no animation).
    /// </summary>
    public void SetProgress(float progress)
    {
        _animationTimer.Stop();
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
        CancelRequested = null;
        Hide();
        LoggingService.Debug("FileTranscriptionProgressWindow: Dismissed");
    }

    // ========================================================================
    // PRIVATE METHODS
    // ========================================================================

    private void PositionWindow()
    {
        var screen = SystemParameters.WorkArea;
        Left = (screen.Width - Width) / 2;
        Top = (screen.Height - Height) / 2;
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

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        double elapsed = (DateTime.Now - _animationStartTime).TotalSeconds;
        double normalizedTime = Math.Min(elapsed / _animationDuration, 1.0);
        double easedTime = EaseOutCubic(normalizedTime);

        float newProgress = _currentProgress + (float)((_targetProgress - _currentProgress) * easedTime);
        UpdateProgressUI(newProgress);

        if (normalizedTime >= 1.0)
        {
            _animationTimer.Stop();
            _currentProgress = _targetProgress;
            UpdateProgressUI(_currentProgress);
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
        // CLEANUP: Stop timer when window closes
        _animationTimer.Stop();
        CancelRequested = null;
        base.OnClosed(e);
    }
}
