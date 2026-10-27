using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HyperWhisper.Linux;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.ViewModels;

// Google's newer Gemini keys do not start with "AIza". The provider-key modal refused them with an
// "Invalid API Key" notice, because its key check copied Windows' "AIza" prefix rule, so a customer
// with a working key could not configure Gemini 3.5 Transcribe at all. This opens the real modal for
// the Gemini 3.5 Transcribe slot under the real App, types a key that starts "AQ.", presses the real
// Save button, and reads the store and the modal.
static class ProviderApiKeyNonAizaGeminiRenderTests
{
    public const string ProbeArgument = "--probe-provider-key-non-aiza-gemini";
    private const string Account = "GeminiTranscribeApiKey";
    // "AQ." + 37 characters: the shape of a newer Google key. Not a real key.
    private static readonly string TypedKey = "AQ." + new string('k', 37);

    public static Task RunAsync() =>
        XvfbProbe.RunAsync(ProbeArgument, "the non-AIza Gemini key probe", "en", "1280x800");

    /// <summary>The child half. Exit 0 only if the key is stored and the modal closes with no notice.</summary>
    public static int RunProbe()
    {
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            var store = new RecordingStore();
            var credentials = new CredentialManagementViewModel(store);
            var window = new ProviderApiKeyWindow(credentials, Account, "Gemini 3.5 Transcribe", string.Empty);
            var failures = new List<string>();
            var exit = 2;
            using var done = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    window.Show();
                    await Settle();
                    Named<TextBox>(window, "ProviderKeyInput").Text = TypedKey;
                    Named<Button>(window, "ProviderKeySaveButton")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Settle();

                    var notices = window.OwnedWindows.Count;
                    store.Written.TryGetValue(Account, out var written);
                    Console.WriteLine($"after Save: stored {(written is null ? "nothing" : $"{written.Length} chars")}, "
                        + $"modal open {window.IsVisible}, notices {notices}");
                    if (notices != 0) failures.Add("Save opened a notice (the Invalid API Key dialog) over the modal");
                    if (written != TypedKey) failures.Add($"the key was not stored under {Account}");
                    if (window.IsVisible) failures.Add("the modal stayed open after a good key");
                    exit = failures.Count == 0 ? 0 : 1;
                }
                catch (Exception exception) { Console.Error.WriteLine(exception); exit = 2; }
                finally
                {
                    foreach (var owned in window.OwnedWindows.ToList()) owned.Close();
                    window.Close();
                    done.Cancel();
                }
            });
            Dispatcher.UIThread.MainLoop(done.Token);
            foreach (var failure in failures) Console.Error.WriteLine($"FAIL {failure}");
            return exit;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 2; }
    }

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"the provider-key modal shows no {name}");

    private static async Task Settle()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(300);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private sealed class RecordingStore : ICredentialStore
    {
        public Dictionary<string, string> Written { get; } = new(StringComparer.Ordinal);
        public PlatformResult<byte[]?> Read(string resource, string account) =>
            PlatformResult<byte[]?>.Success(Written.TryGetValue(account, out var value)
                ? System.Text.Encoding.UTF8.GetBytes(value)
                : null);
        public PlatformResult Write(string resource, string account, ReadOnlySpan<byte> value)
        {
            Written[account] = System.Text.Encoding.UTF8.GetString(value);
            return PlatformResult.Success();
        }
        public PlatformResult Delete(string resource, string account)
        {
            Written.Remove(account);
            return PlatformResult.Success();
        }
    }
}
