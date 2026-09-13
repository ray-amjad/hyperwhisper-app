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
    // CREDITS (Windows-only seam; macOS reads the singleton from its views)
    // =========================================================================

    /// <summary>What every credits readout shows while the figure is unknown.</summary>
    private const string CreditsUnknown = "…";

    private string _creditsFormatted = CreditsUnknown;

    /// <summary>
    /// The balance SENTENCE - "$66.95 remaining (~10627 minutes)" - or an ellipsis
    /// while it is unknown. Display only: it never gates Continue, and a failed fetch
    /// is not a setup error.
    ///
    /// It is a sentence, so it belongs in a tooltip and not in a 30 pt readout under a
    /// caption that names a count; both cloud steps show it that way and macOS shows it
    /// nowhere in the flow. Use CreditsCountFormatted for anything that reads as a
    /// number.
    /// </summary>
    public string CreditsFormatted
    {
        get => _creditsFormatted;
        private set => SetProperty(ref _creditsFormatted, value);
    }

    private string _creditsCountFormatted = CreditsUnknown;

    /// <summary>
    /// The credit COUNT, grouped and without decimals - "66,950", not
    /// "$66.95 remaining (~10627 minutes)".
    ///
    /// The Done summary reads "{source} · {n} credits", so it wants the count, which is
    /// what OnboardingView.swift:457 passes through its own decimal formatter. Feeding
    /// it CreditsFormatted instead rendered "HyperWhisper Cloud · $66.95 remaining
    /// (~10627 minutes) credits". Nothing caught it because the Cloud branch never had
    /// a balance at Done until the activation refresh above started filling one in.
    ///
    /// Both cloud steps draw this in their big-number slot, so it carries the same
    /// ellipsis placeholder CreditsFormatted always had: an unknown balance has to read
    /// as unknown, never as a blank 30 pt line above a caption. The Done summary cannot
    /// see the placeholder because SourceSummary is gated on HasCredits, which
    /// ApplyCredits deliberately assigns last.
    /// </summary>
    public string CreditsCountFormatted
    {
        get => _creditsCountFormatted;
        private set => SetProperty(ref _creditsCountFormatted, value);
    }

    private bool _hasCredits;

    public bool HasCredits
    {
        get => _hasCredits;
        private set => SetProperty(ref _hasCredits, value);
    }

    private bool _isFetchingCredits;

    public bool IsFetchingCredits
    {
        get => _isFetchingCredits;
        private set => SetProperty(ref _isFetchingCredits, value);
    }

    /// <summary>Kick a balance refresh. Failures are swallowed into "unknown".</summary>
    public void RefreshCredits(bool force) => RefreshCredits(force, licenseKeyOverride: null);

    private void RefreshCredits(bool force, string? licenseKeyOverride)
    {
        if (!_isLive)
            return;

        RunTracked(OnboardingTaskKeys.CreditsRefresh, ct => RefreshCreditsCoreAsync(force, licenseKeyOverride, ct));
    }

    private async Task RefreshCreditsCoreAsync(bool force, string? licenseKeyOverride, CancellationToken cancellationToken)
    {
        ApplyCredits();

        try
        {
            await _credits.RefreshAsync(force, cancellationToken, licenseKeyOverride);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // Display only. A network failure here must never surface as a setup
            // error or close the gate; the figure simply stays unknown.
        }

        if (cancellationToken.IsCancellationRequested || !_isLive)
            return;

        ApplyCredits();
        _taskBox.Clear(OnboardingTaskKeys.CreditsRefresh);
    }

    private void ApplyCredits()
    {
        var credits = _credits.Credits;

        // The figures BEFORE the flag that gates them. SetProperty fans out to the
        // derived properties synchronously, and HasCredits is what SourceSummary reads
        // to decide whether to interpolate the count - so setting the flag first would
        // let the Done summary be recomputed as "HyperWhisper Cloud · … credits"
        // between the two lines.
        CreditsFormatted = credits?.FormattedBalance ?? CreditsUnknown;
        CreditsCountFormatted = credits is null
            ? CreditsUnknown
            : credits.CreditsRemaining.ToString("N0", CultureInfo.CurrentCulture);
        HasCredits = credits is not null;
        IsFetchingCredits = _credits.IsFetching;
    }

}
