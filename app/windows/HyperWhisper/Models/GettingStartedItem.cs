using CommunityToolkit.Mvvm.ComponentModel;

namespace HyperWhisper.Models;

public partial class GettingStartedItem : ObservableObject
{
    public string Id { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string? ShortcutText { get; init; }
    public System.Windows.Media.Color IconColor { get; init; } = System.Windows.Media.Colors.Blue;

    [ObservableProperty]
    private bool _isCompleted;
}
