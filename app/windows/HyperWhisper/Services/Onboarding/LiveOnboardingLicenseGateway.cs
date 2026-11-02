// The live IOnboardingLicenseGateway adapter for the first-run flow. Part of the onboarding
// composition described in OnboardingLiveDependencies.cs; split out of that file (#941).

using HyperWhisper.Localization;
using HyperWhisper.Models;
using HyperWhisper.ViewModels.Onboarding;

namespace HyperWhisper.Services.Onboarding;

// =============================================================================
// License
// =============================================================================

/// <summary>
/// HyperWhisper Cloud. Probe is read-only; Activate is the single explicit action
/// that writes account state. Entitlement itself stays server side and there is
/// no local shortcut here - there must never be one.
/// </summary>
public sealed class LiveOnboardingLicenseGateway : IOnboardingLicenseGateway
{
    private readonly LicenseManager _manager;

    public LiveOnboardingLicenseGateway(LicenseManager manager)
    {
        _manager = manager;
    }

    public bool IsActive => _manager.IsLicensed;

    // A SWALLOWED CANCEL IS STILL A CANCEL.
    //
    // LicenseNetworkService catches OperationCanceledException and returns
    // Failed("Validation cancelled", Invalid) rather than rethrowing, so without
    // these two lines the flow's own `catch (OperationCanceledException) { return; }`
    // never ran and a cancelled call arrived at the continuation looking exactly
    // like a rejected licence key - complete with "Validation cancelled" rendered
    // as the reason the key was refused.

    public async Task<OnboardingLicenseOutcome> ProbeAsync(string key, CancellationToken cancellationToken)
    {
        var result = await _manager.ProbeLicenseAsync(key, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ToOutcome(result);
    }

    public async Task<OnboardingLicenseOutcome> ActivateAsync(string key, CancellationToken cancellationToken)
    {
        var result = await _manager.ActivateLicenseAsync(key, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ToOutcome(result);
    }

    private static OnboardingLicenseOutcome ToOutcome(LicenseValidationResult result) =>
        new(result.IsValid, result.IsValid ? null : (result.ErrorMessage ?? Loc.S("app.unknown.error")));
}
