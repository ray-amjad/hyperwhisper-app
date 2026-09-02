using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Services;

namespace HyperWhisper.Views.Pages.Onboarding;

/// <summary>
/// Presentation only. The DataContext is the one OnboardingFlowViewModel the
/// window owns; this page constructs nothing and decides nothing.
/// </summary>
public partial class MicrophoneStepPage : Page
{
    public MicrophoneStepPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opens the Sound page of Windows Settings. This is a shell link, the same
    /// shape as ConfigureStepPage's credits link, so it stays in the view: it
    /// reads no state and changes none. The privacy page is different and does
    /// go through the flow model, because the permission seam owns it.
    /// </summary>
    private void SoundSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"MicrophoneStepPage: could not open Sound settings: {ex.Message}");
        }
    }
}
