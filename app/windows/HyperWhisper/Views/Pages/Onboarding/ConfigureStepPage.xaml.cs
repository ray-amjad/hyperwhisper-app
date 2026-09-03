using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using HyperWhisper.Services;
using HyperWhisper.ViewModels.Onboarding;

namespace HyperWhisper.Views.Pages.Onboarding;

/// <summary>
/// Presentation only. The DataContext is the one OnboardingFlowViewModel the
/// window owns; this page constructs nothing and decides nothing.
/// </summary>
public partial class ConfigureStepPage : Page
{
    private PasswordBox? _apiKeyBox;
    private bool _syncingPassword;

    public ConfigureStepPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// The masked field lives inside OnboardingStage, which owns its own XAML name
    /// scope, so x:Name on it is a compile error (MC3093). Loaded hands the
    /// reference over instead; nothing else about the field changes.
    /// </summary>
    private void ApiKeyBox_Loaded(object sender, RoutedEventArgs e)
    {
        _apiKeyBox = (PasswordBox)sender;
        SyncPasswordFromModel();
    }

    /// <summary>
    /// PasswordBox deliberately exposes no bindable Password property, so the one
    /// supported way to get the typed key onto the flow model is this handler. It
    /// carries no policy: the model owns the invalidation that follows.
    /// </summary>
    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword)
            return;

        if (DataContext is OnboardingFlowViewModel flow)
            flow.ApiKeyInput = ((PasswordBox)sender).Password;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is OnboardingFlowViewModel previous)
            previous.PropertyChanged -= OnFlowPropertyChanged;

        if (e.NewValue is OnboardingFlowViewModel current)
            current.PropertyChanged += OnFlowPropertyChanged;

        SyncPasswordFromModel();
    }

    private void OnFlowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OnboardingFlowViewModel.ApiKeyInput))
            SyncPasswordFromModel();
    }

    /// <summary>
    /// The model clears ApiKeyInput when the provider changes, so that a key typed
    /// for one vendor can never be saved under another. Without this the field
    /// would still show the old key and the user would think it was still there.
    /// </summary>
    private void SyncPasswordFromModel()
    {
        if (_apiKeyBox is null || DataContext is not OnboardingFlowViewModel flow)
            return;

        if (string.Equals(_apiKeyBox.Password, flow.ApiKeyInput, StringComparison.Ordinal))
            return;

        _syncingPassword = true;
        try
        {
            _apiKeyBox.Password = flow.ApiKeyInput;
        }
        finally
        {
            _syncingPassword = false;
        }
    }

    /// <summary>
    /// Opens the site where credits are bought. macOS does the same from its view
    /// (OnboardingSourceViews.swift:341-344): a link is presentation, not policy.
    /// </summary>
    private void GetCredits_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://hyperwhisper.com") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"ConfigureStepPage: could not open the credits page: {ex.Message}");
        }
    }
}
