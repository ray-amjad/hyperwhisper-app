using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services.LocalApi;

/// <summary>
/// REQUEST-SIZE LIMITS FOR THE LOCAL API
///
/// The numbers come from <c>hw-localapi</c> (<c>crates/hw-localapi/src/limits.rs</c>),
/// the same place macOS and the portable .NET head read them from since #405.
/// Since issue #356 item 6 this head hosts the shared server
/// (<c>PortableLocalApi</c>), which enforces them: the Kestrel body cap, the
/// multipart cap, the encoded and decoded <c>audio_base64</c> caps and the
/// <c>file</c> cap. This head only hands it the two shared numbers, so they
/// cannot drift from the other heads.
/// </summary>
internal static class LocalApiLimits
{
    /// <summary>
    /// The largest request body this head accepts: 52,428,800 bytes (50 MiB).
    /// </summary>
    public static long MaxRequestBytes { get; } =
        checked((long)HyperwhisperCoreMethods.LocalApiMaxRequestBytes());

    /// <summary>
    /// The largest single piece of audio this head buffers: 50,331,648 bytes
    /// (48 MiB). Applies to a multipart <c>audio</c> part, the decoded
    /// <c>audio_base64</c> bytes and a <c>file</c> path.
    /// </summary>
    public static long MaxUploadBytes { get; } =
        checked((long)HyperwhisperCoreMethods.LocalApiMaxUploadBytes());
}
