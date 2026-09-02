using System.Windows.Controls;
using HyperWhisper.ViewModels.Onboarding;
using HyperWhisper.Views.Controls;

namespace HyperWhisper.Views.Pages.Onboarding;

/// <summary>
/// Presentation only. The DataContext is the one OnboardingFlowViewModel the
/// window owns; this page constructs nothing and decides nothing.
/// </summary>
public partial class PermissionsStepPage : Page
{
    public PermissionsStepPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The inline recorder has already validated the chord (same control, same
    /// ShortcutValidationService as the Shortcuts settings page). All that is
    /// left is to hand the persisted string to the flow, which owns the seam
    /// that writes it and re-reads the registration outcome.
    /// </summary>
    private void ShortcutRecorder_Captured(object sender, ShortcutCapturedEventArgs e)
    {
        (DataContext as OnboardingFlowViewModel)?.ApplyToggleShortcut(e.Persisted);
    }
}
