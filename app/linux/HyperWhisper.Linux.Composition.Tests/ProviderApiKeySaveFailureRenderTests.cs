using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HyperWhisper.Linux;
using HyperWhisper.Platform.Abstractions;
using HyperWhisper.PortableApplication.ViewModels;

// Issue #1388. CredentialManagementViewModel.SaveAsync now keeps the typed key in Secret when the
// secure-storage write fails, so the credentials page can offer it again. The Model Library's
// provider-key modal drives the same, long-lived view model but reads its own ProviderKeyInput, so
// it must not leave that key in Secret: a cancelled modal would pre-fill the credentials page, and a
// Save there under another account would store this key under that account. This opens the real
// modal under the real App over a store whose Write fails, types a key, presses the real Save
// button, and reads the view model and the modal.
static class ProviderApiKeySaveFailureRenderTests
{
    public const string ProbeArgument = "--probe-provider-key-save-failure";
    private const string TypedKey = "sk-typed-key-that-the-store-refused-1388";

    public static Task RunAsync() =>
        XvfbProbe.RunAsync(ProbeArgument, "the #1388 provider-key save-failure probe", "en", "1280x800");

    /// <summary>The child half. Exit 0 only if a failed save leaves no key in the shared view model.</summary>
    public static int RunProbe()
    {
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            var credentials = new CredentialManagementViewModel(new FailingWriteStore());
            var window = new ProviderApiKeyWindow(credentials, "OpenAIApiKey", "OpenAI", string.Empty);
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

                    var status = Named<TextBlock>(window, "ProviderKeyStatus").Text;
                    Console.WriteLine($"after a failed save: Secret length {credentials.Secret.Length}, "
                        + $"error {credentials.Status.ErrorCode ?? "<none>"}, modal open {window.IsVisible}, status \"{status}\"");
                    if (credentials.Status.ErrorCode != FailingWriteStore.Code)
                        failures.Add($"the save did not fail with {FailingWriteStore.Code}: {credentials.Status.ErrorCode ?? "<none>"}");
                    if (credentials.Secret.Length != 0)
                        failures.Add("a failed save left the typed key in the shared credentials view model");
                    if (!window.IsVisible) failures.Add("the modal closed on a failed save");
                    if (status != FailingWriteStore.Message) failures.Add($"the modal shows \"{status}\", not the store's error");
                    if (Named<TextBox>(window, "ProviderKeyInput").Text != TypedKey)
                        failures.Add("the modal emptied its own key field on a failed save");
                    exit = failures.Count == 0 ? 0 : 1;
                }
                catch (Exception exception) { Console.Error.WriteLine(exception); exit = 2; }
                finally { window.Close(); done.Cancel(); }
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

    private sealed class FailingWriteStore : ICredentialStore
    {
        public const string Code = "credentials.write_failed";
        public const string Message = "simulated secure store failure";
        public PlatformResult<byte[]?> Read(string resource, string account) => PlatformResult<byte[]?>.Success(null);
        public PlatformResult Write(string resource, string account, ReadOnlySpan<byte> value) =>
            PlatformResult.Failure(Code, Message);
        public PlatformResult Delete(string resource, string account) => PlatformResult.Success();
    }
}
