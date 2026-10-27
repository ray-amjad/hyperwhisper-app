using CommunityToolkit.Mvvm.ComponentModel;

namespace HyperWhisper.Models;

public partial class GettingStartedItem : ObservableObject
{
    public string Id { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public System.Windows.Media.Color IconColor { get; init; } = System.Windows.Media.Colors.Blue;

    /// <summary>
    /// Whether the step is really done (#638). Written only by
    /// MainViewModel.ApplyGettingStartedProgress from real app state (a saved
    /// transcript, a changed shortcut, a user-made Mode, a vocabulary word),
    /// never by a click on the row.
    /// </summary>
    [ObservableProperty]
    private bool _isCompleted;

    /// <summary>
    /// The hotkey this row is teaching, or null for a row that teaches no hotkey -
    /// the Home badge is hidden on null.
    ///
    /// Observable, not init-only. The rows are built once, by InitializeGettingStartedAsync
    /// behind an IsInitialized guard, so an init-only badge could never follow a
    /// shortcut the user changed in Settings: the Start Recording row went on telling
    /// a new user to press a chord that no longer did anything, on the one row whose
    /// whole job is to teach them the hotkey, until the app was restarted.
    /// </summary>
    [ObservableProperty]
    private string? _shortcutText;
}
