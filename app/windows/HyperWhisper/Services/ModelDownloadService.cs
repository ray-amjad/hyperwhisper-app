using HyperWhisper.Localization;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

public sealed class ModelDownloadChangedEventArgs : EventArgs
{
    public required string ModelId { get; init; }
    public double Progress { get; init; }
    public bool IsCompleted { get; init; }
    public bool IsSuccess { get; init; }
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

        RaiseChanged(model.Id, 0, isCompleted: false, isSuccess: false, error: null, model.DisplayName);
        _ = Task.Run(() => RunDownloadAsync(download));
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
        download.Cancellation.Cancel();
    }

    private async Task RunDownloadAsync(ActiveDownload download)
    {
        Result<string> result;
        try
        {
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
                RaiseChanged(download.Model.Id, progress, isCompleted: false, isSuccess: false, error: null, download.Model.DisplayName);
            }
        });

    private void RaiseChanged(
        string modelId,
        double progress,
        bool isCompleted,
        bool isSuccess,
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
