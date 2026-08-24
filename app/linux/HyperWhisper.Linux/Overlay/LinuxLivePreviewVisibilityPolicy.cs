using HyperWhisper.LiveStreaming;

namespace HyperWhisper.Linux.Overlay;

internal static class LinuxLivePreviewVisibilityPolicy
{
    public static bool ShouldShow(EphemeralLiveTranscriptSnapshot snapshot) =>
        snapshot.IsActive && !string.IsNullOrWhiteSpace(snapshot.DisplayText);
}
