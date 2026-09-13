using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.ViewModels.Base;

namespace HyperWhisper.ViewModels.Onboarding;

public sealed partial class OnboardingFlowViewModel : ViewModelBase
{
    // =========================================================================
    // INTERNALS
    // =========================================================================

    private void RaiseGateChanged()
    {
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(IsSelectedSourceUsable));
        OnPropertyChanged(nameof(StagedSource));
        OnPropertyChanged(nameof(HasPendingProductionWrite));
        // KeyValidated is derived from the recorded scope AND the live one, so every
        // input that can move either - both key fields, the provider, the source -
        // has to re-raise it. All of them already come through here.
        OnPropertyChanged(nameof(KeyValidated));

        // Both derive from KeyValidated plus a per-session record, so they move on
        // strictly fewer occasions than KeyValidated - but never on more.
        OnPropertyChanged(nameof(CloudKeyIsVerified));
        OnPropertyChanged(nameof(ProviderKeyIsVerified));
    }

    /// <summary>
    /// Start an asynchronous action under a task-box key. The key is registered
    /// BEFORE the body runs: a C# async method can complete synchronously, and a body
    /// that cleared a key which had not been stored yet would leave the box
    /// permanently non-empty.
    /// </summary>
    private void RunTracked(string key, Func<CancellationToken, Task> body)
    {
        var source = new CancellationTokenSource();
        _taskBox.Store(key, source);
        var task = body(source.Token);
        LastAsyncTaskForTesting = task;
    }

    // ----- Test seams --------------------------------------------------------
    // Internal, so they reach HyperWhisper.SmokeTests through InternalsVisibleTo and
    // nothing else. They grant no capability: there is no way to bypass validation or
    // entitlement here.

    internal bool HasInFlightWorkForTesting => !_taskBox.IsEmpty;

    internal bool IsLiveForTesting => _isLive;

    /// <summary>
    /// The most recently spawned asynchronous action, so a test can await the exact
    /// task instead of yielding an arbitrary number of times.
    /// </summary>
    internal Task? LastAsyncTaskForTesting { get; private set; }
}
