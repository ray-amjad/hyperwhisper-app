using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HyperWhisper.Localization;
using HyperWhisper.Services;

namespace HyperWhisper.ViewModels;

/// <summary>
/// Settings -> Sound -> Microphone: the picker row, the amber "not connected"
/// line and the Test meter.
///
/// It owns NO selection state. The rows are rebuilt from
/// <see cref="MainViewModel.MicrophoneResolution"/> on every change, and a pick
/// goes through <see cref="MainViewModel.SelectMicrophone"/>, the same funnel the
/// tray uses, so the tray, onboarding and this picker always agree.
///
/// TEST. Visiting the page opens nothing. Test opens the device the app records
/// from (the saved choice, or the Windows default standing in for it) for
/// <see cref="TestDuration"/>; a second click, leaving the page
/// (<see cref="Dispose"/>), a recording starting, or the device changing stops it.
///
/// LIFETIME. The owning page creates one on Loaded and disposes it on Unloaded:
/// that unsubscribes from MainViewModel, the tester and the timer, and releases
/// the tester's capture.
/// </summary>
public sealed partial class MicrophonePickerViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan TestDuration = TimeSpan.FromSeconds(10);

    private readonly MainViewModel _main;
    private MicrophoneLevelTester? _tester;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _testTimer;
    private bool _attached;
    private bool _rebuilding;
    private bool _disposed;

    [ObservableProperty] private IReadOnlyList<MicrophoneOption> _options = Array.Empty<MicrophoneOption>();
    [ObservableProperty] private string? _fallbackMessage;
    [ObservableProperty] private bool _hasDevices;

    /// <summary>
    /// Whether the picker opens. True with devices, and also with none while a
    /// saved choice is "(not connected)": the row must stay readable rather
    /// than greyed, and "Default" stays pickable so the choice can be cleared.
    /// </summary>
    [ObservableProperty] private bool _canPick;
    [ObservableProperty] private float _level;
    [ObservableProperty] private bool _isTesting;

    private MicrophoneOption? _selectedOption;

    public MicrophonePickerViewModel(MainViewModel main)
    {
        _main = main;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _tester = new MicrophoneLevelTester(
            MicrophoneKeepWarmService.Instance,
            () => _main.SelectedAudioDevice?.DeviceNumber);
        _tester.LevelChanged += OnTesterLevel;

        _testTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TestDuration };
        _testTimer.Tick += OnTestTimerTick;
    }

    public bool HasFallback => !string.IsNullOrEmpty(FallbackMessage);

    public string TestButtonText => Loc.S(IsTesting ? "settings.sound.microphone.stop" : "settings.sound.microphone.test");

    public bool CanTest => !_disposed && HasDevices && !_main.IsRecording;

    /// <summary>
    /// The picked row. A TwoWay ComboBox binding writes null while its list is
    /// replaced and when its view is torn down; neither is a pick, so both are
    /// ignored.
    /// </summary>
    public MicrophoneOption? SelectedOption
    {
        get => _selectedOption;
        set
        {
            if (value == null || _rebuilding || _disposed || Equals(value, _selectedOption))
                return;

            _selectedOption = value;
            OnPropertyChanged();

            LoggingService.Info($"SoundSettingsPage: microphone set to '{value.Name ?? "(default)"}'");
            // The row's DeviceNumber keeps the exact device when several share
            // the name; only the name is saved.
            if (!_main.SelectMicrophone(value.Name, value.DeviceNumber))
            {
                // Refused (onboarding is open): put the row back.
                Rebuild();
            }
        }
    }

    partial void OnFallbackMessageChanged(string? value) => OnPropertyChanged(nameof(HasFallback));

    partial void OnHasDevicesChanged(bool value) => OnPropertyChanged(nameof(CanTest));

    partial void OnIsTestingChanged(bool value) => OnPropertyChanged(nameof(TestButtonText));

    /// <summary>Called when the page loads.</summary>
    public void Attach()
    {
        if (_disposed || _attached) return;
        _attached = true;
        _main.PropertyChanged += OnMainPropertyChanged;
        _main.MicrophoneCaptureStarting += OnCaptureStarting;
        Rebuild();
    }

    /// <summary>
    /// Called when the page unloads: stops a running test, then unsubscribes
    /// from everything before the tester is disposed, so no late callback
    /// reaches a disposed tester or a page that is gone.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            StopTest();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"MicrophonePickerViewModel: Stopping the test failed: {ex.Message}");
        }
        finally
        {
            if (_attached)
            {
                _attached = false;
                _main.PropertyChanged -= OnMainPropertyChanged;
                _main.MicrophoneCaptureStarting -= OnCaptureStarting;
            }

            _testTimer.Tick -= OnTestTimerTick;
            _testTimer.Stop();

            if (_tester != null)
                _tester.LevelChanged -= OnTesterLevel;
        }

        // Reverse order of creation: the timer is stopped above, then the tester.
        SafeDispose(ref _tester);
    }

    private static void SafeDispose<T>(ref T? resource) where T : class, IDisposable
    {
        var temp = resource;
        resource = null;

        try
        {
            temp?.Dispose();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"MicrophonePickerViewModel: Dispose failed for {typeof(T).Name}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ToggleTest()
    {
        if (IsTesting)
        {
            StopTest();
            return;
        }

        if (!CanTest || _tester == null) return;

        var deviceNumber = _main.SelectedAudioDevice?.DeviceNumber ?? -1;
        if (!_tester.Start(deviceNumber))
            return;

        Level = 0f;
        IsTesting = true;
        _testTimer.Stop();
        _testTimer.Start();
    }

    /// <summary>Stops a running test; idle, the bar is empty.</summary>
    public void StopTest(bool resumeKeepWarm = true)
    {
        _testTimer.Stop();
        _tester?.Stop(resumeKeepWarm);
        IsTesting = false;
        Level = 0f;
    }

    private void OnTestTimerTick(object? sender, EventArgs e) => StopTest();

    private void OnCaptureStarting(object? sender, EventArgs e)
    {
        if (!IsTesting) return;
        LoggingService.Info("SoundSettingsPage: a recording is starting; stopping the microphone test");
        StopTest(resumeKeepWarm: false);
    }

    private void OnTesterLevel(object? sender, float level)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed && IsTesting)
                Level = level;
        });
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SelectedAudioDevice):
                // The test follows the device the app records from; a different
                // one now means the old stream is the wrong device (or gone).
                if (IsTesting) StopTest();
                RunOnUi(Rebuild);
                break;
            case nameof(MainViewModel.AudioDevices):
            case nameof(MainViewModel.MicrophoneResolution):
                RunOnUi(Rebuild);
                break;
            case nameof(MainViewModel.IsRecording):
                if (_main.IsRecording && IsTesting) RunOnUi(() => StopTest(resumeKeepWarm: false));
                OnPropertyChanged(nameof(CanTest));
                break;
        }
    }

    private void RunOnUi(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess())
            action();
        else if (!_dispatcher.HasShutdownStarted)
            _dispatcher.BeginInvoke(() =>
            {
                if (!_disposed) action();
            });
    }

    private void Rebuild()
    {
        var devices = _main.AudioDevices;
        var resolution = _main.MicrophoneResolution
            ?? new MicrophoneResolution(_main.SelectedAudioDevice, MicrophoneSource.SystemDefault, null, _main.SelectedAudioDevice);

        var (options, selected) = MicrophoneSelection.BuildOptions(devices, resolution);

        _rebuilding = true;
        try
        {
            Options = options;
            _selectedOption = selected;
            OnPropertyChanged(nameof(SelectedOption));
        }
        finally
        {
            _rebuilding = false;
        }

        HasDevices = devices.Count > 0;
        CanPick = HasDevices || resolution.IsSavedMissing;
        FallbackMessage = MicrophoneSelection.FallbackMessage(resolution);
    }
}
