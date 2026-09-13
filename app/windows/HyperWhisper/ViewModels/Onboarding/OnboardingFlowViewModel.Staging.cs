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
    // STAGING AND COMMIT
    // =========================================================================

    /// <summary>
    /// The staged configuration, or null while the user has not chosen a source.
    /// Computed on demand: producing it touches nothing.
    /// </summary>
    public OnboardingStagedSource? StagedSource
    {
        get
        {
            if (SelectedSource is not { } source)
                return null;

            return source switch
            {
                // Fully offline: local model, post-processing off.
                OnboardingSourceKind.OnDevice => new OnboardingStagedSource(
                    OnboardingSourceKind.OnDevice,
                    SelectedModel?.Id ?? "base",
                    null,
                    0,
                    null),

                OnboardingSourceKind.HyperWhisperCloud => new OnboardingStagedSource(
                    OnboardingSourceKind.HyperWhisperCloud,
                    "cloud",
                    "hyperwhisper",
                    1,
                    CloudAccuracyTier.ElevenLabsScribeV2.ToStorageValue()),

                // Post-processing off by default so first run never fails on a
                // missing post-processing key.
                OnboardingSourceKind.YourProvider => new OnboardingStagedSource(
                    OnboardingSourceKind.YourProvider,
                    "cloud",
                    SelectedProvider.GetIdentifier(),
                    0,
                    null),

                _ => null
            };
        }
    }

    /// <summary>
    /// True once production state has been written and not yet restored. Covers all
    /// three reversible writes: the default Mode, the credential store, and the
    /// selected input device.
    /// </summary>
    public bool HasPendingProductionWrite =>
        _restorePoint is not null || _providerKeyRestorePoints.Count > 0 || _didCaptureDevice;

    /// <summary>
    /// Providers whose pre-onboarding API key the last rollback could not put back.
    /// Empty unless the credential store refused the write twice. The window
    /// surfaces this rather than closing silently over a lost key.
    /// </summary>
    public IReadOnlyList<CloudTranscriptionProvider> UnrestoredProviderKeys => _unrestoredProviderKeys;

    /// <summary>
    /// True when the last rollback could not put the pre-onboarding default Mode
    /// back. The restore point is retained in that case, so
    /// <see cref="HasPendingProductionWrite"/> stays true and the window reports
    /// it beside any lost credential.
    /// </summary>
    public bool ModeRestoreFailed => _modeRestoreFailed;

    /// <summary>
    /// True when the last attempt to write the staged source into production state
    /// failed. The mirror of <see cref="ModeRestoreFailed"/>, on the apply side:
    /// the restore point is kept, the step does not change, the flow does not
    /// close, and the window reports it through the same path.
    /// </summary>
    public bool SourceApplyFailed => _sourceApplyFailed;

    private bool _sourceApplyFailed;

    /// <summary>
    /// Write the staged source into production state, reversibly.
    /// </summary>
    /// <returns>
    /// True when the write went in, and on a flow with nothing staged (there is
    /// then nothing that can fail). False when the Modes database refused.
    ///
    /// The Restore mirror at LiveOnboardingSourceCommitter.Restore has been fully
    /// wrapped since it was written; this was not, and ModeService.SaveMode
    /// RETHROWS DbUpdateException. The path from the footer button is
    /// PrimaryButton_Click -> Advance/Complete -> ApplyStagedSourceReversibly with
    /// no try/catch anywhere on it, so a locked SQLite file (the Local API's own
    /// DbContext, an antivirus handle, a full disk) surfaced as App.xaml.cs's raw
    /// unhandled-exception box on top of the first-run window - and left the flow
    /// on an unarmed Try It page, or with the window never closing at all.
    /// </returns>
    private bool ApplyStagedSourceReversibly()
    {
        _sourceApplyFailed = false;

        if (StagedSource is not { } staged)
            return true;

        // Captured BEFORE the write, and kept if the write fails: a throw can
        // still leave a half-applied Mode behind, so the snapshot is the only way
        // back and HasPendingProductionWrite must stay true over it.
        if (_restorePoint is null)
        {
            _restorePoint = _committer.CaptureRestorePoint();
            RaiseGateChanged();
        }

        try
        {
            _committer.Apply(staged);
            return true;
        }
        catch (Exception ex)
        {
            _sourceApplyFailed = true;
            HyperWhisper.Services.LoggingService.Error(
                "OnboardingFlowViewModel: could not write the staged source into production state; "
                + $"the restore point is kept so the change is still reversible: {ex.Message}",
                ex);
            RaiseGateChanged();
            return false;
        }
    }

    /// <summary>
    /// Explicit completion. The staged configuration becomes production state and
    /// there is nothing left to roll back.
    /// </summary>
    /// <returns>
    /// True when the flow closed. False when the final write refused, in which case
    /// NOTHING has happened: first run is not marked complete, the restore point is
    /// kept, and the window stays open so the user can retry or defer.
    /// <see cref="SourceApplyFailed"/> says why.
    ///
    /// No longer a [RelayCommand]: the generator only accepts void and Task, and
    /// nothing bound to CompleteCommand - the footer button is a Click handler in
    /// OnboardingWindow, which is the caller that needs the answer.
    /// </returns>
    public bool Complete()
    {
        if (!_isLive)
            return false;

        // Discarding the restore points below is what makes this irreversible, so
        // it may only happen over a write that actually landed.
        if (!ApplyStagedSourceReversibly())
            return false;

        _restorePoint = null;
        _providerKeyRestorePoints.Clear();
        _didCaptureDevice = false;
        _previousDeviceId = null;
        _previousOpenDeviceId = null;
        Finish(markCompleted: true);
        return true;
    }

    /// <summary>
    /// Set Up Later. Bug 1: every reversible write this flow made is put back, so the
    /// default Mode, the active mode selection, the provider API keys, and the
    /// selected input device are exactly what they were before the window opened.
    /// Downloaded models are deliberately kept (harmless, and the user paid the
    /// bytes), as is an activated HyperWhisper Cloud licence: activation is a
    /// server-side account action, not local state this flow can un-write.
    ///
    /// This is an EXPLICIT decision by the user, so it closes first run for good,
    /// exactly as macOS's <c>deferSetup()</c> does (it reaches the same
    /// <c>markOnboardingCompleted()</c> as <c>complete()</c>). A close that is NOT
    /// a decision goes to <see cref="AbandonSetup"/> instead.
    /// </summary>
    /// <remarks>
    /// Read <see cref="UnrestoredProviderKeys"/> afterwards. A reversible write
    /// that could not be put back has to be REPORTED: silently closing over a lost
    /// credential is what that list exists to prevent. The method stays void
    /// because [RelayCommand] only generates for void and Task.
    /// </remarks>
    [RelayCommand]
    public void DeferSetup()
    {
        if (!_isLive)
            return;

        Rollback();
        Finish(markCompleted: true);
    }

    /// <summary>
    /// The window went away without the user deciding anything: Alt+F4, the
    /// taskbar, tray Quit, or the OS ending the session for an update.
    ///
    /// It rolls back exactly like <see cref="DeferSetup"/> but does NOT mark first
    /// run complete, so <c>SettingsService.OnboardingPending</c> survives and the
    /// interrupted run is re-offered on the next launch. That is macOS's behaviour
    /// too: its sheet is <c>.interactiveDismissDisabled()</c> and has no close
    /// button, so a process that dies mid-flow never reaches
    /// <c>markOnboardingCompleted()</c> and both of its flags stay put. Windows has
    /// an OS-supplied caption X and a real shutdown path, which macOS does not, so
    /// the distinction has to be made in code rather than by the frame.
    /// </summary>
    public void AbandonSetup()
    {
        if (!_isLive)
            return;

        Rollback();
        Finish(markCompleted: false);
    }

    /// <summary>
    /// The footer primary: Continue everywhere, "Done Onboarding" on the last step.
    /// </summary>
    [RelayCommand]
    public void Continue()
    {
        if (Step == OnboardingSteps.Last)
        {
            Complete();
            return;
        }

        Advance();
    }

    [RelayCommand]
    public void GoBack() => Back();

    /// <summary>
    /// Put every reversible write back.
    /// </summary>
    /// <returns>
    /// True when everything went back. The credential store is the one sink here
    /// that can REFUSE a write (Windows Credential Manager returns a Win32 error;
    /// <c>Persist</c> reports it as false), and a rollback that drops that answer
    /// turns "your original key is gone" into a clean deferral.
    /// </returns>
    private bool Rollback()
    {
        // The Mode path now matches the credential path below: the restore point
        // is discarded only when the write actually went back.
        //
        // Restore() swallows database failures; discarding the snapshot
        // regardless turned "your default Mode is still the one onboarding
        // staged" into a clean deferral, with the pre-onboarding row gone from
        // memory and no way to retry. A transient EF failure while deferring
        // after the Try It step is enough.
        _modeRestoreFailed = false;
        if (_restorePoint is { } point)
        {
            if (_committer.Restore(point))
            {
                _restorePoint = null;
            }
            else
            {
                _modeRestoreFailed = true;
                HyperWhisper.Services.LoggingService.Error(
                    "OnboardingFlowViewModel: could not restore the pre-onboarding default Mode; "
                    + "the restore point is kept so the change is still reversible");
            }
        }

        // "Test API key" writes to the credential store before any commit boundary,
        // so deferral has to put the previous value back. "" is how the store encodes
        // "no key", so a provider that had nothing ends up with nothing.
        _unrestoredProviderKeys.Clear();
        var restoredProviders = new List<CloudTranscriptionProvider>();

        foreach (var entry in _providerKeyRestorePoints)
        {
            // One retry: the common failure is a transient Credential Manager lock,
            // and the alternative to a second attempt is losing the key outright.
            var persisted = _providerKeys.Persist(entry.Value, entry.Key)
                || _providerKeys.Persist(entry.Value, entry.Key);

            if (persisted)
            {
                restoredProviders.Add(entry.Key);
                continue;
            }

            _unrestoredProviderKeys.Add(entry.Key);
            // Fully qualified: this file is presentation and deliberately imports no
            // Services namespace, so the one place it needs the logger names it.
            HyperWhisper.Services.LoggingService.Error(
                "OnboardingFlowViewModel: could not restore the pre-onboarding API key for "
                + $"{entry.Key.GetIdentifier()}: {_providerKeys.ValidationError ?? "no reason reported"}");
        }

        // Only what actually went back is forgotten. A provider whose key could not
        // be restored keeps its restore point, so HasPendingProductionWrite stays
        // honest and a second attempt still has the value to write.
        foreach (var provider in restoredProviders)
        {
            _providerKeyRestorePoints.Remove(provider);
        }

        if (_didCaptureDevice)
        {
            _audio.RestoreDevice(_previousDeviceId, _previousOpenDeviceId);
            _didCaptureDevice = false;
            _previousDeviceId = null;
            _previousOpenDeviceId = null;
        }

        RaiseGateChanged();
        return _unrestoredProviderKeys.Count == 0 && !_modeRestoreFailed;
    }

    /// <param name="markCompleted">
    /// True only when the user made an explicit decision (Done Onboarding, or Set
    /// Up Later). False when the window merely went away, which must leave
    /// OnboardingPending set so first run is re-offered.
    /// </param>
    private void Finish(bool markCompleted)
    {
        // Close the commit boundary FIRST so any in-flight continuation that is
        // already past its cancellation check still cannot write onboarding state.
        _isLive = false;
        _taskBox.CancelAll();
        _audio.StopRecordingForExit();
        _audio.StopInputLevelPreview();
        IsLevelMeterActive = false;

        if (markCompleted)
            _committer.MarkOnboardingCompleted();

        _committer.ReturnToHome();
    }

}
