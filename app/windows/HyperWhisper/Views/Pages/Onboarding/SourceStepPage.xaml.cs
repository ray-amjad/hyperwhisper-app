using System.Windows.Controls;

namespace HyperWhisper.Views.Pages.Onboarding;

/// <summary>
/// Presentation only. The DataContext is the one OnboardingFlowViewModel the
/// window owns; this page constructs nothing and decides nothing.
/// </summary>
public partial class SourceStepPage : Page
{
    public SourceStepPage()
    {
        InitializeComponent();
    }
}
