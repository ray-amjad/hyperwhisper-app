// ONBOARDING TASK OWNERSHIP
//
// Mirror of Swift's `OnboardingTaskBox`
// (app/macos/hyperwhisper/Views/Onboarding/OnboardingFlowModel.swift:224-262).
//
// Bug 3 on macOS was a cloud activation running in an untracked Task that could
// land after the sheet closed and write flow state. Every asynchronous action the
// flow starts is registered here under a key, so a second press of the same button
// cancels and replaces the first, and teardown cancels the lot.
//
// The keys mirror Swift's `TaskKey` plus the two Windows-only actions.

namespace HyperWhisper.ViewModels.Onboarding;

/// <summary>
/// Holds the flow's in-flight cancellation sources so they can be cancelled from
/// teardown. Keyed so a second press of the same button replaces the first.
/// Thread-safe: teardown can arrive on a different thread from the action.
/// </summary>
internal sealed class OnboardingTaskBox
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _sources = new();

    /// <summary>
    /// Register a new action under <paramref name="key"/>, cancelling whatever was
    /// there before. The previous source is cancelled OUTSIDE the lock, because a
    /// cancellation callback can run inline and must not re-enter the box.
    /// </summary>
    public void Store(string key, CancellationTokenSource source)
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            _sources.TryGetValue(key, out previous);
            _sources[key] = source;
        }

        previous?.Cancel();
    }

    /// <summary>Cancel and forget the action under <paramref name="key"/>.</summary>
    public void Cancel(string key)
    {
        CancellationTokenSource? source;
        lock (_gate)
        {
            _sources.Remove(key, out source);
        }

        source?.Cancel();
    }

    /// <summary>Forget the action under <paramref name="key"/> without cancelling it.</summary>
    public void Clear(string key)
    {
        lock (_gate)
        {
            _sources.Remove(key);
        }
    }

    /// <summary>Cancel and forget everything. The teardown path.</summary>
    public void CancelAll()
    {
        CancellationTokenSource[] all;
        lock (_gate)
        {
            all = _sources.Values.ToArray();
            _sources.Clear();
        }

        foreach (var source in all)
        {
            source.Cancel();
        }
    }

    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _sources.Count == 0;
            }
        }
    }

    // The sources are deliberately never disposed. Every continuation reads
    // `ct.IsCancellationRequested` AFTER its await, which can be after CancelAll(),
    // and disposing would race that read. Nothing here asks for a WaitHandle, so a
    // CancellationTokenSource holds no unmanaged handle to leak.
}

/// <summary>
/// Task-box keys. The first four mirror Swift's <c>TaskKey</c>; the rest are the
/// Windows-only asynchronous actions (the credits fetch, the sample clip, and the
/// Try It microphone transcription).
/// </summary>
internal static class OnboardingTaskKeys
{
    public const string LicenseTest = "license.test";
    public const string ProviderTest = "provider.test";
    public const string Activation = "license.activate";
    public const string MicrophonePermission = "permission.microphone";
    public const string CreditsRefresh = "credits.refresh";
    public const string SampleClip = "tryIt.sample";

    /// <summary>
    /// The Try It microphone path. macOS has no equivalent because its audio
    /// manager owns the recording; on Windows the flow owns it, and an untracked
    /// one is what let a transcription outlive the window that started it.
    /// </summary>
    public const string TestRecording = "tryIt.record";
}
