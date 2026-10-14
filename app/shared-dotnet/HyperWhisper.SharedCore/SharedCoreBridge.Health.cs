using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

/// <summary>
/// The core's verdict on one provider health probe. Mirrors <c>HwProviderHealth</c>:
/// <c>Healthy</c> is reachable-and-authorized; <c>Status</c> is the raw HTTP status
/// the host uses to tell the failure causes apart.
/// </summary>
public sealed record PortableProviderHealth(bool Healthy, int? Status);

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
    /// <paramref name="baseUrl"/> only matters for the HW-Cloud-routed providers.
    /// </summary>
    internal static HttpRequestMessage BuildHealthRequestMessage(
        HwProvider provider, string apiKey, string? baseUrl = null)
        => RustHttpTransport.BuildRequestMessage(
            HyperwhisperCoreMethods.BuildHealthRequestWithBase(provider, apiKey, baseUrl));

    /// <summary>
    /// Grade a health probe's HTTP status with the core. The core reads the status
    /// only, so the body and headers are not handed across the FFI.
    /// </summary>
    internal static PortableProviderHealth ParseHealthResponse(HwProvider provider, int status)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(status, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(status, ushort.MaxValue);
        var verdict = HyperwhisperCoreMethods.ParseHealthResponse(
            provider, new HttpResponse((ushort)status, [], []));
        return new(verdict.@healthy, verdict.@status);
    }
}
