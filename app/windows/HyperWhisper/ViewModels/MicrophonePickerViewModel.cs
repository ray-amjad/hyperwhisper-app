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
/// (<see cref="Detach"/>), a recording starting, or the device changing stops it.
/// </summary>
public partial class MicrophonePickerViewModel : ObservableObject
{
    public static readonly TimeSpan TestDuration = TimeSpan.FromSeconds(10);

    private readonly MainViewModel _main;
    private readonly MicrophoneLevelTester _tester;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _testTimer;
    private bool _attached;
    private bool _rebuilding;

    [ObservableProperty] private IReadOnlyList<MicrophoneOption> _options = Array.Empty<MicrophoneOption>();
    [ObservableProperty] private string? _fallbackMessage;
    [ObservableProperty] private bool _hasDevices;
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
        _testTimer.Tick += (_, _) => StopTest();
    }

    public bool HasFallback => !string.IsNullOrEmpty(FallbackMessage);

    public string TestButtonText => Loc.S(IsTesting ? "settings.sound.microphone.stop" : "settings.sound.microphone.test");

    public bool CanTest => HasDevices && !_main.IsRecording;

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
            if (value == null || _rebuilding || Equals(value, _selectedOption))
                return;

            _selectedOption = value;
            OnPropertyChanged();

            LoggingService.Info($"SoundSettingsPage: microphone set to '{value.Name ?? "(default)"}'");
            if (!_main.SelectMicrophone(value.Name))
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
        if (_attached) return;
        _attached = true;
        _main.PropertyChanged += OnMainPropertyChanged;
        _main.MicrophoneCaptureStarting += OnCaptureStarting;
        Rebuild();
    }

    /// <summary>Called when the page unloads: stops a running test.</summary>
    public void Detach()
    {
        StopTest();
        if (!_attached) return;
        _attached = false;
        _main.PropertyChanged -= OnMainPropertyChanged;
        _main.MicrophoneCaptureStarting -= OnCaptureStarting;
    }

    [RelayCommand]
    private void ToggleTest()
    {
        if (IsTesting)
        {
            StopTest();
            return;
        }

        if (!CanTest) return;

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
        _tester.Stop(resumeKeepWarm);
        IsTesting = false;
        Level = 0f;
    }

    private void OnCaptureStarting(object? sender, EventArgs e)
    {
        if (!IsTesting) return;
        LoggingService.Info("SoundSettingsPage: a recording is starting; stopping the microphone test");
        StopTest(resumeKeepWarm: false);
    }

    private void OnTesterLevel(object? sender, float level)
    {
        if (_dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (IsTesting)
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
        if (_dispatcher.CheckAccess())
            action();
        else if (!_dispatcher.HasShutdownStarted)
            _dispatcher.BeginInvoke(action);
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
        FallbackMessage = MicrophoneSelection.FallbackMessage(resolution);
    }
}
