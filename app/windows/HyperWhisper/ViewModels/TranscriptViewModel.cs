using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;

namespace HyperWhisper.ViewModels;

/// <summary>
/// TRANSCRIPT VIEW MODEL
///
/// Wraps a Transcript entity with UI-specific properties and computed values.
/// Used by HistoryViewModel for display in the list and detail views.
///
/// DESIGN NOTES:
/// - Mirrors all Transcript properties as observable
/// - Adds UI state properties (IsSelected, IsRetrying, IsDeleting)
/// - Provides computed properties for formatted display
/// - Immutable source ID for stable selection tracking
/// </summary>
public partial class TranscriptViewModel : ObservableObject
{
    // =========================================================================
    // SOURCE ENTITY
    // =========================================================================

    /// <summary>The underlying Transcript entity.</summary>
    private readonly Transcript _source;

    /// <summary>Stable ID for selection tracking (doesn't change).</summary>
    public Guid Id => _source.Id;

    // =========================================================================
    // OBSERVABLE PROPERTIES FROM ENTITY
    // =========================================================================

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private string? _transcribedText;

    [ObservableProperty]
    private string? _postProcessedText;

    [ObservableProperty]
    private DateTime _date;

    [ObservableProperty]
    private double _duration;

    [ObservableProperty]
    private string? _mode;

    [ObservableProperty]
    private string? _audioFilePath;

    [ObservableProperty]
    private TranscriptStatus _status;

    [ObservableProperty]
    private string? _failedReason;

    [ObservableProperty]
    private string? _transcriptionProvider;

    [ObservableProperty]
    private string? _postProcessingProvider;

    [ObservableProperty]
    private int _retryCount;

    [ObservableProperty]
    private DateTime? _lastRetryDate;

    // =========================================================================
    // UI STATE PROPERTIES
    // =========================================================================

    /// <summary>Whether this transcript is selected in the list.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Whether a retry operation is in progress.</summary>
    [ObservableProperty]
    private bool _isRetrying;

    /// <summary>Whether a delete operation is in progress.</summary>
    [ObservableProperty]
    private bool _isDeleting;

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================

    public TranscriptViewModel(Transcript source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        CopyFromSource();
    }

    /// <summary>
    /// Updates the view model from the source entity.
    /// Called when the transcript is updated in HistoryService.
    /// </summary>
    public void Refresh()
    {
        CopyFromSource();
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(FormattedTime));
        OnPropertyChanged(nameof(FormattedDuration));
        OnPropertyChanged(nameof(FormattedDate));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(HasRawText));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(HasAudio));
        OnPropertyChanged(nameof(DisplayText));
    }

    private void CopyFromSource()
    {
        Text = _source.Text;
        TranscribedText = _source.TranscribedText;
        PostProcessedText = _source.PostProcessedText;
        Date = _source.Date;
        Duration = _source.Duration;
        Mode = _source.Mode;
        AudioFilePath = _source.AudioFilePath;
        Status = _source.Status;
        FailedReason = _source.FailedReason;
        TranscriptionProvider = _source.TranscriptionProvider;
        PostProcessingProvider = _source.PostProcessingProvider;
        RetryCount = _source.RetryCount;
        LastRetryDate = _source.LastRetryDate;
    }

    /// <summary>
    /// Gets the underlying Transcript entity.
    /// Used when saving updates back to HistoryService.
    /// </summary>
    public Transcript ToEntity()
    {
        _source.Text = Text;
        _source.TranscribedText = TranscribedText;
        _source.PostProcessedText = PostProcessedText;
        _source.Date = Date;
        _source.Duration = Duration;
        _source.Mode = Mode;
        _source.AudioFilePath = AudioFilePath;
        _source.Status = Status;
        _source.FailedReason = FailedReason;
        _source.TranscriptionProvider = TranscriptionProvider;
        _source.PostProcessingProvider = PostProcessingProvider;
        _source.RetryCount = RetryCount;
        _source.LastRetryDate = LastRetryDate;
        return _source;
    }

    // =========================================================================
    // COMPUTED PROPERTIES - DISPLAY
    // =========================================================================

    /// <summary>
    /// Truncated text preview for the list view (max 100 chars, 2 lines).
    /// </summary>
    public string PreviewText
    {
        get
        {
            if (string.IsNullOrEmpty(Text)) return "";

            // Remove newlines for preview and truncate
            var preview = Text.Replace('\n', ' ').Replace('\r', ' ');
            if (preview.Length > 100)
            {
                preview = preview[..100] + "...";
            }
            return preview;
        }
    }

    /// <summary>
    /// Time formatted for list display (e.g., "2:30 PM").
    /// </summary>
    public string FormattedTime => Date.ToLocalTime().ToString("h:mm tt");

    /// <summary>
    /// Duration formatted as "Xm Ys" (e.g., "2m 15s").
    /// </summary>
    public string FormattedDuration
    {
        get
        {
            var span = TimeSpan.FromSeconds(Duration);
            if (span.TotalMinutes >= 1)
            {
                return $"{(int)span.TotalMinutes}m {span.Seconds}s";
            }
            return $"{span.Seconds}s";
        }
    }

    /// <summary>
    /// Full date/time for detail view header.
    /// </summary>
    public string FormattedDate => Date.ToLocalTime().ToString("dddd, MMMM d, yyyy, h:mm tt");

    /// <summary>
    /// Group header for ICollectionView grouping.
    /// Returns "Today", "Yesterday", or a formatted date string.
    /// Used by the ListBox GroupStyle to display date section headers.
    /// </summary>
    public string GroupHeader
    {
        get
        {
            var localDate = Date.ToLocalTime().Date;
            var today = DateTime.Now.Date;
            var yesterday = today.AddDays(-1);

            if (localDate == today) return Loc.S("history.section.today");
            if (localDate == yesterday) return Loc.S("history.section.yesterday");
            return localDate.ToString("MMMM d, yyyy");
        }
    }

    /// <summary>
    /// Raises PropertyChanged for <see cref="GroupHeader"/>. Called by the
    /// midnight timer in HistoryViewModel so "Today" / "Yesterday" labels
    /// re-evaluate without the user reopening the app.
    /// </summary>
    public void RefreshGroupHeader() => OnPropertyChanged(nameof(GroupHeader));

    /// <summary>
    /// Text to display based on whether raw or processed is requested.
    /// </summary>
    public string DisplayText => Text;

    /// <summary>
    /// Gets the text to display when showing raw transcription.
    /// </summary>
    public string RawDisplayText => TranscribedText ?? Text;

    /// <summary>
    /// Gets the text to display when showing processed transcription.
    /// </summary>
    public string ProcessedDisplayText => PostProcessedText ?? Text;

    // =========================================================================
    // COMPUTED PROPERTIES - STATUS
    // =========================================================================

    /// <summary>Whether transcription failed.</summary>
    public bool IsFailed => Status == TranscriptStatus.Failed;

    /// <summary>Whether transcription is in progress.</summary>
    public bool IsProcessing => Status == TranscriptStatus.Processing;

    /// <summary>Whether transcription completed successfully.</summary>
    public bool IsCompleted => Status == TranscriptStatus.Completed;

    /// <summary>
    /// Whether the transcript has both raw and processed text available.
    /// Used to show/hide the raw/processed toggle button.
    /// </summary>
    public bool HasRawText =>
        !string.IsNullOrEmpty(TranscribedText) &&
        !string.IsNullOrEmpty(PostProcessedText) &&
        TranscribedText != PostProcessedText;

    /// <summary>
    /// Whether the transcript can be retried.
    /// Requires failed status and existing audio file.
    /// </summary>
    public bool CanRetry =>
        IsFailed &&
        !string.IsNullOrEmpty(AudioFilePath) &&
        File.Exists(AudioFilePath);

    /// <summary>
    /// Whether audio playback is available.
    /// Requires existing audio file path.
    /// </summary>
    public bool HasAudio =>
        !string.IsNullOrEmpty(AudioFilePath) &&
        File.Exists(AudioFilePath);

    /// <summary>
    /// Whether retry info should be displayed (retry count > 0).
    /// </summary>
    public bool HasRetryInfo => RetryCount > 0;

    /// <summary>
    /// Formatted retry info text (e.g., "1 attempt" or "3 attempts").
    /// </summary>
    public string RetryInfoText => RetryCount == 1
        ? Loc.S("history.retry.attemptSingular")
        : Loc.S("history.retry.attemptPlural", RetryCount);
}
