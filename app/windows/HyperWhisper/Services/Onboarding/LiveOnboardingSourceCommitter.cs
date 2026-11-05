// The live IOnboardingSourceCommitter adapter for the first-run flow. Part of the onboarding
// composition described in OnboardingLiveDependencies.cs; split out of that file (#941).

using HyperWhisper.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;
using HyperWhisper.ViewModels.Onboarding;

namespace HyperWhisper.Services.Onboarding;

// =============================================================================
// Commit
// =============================================================================

/// <summary>
/// The one and only path from staged configuration to production state.
/// </summary>
public sealed class LiveOnboardingSourceCommitter : IOnboardingSourceCommitter
{
    private readonly ModeService _modes;
    private readonly SettingsService _settings;
    private readonly Action _returnToHome;

    public LiveOnboardingSourceCommitter(ModeService modes, SettingsService settings, Action returnToHome)
    {
        _modes = modes;
        _settings = settings;
        _returnToHome = returnToHome;
    }

    /// <summary>
    /// The row <see cref="Apply"/> will write and <see cref="Restore"/> will put
    /// back. The flagged default, or - when nothing is flagged - whatever
    /// already sits on the well-known seed id.
    ///
    /// The second half is the fix for a real hole. Apply's fallback builds a
    /// Mode with <c>ModeDefaults.DefaultModeId</c> and SaveMode writes EVERY
    /// column, so a row already at that id was silently overwritten - name,
    /// prompt, vocabulary and all - and a deferral then DELETED it, because the
    /// restore point recorded "no default existed". That is reachable without
    /// any exotic state: the Local API can clear IsDefault on that very row
    /// (PATCH /modes/{id}), and so can any future editor.
    ///
    /// Note this does NOT widen what onboarding touches. The seed id is the
    /// only row the fallback ever wrote to; all that changes is that the write
    /// is now reversible, and that the row's other columns survive it.
    /// </summary>
    private Mode? FindTargetMode() => SelectTargetMode(_modes.GetAllModes());

    /// <summary>
    /// The pure half of <see cref="FindTargetMode"/>, so the rule can be asserted
    /// without a database.
    /// </summary>
    internal static Mode? SelectTargetMode(IEnumerable<Mode> modes)
    {
        var all = modes as IReadOnlyCollection<Mode> ?? modes.ToList();
        return all.FirstOrDefault(m => m.IsDefault)
            ?? all.FirstOrDefault(m => m.Id == ModeDefaults.DefaultModeId);
    }

    public IOnboardingRestorePoint CaptureRestorePoint()
    {
        var existing = FindTargetMode();
        return new WindowsOnboardingRestorePoint
        {
            ModeExisted = existing is not null,
            ModeId = existing?.Id ?? ModeDefaults.DefaultModeId,
            Snapshot = existing is null ? null : WindowsOnboardingRestorePoint.Clone(existing),
            PreviousSelectedModeId = _settings.SelectedModeId
        };
    }

    /// <summary>
    /// Reconfigure the EXISTING default Mode in place. Only the source fields move;
    /// everything else on the row is left exactly as it was, which is what makes
    /// this reversible from the snapshot above.
    ///
    /// CloudTranscriptionModel is deliberately cleared rather than set, so it
    /// re-derives for the new provider and tier.
    /// </summary>
    public void Apply(OnboardingStagedSource staged)
    {
        // The SAME row CaptureRestorePoint snapshotted, or a new one when there
        // genuinely is nothing at the seed id. Reconfiguring an existing
        // fallback-id row in place keeps its name, prompt and vocabulary, and
        // keeps the snapshot an exact undo of what changed.
        var existing = FindTargetMode();
        var mode = existing is null ? NewDefaultMode() : WindowsOnboardingRestorePoint.Clone(existing);

        ApplyStagedFields(mode, staged);

        // SaveMode creates when the row is absent and updates when it is not, and
        // it writes every column either way.
        _modes.SaveMode(mode);

        // Writing the source onto Default is not enough on its own: a returning
        // user's SelectedModeId still points at their own Mode, so the next
        // recording would keep using that Mode's source.
        _modes.SetSelectedMode(mode.Id);
    }

    /// <summary>
    /// The staged source -> Mode mapping.
    ///
    /// The plan's table is a macOS shape and is incomplete for Windows: a local
    /// Mode here carries LocalEngine ("whisper"/"parakeet") and, for Parakeet,
    /// LocalParakeetModel. Writing only Model would leave a Parakeet pick running
    /// on Whisper. The engine is derived from the staged model id, which is why
    /// OnboardingStagedSource does not need a new field.
    /// </summary>
    internal static void ApplyStagedFields(Mode mode, OnboardingStagedSource staged)
    {
        mode.PostProcessingMode = staged.PostProcessingMode;
        mode.CloudProvider = staged.CloudProvider;
        mode.CloudTranscriptionModel = null;
        mode.CloudAccuracyTier = staged.CloudAccuracyTier ?? mode.CloudAccuracyTier;

        if (staged.Source == OnboardingSourceKind.OnDevice)
        {
            mode.ProviderType = "local";

            var isParakeet = ParakeetModelInfo.AllModels.Any(m => m.Id == staged.Model);
            mode.LocalEngine = isParakeet ? "parakeet" : "whisper";

            if (isParakeet)
            {
                mode.LocalParakeetModel = staged.Model;
            }
            else
            {
                mode.Model = staged.Model;
                mode.ModelType = staged.Model;
            }

            // A Parakeet pick left Model as it was, which is "cloud" once a cloud
            // source has been committed (the else arm below). Model is the portable
            // routing field on macOS, so keep it local too (#1477).
            LocalModeModel.ApplyOnDevice(mode);
        }
        else
        {
            // staged.Model is always "cloud" for a cloud source (the flow's
            // StagedSource); ApplyCloud writes the same sentinel by name.
            LocalModeModel.ApplyCloud(mode);
            mode.ModelType = staged.Model;
        }
    }

    private static Mode NewDefaultMode() => new()
    {
        Id = ModeDefaults.DefaultModeId,
        Name = "Hyper",
        Preset = "hyper",
        IsDefault = true,
        IsSystemProvided = true,
        SortOrder = 0,
        Language = "auto"
    };

    /// <returns>
    /// True when production state is back. False when the database refused -
    /// the flow KEEPS the restore point in that case, exactly as the credential
    /// rollback keeps a provider whose key could not be written back, so the
    /// value is still there for a second attempt and the user is told rather
    /// than shown a clean deferral over a Mode that is still the staged one.
    /// </returns>
    public bool Restore(IOnboardingRestorePoint point)
    {
        if (point is not WindowsOnboardingRestorePoint restore) return false;

        try
        {
            if (restore.ModeExisted && restore.Snapshot is not null)
            {
                _modes.SaveMode(WindowsOnboardingRestorePoint.Clone(restore.Snapshot));
            }
            else
            {
                // Nothing was flagged default before, so remove what Apply created
                // rather than leaving a synthetic default behind.
                //
                // JUDGED ON THE OUTCOME, not on the return value. DeleteMode
                // answers false for three unrelated reasons and only one of them
                // is a failed rollback:
                //   * the row is already gone      - the goal is met, this is a pass
                //   * it is the last remaining Mode - a deliberate refusal, but the
                //     onboarding Mode IS still production state afterwards
                //   * DbUpdateException            - a genuine failure
                // So the question asked is "is the row still there?", which
                // separates the first from the other two without teaching this
                // method ModeService's reason codes. Dropping the answer
                // altogether reported a clean deferral over a Mode the flow had
                // created and could not remove, with the restore point discarded.
                if (!DeleteLeftNothingBehind(
                        _modes.DeleteMode(restore.ModeId),
                        () => _modes.GetMode(restore.ModeId) is not null))
                {
                    LoggingService.Error(
                        "LiveOnboardingSourceCommitter: restore could not delete the Mode onboarding "
                        + $"created ({restore.ModeId}); it is still the default");
                    return false;
                }
            }

            // DeleteMode may have re-pointed the selection; put the user's back
            // afterwards, including a null that means "never chose one".
            //
            // Through SetSelectedMode, NOT a raw settings write. Apply() changes the
            // selection with SetSelectedMode, which raises ModeSelected, and
            // MainViewModel caches the result in SelectedMode/CurrentMode. A restore
            // that only wrote SettingsService.SelectedModeId raised nothing, so the
            // running app kept dictating with the onboarding-staged Mode for the rest
            // of the session and could re-persist it. The restore path has to be as
            // loud as the apply path.
            if (restore.PreviousSelectedModeId is { } previousId)
            {
                _modes.SetSelectedMode(previousId);

                // SetSelectedMode is a no-op when the row is gone (the user's Mode
                // was itself the one Apply created and Restore has just deleted).
                // Fall back to the raw write so the setting is never left pointing
                // at the staged selection.
                if (_settings.SelectedModeId != previousId)
                    _settings.SelectedModeId = previousId;
            }
            else
            {
                // Nothing was selected before. There is no "deselect" event to
                // raise, so the raw write is the only option; ModeChanged from the
                // save or delete above has already told the shell to re-read.
                _settings.SelectedModeId = null;
            }

            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Error($"LiveOnboardingSourceCommitter: restore failed: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Did the delete leave the Mode gone? The rollback's question, and NOT
    /// "did DeleteMode return true".
    ///
    /// ModeService.DeleteMode answers false for three unrelated reasons: the row
    /// was already gone (goal met), it is the last remaining Mode (a deliberate
    /// refusal, but the onboarding Mode is still production state), and
    /// DbUpdateException (a real failure). Only the first is a pass, and the row's
    /// presence is what separates it - so this method needs none of ModeService's
    /// reason codes.
    /// </summary>
    /// <param name="stillPresent">
    /// Deferred: it is a database read, and it is only worth doing on the false
    /// path.
    /// </param>
    internal static bool DeleteLeftNothingBehind(bool deleted, Func<bool> stillPresent) =>
        deleted || !stillPresent();

    public void MarkOnboardingCompleted() => _settings.OnboardingPending = false;

    public void ReturnToHome()
    {
        try
        {
            _returnToHome();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LiveOnboardingSourceCommitter: could not return to Home: {ex.Message}");
        }
    }
}
