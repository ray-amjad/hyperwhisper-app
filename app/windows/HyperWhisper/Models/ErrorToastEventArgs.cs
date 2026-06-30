using System;

namespace HyperWhisper.Models;

/// <summary>
/// Event arguments for showing an error toast notification.
/// Used by MainViewModel to communicate error messages to the UI layer.
/// </summary>
public class ErrorToastEventArgs : EventArgs
{
    /// <summary>
    /// The error message to display in the toast.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Whether to show the "Open Settings" button.
    /// Typically true for actionable errors like missing API keys.
    /// </summary>
    public bool ShowSettingsButton { get; }

    /// <summary>
    /// Optional section to navigate to when Settings button is clicked.
    /// Examples: "General", "Shortcuts", "Output", "License"
    /// </summary>
    public string? SettingsSection { get; }

    /// <summary>
    /// Optional guidance text shown below the error message.
    /// Used to provide actionable tips (e.g., for no-speech errors).
    /// </summary>
    public string? GuidanceText { get; }

    /// <summary>
    /// When true, the toast's action button routes to the Model Library page
    /// and opens the API keys manager modal (mirrors macOS AppState.navigateToModelLibraryAPIKeys).
    /// Takes precedence over <see cref="SettingsSection"/>.
    /// </summary>
    public bool OpenApiKeysManager { get; }

    public ErrorToastEventArgs(string message, bool showSettingsButton = false, string? settingsSection = null, string? guidanceText = null, bool openApiKeysManager = false)
    {
        Message = message;
        ShowSettingsButton = showSettingsButton;
        SettingsSection = settingsSection;
        GuidanceText = guidanceText;
        OpenApiKeysManager = openApiKeysManager;
    }
}
