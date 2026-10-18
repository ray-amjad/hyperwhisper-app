using HyperWhisper.Localization;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

public sealed class ModelDownloadChangedEventArgs : EventArgs
{
    public required string ModelId { get; init; }
    public double Progress { get; init; }
    public bool IsCompleted { get; init; }
    public bool IsSuccess { get; init; }

    /// <summary>
    /// The download ended because it was cancelled (a Cancel press or Quit).
    /// Check this, not the <see cref="Error"/> text, which is localized.
    /// </summary>
    public bool IsCancelled { get; init; }

    public string? Error { get; init; }
    public string? DisplayName { get; init; }
}

public sealed class ModelDownloadSnapshot
{
    public required string ModelId { get; init; }
    public required string DisplayName { get; init; }
    public double Progress { get; init; }
}

public sealed class ModelDownloadService
{
    public static ModelDownloadService Instance { get; } = new();

    private readonly WhisperModelService _whisperService = new();
    private readonly ParakeetModelService _parakeetService = new();
    private readonly LocalLlmModelService _localLlmService = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, ActiveDownload> _downloads = new();

    private ModelDownloadService()
    {
    }

    public event EventHandler<ModelDownloadChangedEventArgs>? DownloadChanged;

    public IReadOnlyList<ModelDownloadSnapshot> GetActiveDownloads()
    {
        lock (_sync)
        {
            return _downloads.Values
                .Select(d => new ModelDownloadSnapshot
                {
                    ModelId = d.Model.Id,
                    DisplayName = d.Model.DisplayName,
                    Progress = d.Progress
                })
                .ToList();
        }
    }

    public bool IsDownloading(string modelId)
    {
        lock (_sync)
        {
            return _downloads.ContainsKey(modelId);
        }
    }

    public bool TryStartDownload(LibraryModel model)
    {
        if (model.Payload is not WhisperModelInfo
            and not ParakeetModelInfo
            and not LocalLlmModelInfo)
        {
            LoggingService.Warn($"ModelDownloadService: Unsupported model download payload for {model.Id}");
            return false;
        }

        var download = new ActiveDownload(model);
        lock (_sync)
        {
            if (_downloads.ContainsKey(model.Id))
            {
                return false;
            }

            _downloads[model.Id] = download;
        }

        RaiseChanged(model.Id, 0, isCompleted: false, isSuccess: false, isCancelled: false, error: null, model.DisplayName);
        // StartDownload returns bool to its caller, so the download must outlive
        // the call; Quit awaits the task through CancelAllAsync.
        download.Run = Task.Run(() => RunDownloadAsync(download));
        return true;
    }

    public void CancelDownload(string modelId)
    {
        ActiveDownload? download;
        lock (_sync)
        {
            _downloads.TryGetValue(modelId, out download);
        }

        if (download == null) return;

        LoggingService.Info($"ModelDownloadService: Cancel requested for {download.Model.DisplayName}");
        TryCancel(download);
    }

    /// <summary>
    /// Cancel every running download and wait up to <paramref name="wait"/> for
    /// each to delete its partial file or folder. Called at Quit: a download the
    /// app exits under never reaches its own cleanup, so its partial stayed on
    /// disk (#1599). A download that outlasts the wait is left to the next
    /// start-up sweep (<see cref="ModelDownloadLeftovers"/>).
    /// </summary>
    public async Task CancelAllAsync(TimeSpan wait)
    {
        ActiveDownload[] downloads;
        lock (_sync)
        {
            downloads = _downloads.Values.ToArray();
        }

        if (downloads.Length == 0) return;

        LoggingService.Info($"ModelDownloadService: Quit cancels {downloads.Length} running download(s)");
        foreach (var download in downloads)
        {
            TryCancel(download);
        }

        var runs = downloads
            .Select(d => d.Run)
            .OfType<Task>()
            .ToArray();
        if (runs.Length == 0 || wait <= TimeSpan.Zero) return;

        try
        {
            await Task.WhenAll(runs).WaitAsync(wait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LoggingService.Warn("ModelDownloadService: A cancelled download had not cleaned up when Quit stopped waiting; the next start-up removes its partial");
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"ModelDownloadService: Waiting for cancelled downloads failed: {ex.Message}");
        }
    }

    private static void TryCancel(ActiveDownload download)
    {
        try
        {
            download.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The download finished between the lookup and the cancel.
        }
    }

    private async Task RunDownloadAsync(ActiveDownload download)
    {
        Result<string> result;
        var cancelRequested = false;
        try
        {
            // A download clicked right after launch must not recreate a partial
            // the start-up sweep is about to delete (#1599).
            await ModelDownloadLeftovers.WhenSwept.ConfigureAwait(false);
            result = download.Model.Payload switch
            {
                WhisperModelInfo whisper => await _whisperService.DownloadModelAsync(
                    whisper,
                    Progress(download),
                    download.Cancellation.Token),
                ParakeetModelInfo parakeet => await _parakeetService.DownloadModelAsync(
                    parakeet,
                    Progress(download),
                    download.Cancellation.Token),
                LocalLlmModelInfo localLlm => await _localLlmService.DownloadModelAsync(
                    localLlm,
                    Progress(download),
                    download.Cancellation.Token),
                _ => Result<string>.Failure("Unsupported model type")
            };
        }
        catch (OperationCanceledException)
        {
            result = Result<string>.Failure(Loc.S("settings.models.download.cancelled"));
        }
        catch (Exception ex)
        {
            result = Result<string>.Failure(Loc.S("settings.models.download.failed", ex.Message), ex);
        }
        finally
        {
            lock (_sync)
            {
                _downloads.Remove(download.Model.Id);
            }

            cancelRequested = download.Cancellation.IsCancellationRequested;
            download.Cancellation.Dispose();
        }

        if (result.IsSuccess)
        {
            // No local model-download counting — downloads are unlimited (open source).
            LoggingService.Info($"ModelDownloadService: Download completed for {download.Model.DisplayName}");
        }
        else if (result.Error?.Contains("cancelled", StringComparison.OrdinalIgnoreCase) == true)
        {
            LoggingService.Info($"ModelDownloadService: Download cancelled for {download.Model.DisplayName}");
        }

        RaiseChanged(
            download.Model.Id,
            download.Progress,
            isCompleted: true,
            isSuccess: result.IsSuccess,
            isCancelled: !result.IsSuccess && cancelRequested,
            error: result.Error,
            displayName: download.Model.DisplayName);
    }

    private IProgress<double> Progress(ActiveDownload download)
        => new DirectProgress<double>(p =>
        {
            var progress = Math.Clamp(p * 100, 0, 100);
            var shouldNotify = false;
            lock (_sync)
            {
                download.Progress = progress;
                if (progress >= 100 || progress - download.LastNotifiedProgress >= 1)
                {
                    download.LastNotifiedProgress = progress;
                    shouldNotify = true;
                }
            }

            if (shouldNotify)
            {
                RaiseChanged(download.Model.Id, progress, isCompleted: false, isSuccess: false, isCancelled: false, error: null, download.Model.DisplayName);
            }
        });

    private void RaiseChanged(
        string modelId,
        double progress,
        bool isCompleted,
        bool isSuccess,
        bool isCancelled,
        string? error,
        string? displayName)
    {
        var handler = DownloadChanged;
        if (handler == null) return;

        var args = new ModelDownloadChangedEventArgs
        {
            ModelId = modelId,
            Progress = progress,
            IsCompleted = isCompleted,
            IsSuccess = isSuccess,
            IsCancelled = isCancelled,
            Error = error,
            DisplayName = displayName
        };

        foreach (EventHandler<ModelDownloadChangedEventArgs> subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"ModelDownloadService: DownloadChanged subscriber failed: {ex.Message}");
            }
        }
    }

    private sealed class ActiveDownload
    {
        public ActiveDownload(LibraryModel model)
        {
            Model = model;
        }

        public LibraryModel Model { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public double Progress { get; set; }
        public double LastNotifiedProgress { get; set; }
        public Task? Run { get; set; }
    }

    private sealed class DirectProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public DirectProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value)
        {
            _handler(value);
        }
    }
}
