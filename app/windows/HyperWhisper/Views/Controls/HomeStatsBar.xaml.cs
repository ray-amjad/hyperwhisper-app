using System.Globalization;
using System.Windows;
using Button = System.Windows.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;
using UserControl = System.Windows.Controls.UserControl;
using HyperWhisper.ViewModels;

namespace HyperWhisper.Views.Controls;

public partial class HomeStatsBar : UserControl
{
    public HomeStatsBar()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        RefreshMenuChecks();
    }

    private HomeStatsBarViewModel? ViewModel => DataContext as HomeStatsBarViewModel;

    private void TypingSpeedButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu == null) return;
        RefreshMenuChecks();
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void TypingSpeedMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        if (sender is not MenuItem item) return;
        if (item.Tag is not string tag) return;
        if (!int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wpm)) return;

        ViewModel.SetTypingSpeed(wpm);
        RefreshMenuChecks();
    }

    private void RefreshMenuChecks()
    {
        var current = ViewModel?.TypingSpeedWpm ?? 40;
        foreach (var item in TypingSpeedMenu.Items)
        {
            if (item is MenuItem menuItem && menuItem.Tag is string tag
                && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wpm))
            {
                menuItem.IsCheckable = true;
                menuItem.IsChecked = wpm == current;
            }
        }
    }
}
