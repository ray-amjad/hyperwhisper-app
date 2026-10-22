using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

/// <summary>
/// Service for enumerating and managing audio input devices (microphones).
/// Uses NAudio's WaveIn API to discover available recording devices.
///
/// HOT-PLUG DETECTION:
/// This service monitors audio device changes (plug/unplug) using the
/// Windows Core Audio API (MMDeviceEnumerator). When devices change,
/// the DevicesChanged event is raised so the UI can refresh the device list.
///
/// USAGE:
/// 1. Subscribe to DevicesChanged event
/// 2. Call GetAvailableDevices() to get current list
/// 3. When DevicesChanged fires, call GetAvailableDevices() again
/// 4. Call Dispose() when done to unregister the notification client
/// </summary>
public class AudioDeviceService : IDisposable
{
    /// <summary>
    /// Represents an audio input device.
    /// </summary>
    public record AudioDevice(int DeviceNumber, string Name);

    /// <summary>
    /// Raised when audio devices are added, removed, or changed.
    /// UI should refresh the device list when this event fires.
    /// </summary>
    public event EventHandler? DevicesChanged;

    private MMDeviceEnumerator? _deviceEnumerator;
    private DeviceNotificationClient? _notificationClient;
    private System.Timers.Timer? _debounceTimer;
    private bool _disposed;

    public AudioDeviceService()
    {
        // DEVICE CHANGE MONITORING SETUP:
        // MMDeviceEnumerator provides access to the Windows Core Audio API.
        // We register a notification client to receive callbacks when:
        // - A device is added (plugged in)
        // - A device is removed (unplugged)
        // - Device state changes (enabled/disabled)
        // - Default device changes
        try
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            _notificationClient = new DeviceNotificationClient(this);
            _deviceEnumerator.RegisterEndpointNotificationCallback(_notificationClient);
            LoggingService.Debug("AudioDeviceService: Device change monitoring enabled");
        }
        catch (Exception ex)
        {
            // If Core Audio API is unavailable, fall back to no monitoring
            LoggingService.Warn($"AudioDeviceService: Failed to enable device monitoring: {ex.Message}");
            _deviceEnumerator = null;
            _notificationClient = null;
        }
    }

    /// <summary>
    /// Gets all available audio input devices.
    ///
    /// RACE CONDITION PROTECTION:
    /// Device count can change between checking WaveIn.DeviceCount and
    /// calling GetCapabilities() if a device is unplugged mid-enumeration.
    /// We wrap the enumeration in try-catch to handle this gracefully.
    ///
    /// RESULT PATTERN:
    /// Returns Result&lt;List&lt;AudioDevice&gt;&gt; to explicitly communicate:
    /// - Success with empty list: No devices found (normal state)
    /// - Success with devices: Devices successfully enumerated
    /// - Failure: Enumeration failed (system error, API unavailable)
    /// This allows callers to distinguish between "no devices" and "enumeration failed"
    /// </summary>
    public Result<List<AudioDevice>> GetAvailableDevices()
    {
        var devices = new List<AudioDevice>();

        try
        {
            int deviceCount = WaveIn.DeviceCount;
            for (int i = 0; i < deviceCount; i++)
            {
                try
                {
                    var caps = WaveIn.GetCapabilities(i);
                    devices.Add(new AudioDevice(i, caps.ProductName));
                }
                catch (Exception ex)
                {
                    // Device may have been removed during enumeration - skip it
                    // This is not a failure - just skip the device and continue
                    LoggingService.Debug($"AudioDeviceService: Skipping device {i} during enumeration: {ex.Message}");
                }
            }

            // Successfully enumerated (even if list is empty)
            return Result<List<AudioDevice>>.Success(devices);
        }
        catch (Exception ex)
        {
            // Critical failure accessing WaveIn.DeviceCount or enumeration API
            LoggingService.Error($"AudioDeviceService: Failed to enumerate audio devices: {ex.Message}", ex);
            return Result<List<AudioDevice>>.Failure(Loc.S("audio.error.enumerationFailed"), ex);
        }
    }

    /// <summary>
    /// The Windows default capture endpoint: its Core Audio FriendlyName and its
    /// position in the active capture endpoint list. Null when Windows has no
    /// default input or Core Audio cannot be read.
    ///
    /// ROLE: <see cref="Role.Console"/>, the "Default Device" in the Sound
    /// control panel and the one the Windows 11 quick-settings picker sets.
    /// Dictation is not a call, so the "Default Communication Device" (a headset
    /// a user keeps for Teams) is the wrong answer, and WinMM's WAVE_MAPPER
    /// (WaveIn device -1, which onboarding's System Default row opens) follows
    /// the console default too, so both paths open the same microphone.
    ///
    /// Map the result to a WaveIn row with <see cref="MicrophoneSelection.ResolveDefault"/>.
    /// </summary>
    public (string? FriendlyName, int Ordinal)? GetDefaultCaptureEndpoint()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
                return null;

            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            var defaultId = defaultDevice.ID;

            string? name = null;
            try
            {
                name = CaptureDeviceNameCache.Shared.GetOrRead(defaultId, () => defaultDevice.FriendlyName);
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"AudioDeviceService: Could not read the default capture endpoint's name: {ex.Message}");
            }

            var ordinal = -1;
            try
            {
                var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                var count = endpoints.Count;
                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        using var endpoint = endpoints[i];
                        if (string.Equals(endpoint.ID, defaultId, StringComparison.Ordinal))
                        {
                            ordinal = i;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Debug($"AudioDeviceService: Skipping capture endpoint {i}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"AudioDeviceService: Could not enumerate capture endpoints: {ex.Message}");
            }

            return (name, ordinal);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"AudioDeviceService: Could not read the default capture endpoint: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Raises the DevicesChanged event after a debounce delay.
    /// Called by DeviceNotificationClient when device changes are detected.
    ///
    /// DEBOUNCING:
    /// When multiple devices change simultaneously (e.g., USB hub with multiple
    /// devices), we receive multiple callbacks in quick succession. Debouncing
    /// ensures we only refresh the device list once after all changes settle.
    /// </summary>
    internal void OnDevicesChanged()
    {
        // The endpoint set changed: drop every cached capture name now, before
        // the debounced refresh re-reads them (#980).
        CaptureDeviceNameCache.Shared.Clear();

        // Cancel any pending debounce timer
        _debounceTimer?.Stop();
        _debounceTimer?.Dispose();

        // Start a new debounce timer (250ms delay)
        _debounceTimer = new System.Timers.Timer(250);
        _debounceTimer.AutoReset = false;
        _debounceTimer.Elapsed += (s, e) =>
        {
            LoggingService.Debug("AudioDeviceService: Device change detected, raising DevicesChanged event");
            DevicesChanged?.Invoke(this, EventArgs.Empty);
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        };
        _debounceTimer.Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop and dispose the debounce timer
        _debounceTimer?.Stop();
        _debounceTimer?.Dispose();
        _debounceTimer = null;

        // Unregister the notification callback and dispose resources
        // Always clear references even if unregister fails to prevent memory leaks
        if (_deviceEnumerator != null && _notificationClient != null)
        {
            try
            {
                _deviceEnumerator.UnregisterEndpointNotificationCallback(_notificationClient);
                LoggingService.Debug("AudioDeviceService: Device change monitoring disabled");
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"AudioDeviceService: Error unregistering notification callback: {ex.Message}");
            }
            finally
            {
                // Always clear references to prevent COM callback leaks
                _notificationClient = null;
                _deviceEnumerator = null;
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Internal notification client that receives callbacks from Windows Core Audio API.
    /// Implements IMMNotificationClient to handle device change events.
    ///
    /// CALLBACK METHODS:
    /// - OnDeviceAdded: New device plugged in
    /// - OnDeviceRemoved: Device unplugged
    /// - OnDeviceStateChanged: Device enabled/disabled
    /// - OnDefaultDeviceChanged: Default input/output changed
    /// - OnPropertyValueChanged: Device property changed (ignored)
    /// </summary>
    private class DeviceNotificationClient : IMMNotificationClient
    {
        private readonly AudioDeviceService _service;

        public DeviceNotificationClient(AudioDeviceService service)
        {
            _service = service;
        }

        /// <summary>
        /// Called when a new audio endpoint device is added.
        /// </summary>
        public void OnDeviceAdded(string deviceId)
        {
            LoggingService.Debug($"AudioDeviceService: Device added: {deviceId}");
            _service.OnDevicesChanged();
        }

        /// <summary>
        /// Called when an audio endpoint device is removed.
        /// </summary>
        public void OnDeviceRemoved(string deviceId)
        {
            LoggingService.Debug($"AudioDeviceService: Device removed: {deviceId}");
            _service.OnDevicesChanged();
        }

        /// <summary>
        /// Called when the state of an audio endpoint device changes.
        /// States: Active, Disabled, NotPresent, Unplugged
        /// </summary>
        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            LoggingService.Debug($"AudioDeviceService: Device state changed: {deviceId} -> {newState}");
            _service.OnDevicesChanged();
        }

        /// <summary>
        /// Called when the default audio endpoint device changes.
        /// We only care about capture (input) devices, not render (output).
        /// </summary>
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // Only refresh for capture (microphone) devices
            if (flow == DataFlow.Capture)
            {
                LoggingService.Debug($"AudioDeviceService: Default capture device changed: {defaultDeviceId}");
                _service.OnDevicesChanged();
            }
        }

        /// <summary>
        /// Called when a property of an audio endpoint device changes.
        /// The device list is unaffected, but a rename in the Sound control panel
        /// arrives here, so the endpoint's cached name is dropped (#980).
        /// </summary>
        public void OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
            // Don't refresh the device list - property changes don't affect
            // device availability. Any key: a rename can surface as more than
            // one property, and a dropped entry only costs one re-read.
            CaptureDeviceNameCache.Shared.Invalidate(deviceId);
        }
    }
}
