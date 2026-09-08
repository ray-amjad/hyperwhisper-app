using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using HyperWhisper.Models;

namespace HyperWhisper.ViewModels;

public partial class MainViewModel
{
    private void InitializeGettingStarted()
    {
        var completedSteps = _settingsService.GettingStartedCompletedSteps
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();

        GettingStartedItems = new ObservableCollection<GettingStartedItem>
        {
            new() { Id = "recording", Icon = "\U0001F3A4", IconColor = System.Windows.Media.Color.FromRgb(0, 122, 255), Title = Localization.Loc.S("home.gettingStarted.recording.title"), Description = Localization.Loc.S("home.gettingStarted.recording.description"), IsCompleted = completedSteps.Contains("recording") },
            new() { Id = "shortcuts", Icon = "\u2328\uFE0F", IconColor = System.Windows.Media.Color.FromRgb(175, 82, 222), Title = Localization.Loc.S("home.gettingStarted.shortcuts.title"), Description = Localization.Loc.S("home.gettingStarted.shortcuts.description"), IsCompleted = completedSteps.Contains("shortcuts") },
            new() { Id = "mode", Icon = "\U0001F3AF", IconColor = System.Windows.Media.Color.FromRgb(52, 199, 89), Title = Localization.Loc.S("home.gettingStarted.mode.title"), Description = Localization.Loc.S("home.gettingStarted.mode.description"), IsCompleted = completedSteps.Contains("mode") },
            new() { Id = "vocabulary", Icon = "\U0001F4DA", IconColor = System.Windows.Media.Color.FromRgb(255, 149, 0), Title = Localization.Loc.S("home.gettingStarted.vocabulary.title"), Description = Localization.Loc.S("home.gettingStarted.vocabulary.description"), IsCompleted = completedSteps.Contains("vocabulary") },
        };

        // Seeded through the same method that keeps them current, so the initial
        // badge and the refreshed badge cannot disagree about which row shows what.
        RefreshGettingStartedShortcuts();

        ShowGettingStarted = completedSteps.Count < 4;
    }

    private void RefreshGettingStartedShortcuts() =>
        ApplyShortcutsToGettingStarted(
            GettingStartedItems,
            HotkeyText,
            _settingsService.ChangeModeShortcut.ToDisplayString());

    /// <summary>
    /// Puts the current hotkeys onto the Getting Started rows that teach them.
    /// </summary>
    /// <remarks>
    /// Two rows carry a badge, and both were stale after a shortcut change: the rows
    /// are built once, by InitializeGettingStarted, whose only caller is
    /// OnNavigatedToAsync behind an "if (IsInitialized) return;". So Home kept telling
    /// a new user to press a chord that no longer did anything while the status bar
    /// beside it was already right, and only a restart fixed it.
    ///
    /// static and internal so the smoke suite can assert the mapping without standing
    /// up a MainViewModel and the dozen services behind it.
    /// </remarks>
    internal static void ApplyShortcutsToGettingStarted(
        IEnumerable<GettingStartedItem> items, string toggleText, string changeModeText)
    {
        foreach (var item in items)
        {
            switch (item.Id)
            {
                case "recording":
                    item.ShortcutText = toggleText;
                    break;
                case "mode":
                    item.ShortcutText = changeModeText;
                    break;
            }
        }
    }

    [RelayCommand]
    private void ToggleGettingStartedStep(string stepId)
    {
        var completedSteps = _settingsService.GettingStartedCompletedSteps
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();

        if (completedSteps.Contains(stepId))
            completedSteps.Remove(stepId);
        else
            completedSteps.Add(stepId);

        _settingsService.GettingStartedCompletedSteps = string.Join(",", completedSteps.OrderBy(s => s));

        var item = GettingStartedItems.FirstOrDefault(i => i.Id == stepId);
        if (item != null)
            item.IsCompleted = completedSteps.Contains(stepId);

        ShowGettingStarted = completedSteps.Count < 4;

        // Navigate to relevant page on check (not uncheck)
        if (completedSteps.Contains(stepId))
        {
            switch (stepId)
            {
                case "shortcuts": CurrentPage = NavigationPage.Settings; break;
                case "mode": CurrentPage = NavigationPage.Modes; break;
                case "vocabulary": CurrentPage = NavigationPage.Vocabulary; break;
            }
        }
    }
}
