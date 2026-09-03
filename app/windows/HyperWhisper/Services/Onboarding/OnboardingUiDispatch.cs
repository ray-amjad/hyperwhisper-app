// ONBOARDING UI DISPATCH
//
// WHERE THE ONBOARDING SEAMS CROSS THREADS. One decision, applied at both of the
// places a background thread reaches the first-run flow.
//
// Two live adapters are driven by OS callbacks that do not arrive on the UI
// thread:
//
//   - LiveOnboardingModelCatalog.OnDownloadChanged, raised synchronously by
//     ModelDownloadService from inside Task.Run(() => RunDownloadAsync(...)).
//     A ~170 s Parakeet download fires ~100 of them.
//   - LiveOnboardingAudioGateway.OnHardwareDevicesChanged, raised from
//     AudioDeviceService's System.Timers.Timer debounce, sourced from the
//     MMDevice COM notification client.
//
// Both used to run straight through into OnboardingFlowViewModel, which writes
// bound view-model state (DeviceAvailability, IsLevelMeterActive, DeviceOptions,
// SelectedDeviceId) and reads an unsynchronised progress Dictionary. The
// gateway's own comment claimed "the flow marshals to the UI thread itself";
// nothing in the flow ever did.
//
// THE ADAPTERS MARSHAL, NOT THE FLOW. The flow view model is the unit-tested
// half and is exercised by HyperWhisper.SmokeTests with no WPF Application at
// all, so a Dispatcher dependency inside it would either be untestable or would
// have to be faked in an eighth seam. The adapters are already the layer that
// knows about singletons and the OS, so they are the right place to know about
// the UI thread too.
//
// This is app/windows/AGENTS.md's rule for MMDeviceEnumerator callbacks, and the
// same shape MainViewModel.OnAudioDevicesChanged already uses: get the
// dispatcher, drop the callback if it is null or shutting down, otherwise
// BeginInvoke.

namespace HyperWhisper.Services.Onboarding;

internal static class OnboardingUiDispatch
{
    /// <summary>
    /// Run <paramref name="work"/> on the UI thread.
    ///
    /// Three cases, in order:
    ///   - No WPF Application (the smoke harness, and any other host): run it
    ///     inline. There is no UI to race with.
    ///   - The dispatcher has begun shutting down: DROP it. Driving binding
    ///     callbacks through a dispatcher that is tearing down is the hazard the
    ///     check exists for, and there is no UI left to update anyway.
    ///   - Already on the UI thread: run it inline rather than queueing, so an
    ///     ordinary call ordering (select a device, then re-point the meter) is
    ///     not silently reordered behind a posted continuation.
    /// </summary>
    public static void Post(Action work)
    {
        var dispatcher = WpfApplication.Current?.Dispatcher;

        if (dispatcher is null)
        {
            RunGuarded(work);
            return;
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            LoggingService.Debug("OnboardingUiDispatch: dispatcher is shutting down, dropping the callback");
            return;
        }

        if (dispatcher.CheckAccess())
        {
            RunGuarded(work);
            return;
        }

        dispatcher.BeginInvoke(() => RunGuarded(work));
    }

    /// <summary>
    /// A throwing handler must not take the process down: on the posted path the
    /// exception would surface on the dispatcher rather than at the OS callback
    /// that caused it, where nothing can attribute it.
    /// </summary>
    private static void RunGuarded(Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"OnboardingUiDispatch: handler threw: {ex.Message}");
        }
    }
}
