using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

public static partial class SharedCoreBridge
{
    // -----------------------------------------------------------------------
    // Provider health (issue #781)
    //
    // hw_net::health owns "how do I probe provider X, and what does the answer
    // mean": the endpoint, the auth scheme, ElevenLabs' STT-capability POST and
    // its 400/422-is-healthy grade. Windows calls it directly; Linux's
    // ProviderMetadataHealthProbe used to keep a second, hand-written table that
    // had drifted from it. This wrapper is the only door the shared .NET probe
    // uses, so the table lives in one place.
    // -----------------------------------------------------------------------

    /// <summary>
    /// The core-built health request for <paramref name="provider"/>, materialized
    /// for the caller's own transport. The caller owns and disposes the message.
    /// Vendor endpoints only: a HW-Cloud-routed provider is not a key check.
    /// </summary>
    internal static HttpRequestMessage BuildHealthRequestMessage(HwProvider provider, string apiKey)
        => RustHttpTransport.BuildRequestMessage(
            HyperwhisperCoreMethods.BuildHealthRequest(provider, apiKey));

    /// <summary>
    /// Grade a health probe's HTTP status with the core. The core reads the status
    /// only, so the body and headers are not handed across the FFI. True means
    /// reachable and authorized; the caller grades a failure from its own status.
    /// </summary>
    internal static bool ParseHealthResponse(HwProvider provider, int status)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(status, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(status, ushort.MaxValue);
        return HyperwhisperCoreMethods.ParseHealthResponse(
            provider, new HttpResponse((ushort)status, [], [])).@healthy;
    }
}
