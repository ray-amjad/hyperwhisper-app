using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HyperWhisper.Linux;
using HyperWhisper.Linux.Overlay;

// Issue #2632. When Get Credits or Manage account could not open the browser, the toast offered Open
// Settings, which went to the Cloud page the user was already on, and its message (which carries the
// URL) could not be selected. The two account-link codes now get no button, every other account.*
// code keeps its Cloud settings jump, and the message is selectable.
static class AccountLinkToastRenderTests
{
    public const string ProbeArgument = "--probe-account-link-toast";

    public static Task ClassifierDropsTheButtonForAccountLinks()
    {
        static void Expect(string code, LinuxErrorToastAction expected)
        {
            var actual = MainWindow.ClassifyErrorToastAction(code);
            if (actual != expected) throw new InvalidOperationException($"{code} maps to {actual}, expected {expected}");
        }

        Expect("account.link_failed", LinuxErrorToastAction.None);
        Expect("account.link_rejected", LinuxErrorToastAction.None);
        Expect("ACCOUNT.LINK_FAILED", LinuxErrorToastAction.None);
        // Every other account code still jumps to the Cloud page, where it can be acted on.
        foreach (var code in (string[])["account.credits_failed", "account.activation_failed", "account.status_failed",
                     "account.deactivation_failed", "account.key_required", "account.key_format", "account.cancelled",
                     "account.insufficient_credits", "streaming.cloud_account_missing"])
            Expect(code, LinuxErrorToastAction.CloudSettings);
        // The other destinations are unchanged.
        Expect("provider.credential_rejected", LinuxErrorToastAction.ApiKeys);
        Expect("models.not_downloaded", LinuxErrorToastAction.ModelLibrary);
        Expect("workflow.transcription_failed", LinuxErrorToastAction.None);
        return Task.CompletedTask;
    }

    public static Task RunAsync() =>
        XvfbProbe.RunAsync(ProbeArgument, "the #2632 account-link toast probe", "en", "1440x900");

    /// <summary>The child half: the real toast window, shown the way MainWindow.ShowErrorToast shows it.</summary>
    public static int RunProbe()
    {
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            var failures = new List<string>();
            var exit = 2;
            using var done = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await Run(failures);
                    exit = failures.Count == 0 ? 0 : 1;
                }
                catch (Exception exception) { Console.Error.WriteLine(exception); exit = 2; }
                finally { done.Cancel(); }
            });
            Dispatcher.UIThread.MainLoop(done.Token);
            foreach (var failure in failures) Console.Error.WriteLine($"FAIL {failure}");
            return exit;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 2; }
    }

    private static async Task Run(List<string> failures)
    {
        const string message = "The account page could not be opened in the browser. https://www.hyperwhisper.com/credits";
        var toast = new LinuxErrorToastWindow();
        try
        {
            toast.DismissImmediately();
            toast.ShowError(message, MainWindow.ClassifyErrorToastAction("account.link_failed"));
            await Settle();
            var button = Named<Button>(toast, "ToastSettingsButton");
            Console.WriteLine($"account.link_failed: Open Settings {(button.IsEffectivelyVisible ? "shown" : "hidden")}");
            if (button.IsEffectivelyVisible) failures.Add("account.link_failed still shows Open Settings");

            if (Named<TextBlock>(toast, "ToastMessage") is not SelectableTextBlock text)
            {
                failures.Add("the toast message is not a SelectableTextBlock, so its URL cannot be selected");
                return;
            }
            text.SelectAll();
            Console.WriteLine($"selected: \"{text.SelectedText}\"");
            if (text.SelectedText != message) failures.Add($"selecting the message gave \"{text.SelectedText}\"");

            // The window is reused: the next toast starts with nothing selected, and another account code keeps its button.
            toast.DismissImmediately();
            toast.ShowError("Credits could not be loaded.", MainWindow.ClassifyErrorToastAction("account.credits_failed"));
            await Settle();
            Console.WriteLine($"account.credits_failed: Open Settings {(button.IsEffectivelyVisible ? "shown" : "hidden")}, selection \"{text.SelectedText}\"");
            if (!button.IsEffectivelyVisible) failures.Add("account.credits_failed lost its Open Settings button");
            if (!string.IsNullOrEmpty(text.SelectedText)) failures.Add($"the reused toast kept the old selection \"{text.SelectedText}\"");
        }
        finally { toast.DismissImmediately(); toast.Close(); }
    }

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"the toast has no {name}");

    private static async Task Settle()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(300);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
