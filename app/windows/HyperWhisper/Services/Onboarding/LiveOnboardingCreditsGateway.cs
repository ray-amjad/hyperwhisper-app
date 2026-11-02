// The live IOnboardingCreditsGateway adapter for the first-run flow. Part of the onboarding
// composition described in OnboardingLiveDependencies.cs; split out of that file (#941).

using HyperWhisper.Models;
using HyperWhisper.ViewModels.Onboarding;

namespace HyperWhisper.Services.Onboarding;

// =============================================================================
// Credits
// =============================================================================

/// <summary>
/// The cloud credit balance, flattened to what the two cloud panels show.
///
/// Display only: it never gates Continue, and a fetch failure is swallowed into
/// "credits unknown" rather than becoming a setup error. macOS gets this by
/// injecting HyperWhisperCloudManager into the sheet as an @EnvironmentObject,
/// which the singleton rule at the top of this file forbids here.
/// </summary>
public sealed class LiveOnboardingCreditsGateway : IOnboardingCreditsGateway, IDisposable
{
    private readonly HyperWhisperCloudManager _manager;
    private bool _disposed;

    public LiveOnboardingCreditsGateway(HyperWhisperCloudManager manager)
    {
        _manager = manager;
        _manager.CreditsUpdated += OnCreditsUpdated;
        _manager.PropertyChanged += OnManagerPropertyChanged;
    }

    public event EventHandler? CreditsChanged;

    public OnboardingCloudCredits? Credits => Flatten(_manager.Credits);

    public bool IsFetching => _manager.IsFetchingCredits;

    internal static OnboardingCloudCredits? Flatten(HyperWhisperCloudCredits? credits) =>
        credits is null
            ? null
            : new OnboardingCloudCredits(credits.CreditsRemaining, credits.MinutesRemaining, credits.FormattedBalance);

    /// <summary>
    /// FetchCreditsAsync self-guards against re-entry, so calling this on entry to
    /// both cloud steps is safe. A failure never propagates: the panels render an
    /// ellipsis and the flow moves on.
    /// </summary>
    public async Task RefreshAsync(bool force, CancellationToken cancellationToken, string? licenseKeyOverride = null)
    {
        try
        {
            await _manager.FetchCreditsAsync(force, cancellationToken, licenseKeyOverride);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"LiveOnboardingCreditsGateway: credits unavailable: {ex.Message}");
        }
        finally
        {
            CreditsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnCreditsUpdated(object? sender, EventArgs e) => CreditsChanged?.Invoke(this, EventArgs.Empty);

    private void OnManagerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // The spinner state has to reach the UI too, not just the figure.
        if (e.PropertyName is nameof(HyperWhisperCloudManager.IsFetchingCredits)
            or nameof(HyperWhisperCloudManager.Credits))
        {
            CreditsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The manager is a process-lifetime singleton, so a handler left attached
    /// outlives the onboarding window.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _manager.CreditsUpdated -= OnCreditsUpdated;
        _manager.PropertyChanged -= OnManagerPropertyChanged;
    }
}
