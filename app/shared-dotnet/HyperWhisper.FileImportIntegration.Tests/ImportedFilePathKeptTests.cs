using System.Windows.Input;
using HyperWhisper.Data.Entities;
using HyperWhisper.FileTranscription;
using HyperWhisper.PortableApplication.Persistence;
using HyperWhisper.PortableApplication.Transcription;
using HyperWhisper.PortableApplication.ViewModels;

/// <summary>
/// Issue #2911: Home "Transcribe file" wrote the app's import copy into the bound path field.
/// A failed transcription deletes that copy, so pressing the button again failed with
/// "The selected audio file no longer exists." although the user's file was untouched.
/// </summary>
static class ImportedFilePathKeptTests
{
    public static async Task FailedTranscriptionKeepsPickedPath(string root)
    {
        var source = Path.Combine(root, $"{Guid.NewGuid():N}-picked.ogg");
        File.WriteAllBytes(source, [9, 8, 7, 6]);
        var paths = new TestPaths(Path.Combine(root, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(paths.RecordingsDirectory);
        var transcriber = new CountingTranscriber(PortableTranscriptionResult.Failed(
            PortableTranscriptionErrorCode.TranscriptionFailed, "expected"));
        var workflow = new TranscriptionWorkflow(
            new FakeRecorder(), new FakeDevices(), transcriber, new MemoryHistory());
        var importer = new DurableAudioImportService(new UnixPrivateFiles(), paths, normalizer: new FakeNormalizer());
        var preflight = new PortableFileTranscriptionPreflight(
            new StreamingFileAudioMetadataSource(), new FakeReadiness(), new FakeCredentials());
        var mode = new Mode
        {
            Name = "Cloud", ProviderType = "cloud", CloudProvider = "openai",
            CloudTranscriptionModel = "whisper-1", Language = "en",
        };
        using var viewModel = new TranscriptionWorkflowViewModel(
            workflow, () => new TranscriptionWorkflowRequest(SelectedMode: mode), importer, preflight)
        { FilePath = source };

        await RunCommand(viewModel.TranscribeFileCommand, transcriber, expectedCalls: 1);

        Check(viewModel.ErrorCode is not null, "the failing transcription did not report a failure");
        Check(string.Equals(viewModel.FilePath, source, StringComparison.Ordinal),
            $"FilePath was replaced by '{viewModel.FilePath}' instead of keeping the picked '{source}'");
        var owned = transcriber.Paths[0];
        Check(!string.Equals(owned, source, StringComparison.Ordinal)
            && owned.StartsWith(paths.RecordingsDirectory, StringComparison.Ordinal),
            $"the workflow did not receive the imported copy (got '{owned}')");
        Check(!File.Exists(owned), "the failed owned import copy was not deleted");
        Check(File.Exists(source), "the failure deleted the user's picked file");

        // The retry the user makes after fixing the cause must import the picked file again.
        await RunCommand(viewModel.TranscribeFileCommand, transcriber, expectedCalls: 2);
        Check(transcriber.Paths.Count == 2 && transcriber.Paths[1] != owned
            && string.Equals(viewModel.FilePath, source, StringComparison.Ordinal),
            $"retry did not import the picked file again ({viewModel.ErrorCode}: {viewModel.Message})");
    }

    // AsyncCommand.Execute is async void: completion is the CanExecuteChanged that follows the
    // transcriber call with the command executable again.
    private static async Task RunCommand(ICommand command, CountingTranscriber transcriber, int expectedCalls)
    {
        Check(command.CanExecute(null), "TranscribeFileCommand was not executable");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs e)
        {
            if (command.CanExecute(null) && transcriber.Paths.Count >= expectedCalls) done.TrySetResult();
        }
        command.CanExecuteChanged += OnChanged;
        try
        {
            command.Execute(null);
            OnChanged(null, EventArgs.Empty);
            try { await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException)
            {
                throw new InvalidOperationException(
                    $"TranscribeFileCommand did not reach transcription call {expectedCalls} "
                    + $"(transcriber calls: {transcriber.Paths.Count})");
            }
        }
        finally { command.CanExecuteChanged -= OnChanged; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CountingTranscriber(PortableTranscriptionResult result) : IRecordedAudioTranscriber
    {
        private readonly List<string> _paths = [];
        public IReadOnlyList<string> Paths { get { lock (_paths) return [.. _paths]; } }
        public TranscriptionBackendCapability Capability => new(true, "test");
        public Task<PortableTranscriptionResult> TranscribeAsync(
            string audioPath, TranscriptionWorkflowRequest request, CancellationToken cancellationToken = default)
        { lock (_paths) _paths.Add(audioPath); return Task.FromResult(result); }
        public Task<PortableTranscriptionResult> TranscribeAsync(
            string audioPath, string? language, CancellationToken cancellationToken = default)
        { lock (_paths) _paths.Add(audioPath); return Task.FromResult(result); }
    }
}
