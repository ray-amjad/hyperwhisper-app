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
    public bool IsRecording => State == LinuxRecordingOverlayState.Recording;
    public bool IsTranscribing => State == LinuxRecordingOverlayState.Transcribing;
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
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsTranscribing));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsModeChanged));
        OnPropertyChanged(nameof(IsCancelled));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
