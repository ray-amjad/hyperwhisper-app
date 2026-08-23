using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HyperWhisper.Linux.Overlay;

public sealed class LinuxRecordingOverlayViewModel : INotifyPropertyChanged
{
    private LinuxRecordingOverlaySnapshot _snapshot = HiddenSnapshot;

    public event PropertyChangedEventHandler? PropertyChanged;
    public LinuxRecordingOverlaySnapshot Snapshot => _snapshot;
    public LinuxRecordingOverlayState State => _snapshot.State;
    public bool IsVisible => _snapshot.IsVisible;
    public string StatusText => _snapshot.StatusText;
    public string ModeText => _snapshot.ModeText;
    public string DurationText => _snapshot.DurationText;
    public double AudioLevel => _snapshot.AudioLevel;
    public string StreamingIndicatorBrush => _snapshot.StreamingConnection switch
    {
        LinuxStreamingOverlayConnectionState.Connecting => "#FFFFCC00",
        LinuxStreamingOverlayConnectionState.Reconnecting => "#FFFF9500",
        LinuxStreamingOverlayConnectionState.Error => "#FFFF3B30",
        _ => "#FF34C759",
    };
    public bool IsRecording => State == LinuxRecordingOverlayState.Recording;
    public bool IsStreaming => State == LinuxRecordingOverlayState.Streaming;
    public bool IsTranscribing => State == LinuxRecordingOverlayState.Transcribing;
    public bool IsPasted => State == LinuxRecordingOverlayState.Pasted;
    public bool IsCopied => State == LinuxRecordingOverlayState.Copied;
    public bool IsSecureField => State == LinuxRecordingOverlayState.SecureField;
    public bool IsCancelConfirmation => State == LinuxRecordingOverlayState.CancelConfirmation;
    public bool IsError => State == LinuxRecordingOverlayState.Error;
    public bool IsModeChanged => State == LinuxRecordingOverlayState.ModeChanged;
    public bool IsCancelled => State == LinuxRecordingOverlayState.Cancelled;

    internal static LinuxRecordingOverlaySnapshot HiddenSnapshot =>
        new(LinuxRecordingOverlayState.Hidden, false, string.Empty, string.Empty, "00:00");

    internal void Apply(LinuxRecordingOverlaySnapshot snapshot)
    {
        if (_snapshot == snapshot) return;
        _snapshot = snapshot;
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(AudioLevel));
        OnPropertyChanged(nameof(StreamingIndicatorBrush));
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsStreaming));
        OnPropertyChanged(nameof(IsTranscribing));
        OnPropertyChanged(nameof(IsPasted));
        OnPropertyChanged(nameof(IsCopied));
        OnPropertyChanged(nameof(IsSecureField));
        OnPropertyChanged(nameof(IsCancelConfirmation));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsModeChanged));
        OnPropertyChanged(nameof(IsCancelled));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
