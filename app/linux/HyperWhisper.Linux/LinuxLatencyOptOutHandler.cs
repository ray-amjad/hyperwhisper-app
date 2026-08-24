namespace HyperWhisper.Linux;

/// <summary>Adds the backend opt-out signal only to HyperWhisper Cloud transcription requests.</summary>
internal sealed class LinuxLatencyOptOutHandler(
    Func<bool> shareAnonymousSpeedData,
    HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    internal const string HeaderName = "X-Latency-Opt-Out";
    private readonly Func<bool> _share = shareAnonymousSpeedData
        ?? throw new ArgumentNullException(nameof(shareAnonymousSpeedData));

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!_share()
            && string.Equals(request.RequestUri?.Host, "transcribe-prod-v2.hyperwhisper.com", StringComparison.OrdinalIgnoreCase))
            request.Headers.TryAddWithoutValidation(HeaderName, "1");
        return base.SendAsync(request, cancellationToken);
    }
}
