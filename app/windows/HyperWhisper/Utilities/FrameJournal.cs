// KEEPING NO BACK STACK IN A FRAME (issue #977)
//
// A WPF Frame journals every page it leaves, and a journaled page stays alive
// for as long as the Frame does. The app shows no back button on any Frame it
// navigates by code, so for those Frames the journal is only a leak: every
// visited page, its view model and whatever the view model holds.
//
// The rule lives here so each such Frame opts in with one call. The handler is
// static and reads the Frame from sender, so the subscription roots nothing and
// needs no unsubscribe, whatever the Frame's lifetime.

using System.Windows.Controls;
using System.Windows.Navigation;

namespace HyperWhisper.Utilities;

internal static class FrameJournal
{
    /// <summary>
    /// Gives <paramref name="frame"/> its own journal and empties its back stack
    /// after every navigation. Call it once, before the first navigation.
    /// </summary>
    internal static void KeepNoBackStack(Frame frame)
    {
        // A Frame hosted inside another navigator (SettingsPage sits in
        // MainWindow's ContentFrame) would otherwise write its entries into the
        // PARENT journal, where its own Navigated handler cannot remove them.
        frame.JournalOwnership = JournalOwnership.OwnsJournal;
        frame.Navigated += DropBackStack;
    }

    private static void DropBackStack(object sender, NavigationEventArgs e)
    {
        if (sender is not Frame frame)
            return;

        while (frame.CanGoBack)
            frame.RemoveBackEntry();
    }
}
