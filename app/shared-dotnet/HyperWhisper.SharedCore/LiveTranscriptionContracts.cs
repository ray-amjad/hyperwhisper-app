using System.Net.WebSockets;

namespace HyperWhisper.SharedCore;

public enum LiveTranscriptionProvider
{
    Deepgram,
    ElevenLabs,
    OpenAi,
    Grok,
    GeminiTranscribe,
    HyperWhisperCloud,
    ParakeetLocal,
    NemotronLocal,
}

/// <param name="CloudTier">
/// Which vendor HyperWhisper Cloud's live route should use, as a
/// <c>cloud-stt-catalog.json</c> entry id (<c>deepgramNova3</c>,
/// <c>geminiTranscribe</c>, …). Meaningful only when
/// <paramref name="Provider"/> is <see cref="LiveTranscriptionProvider.HyperWhisperCloud"/>;
/// every other provider ignores it. Null means the catalog default,
/// <c>deepgramNova3</c>, which reproduces the pre-tier-picker behaviour exactly.
/// </param>
public sealed record LiveTranscriptionConfig(
    LiveTranscriptionProvider Provider,
    string? ApiKey = null,
    string? LicenseKey = null,
    string? DeviceId = null,
    string? Language = null,
    IReadOnlyList<string>? Vocabulary = null,
    string? Model = null,
    bool FastFormatting = false,
    string? CloudTier = null)
{
    public override string ToString() =>
        $"LiveTranscriptionConfig {{ Provider = {Provider}, HasApiKey = {!string.IsNullOrWhiteSpace(ApiKey)}, HasLicenseKey = {!string.IsNullOrWhiteSpace(LicenseKey)}, HasDeviceId = {!string.IsNullOrWhiteSpace(DeviceId)}, Language = {Language ?? "auto"}, VocabularyTerms = {Vocabulary?.Count ?? 0}, Model = {Model ?? "default"}, FastFormatting = {FastFormatting}, CloudTier = {CloudTier ?? "default"} }}";
}

public enum LiveTranscriptionFailureCode
{
    Unauthorized,
    RateLimited,
    QuotaExceeded,
    NoSpeech,
    ProviderUnavailable,
    InvalidRequest,
    Protocol,
    BufferLimit,
    Cancelled,
    Timeout,
    Network,
    Unknown,
}

/// <param name="IsTerminal">
/// Whether reconnecting with the same credentials cannot rescue the session —
/// an exhausted balance, a revoked key, a refused upgrade. Set from the shared
/// terminal-error policy (issue #281); <see cref="Code"/> alone cannot say it,
/// because a provider's error frame arrives as
/// <see cref="LiveTranscriptionFailureCode.ProviderUnavailable"/> whether the
/// account is out of credit or the service is merely having a bad minute, and
/// only the first of those must stop the retry.
/// </param>
public sealed record LiveTranscriptionFailure(
    LiveTranscriptionFailureCode Code,
    string Message,
    LiveTranscriptionProvider Provider,
    int? CloseStatus = null,
    bool IsTerminal = false);

public sealed record LiveTranscriptionResult(
    string? Transcript,
    LiveTranscriptionFailure? Failure,
    int AudioChunksSent,
    int MessagesReceived)
{
    public bool IsSuccess => Failure is null && Transcript is not null;
}

public sealed record LiveTranscriptUpdate(string Text, bool IsFinal);

public interface ILiveTranscriptSink
{
    void OnTranscript(LiveTranscriptUpdate update);
}

public sealed record LiveTranscriptionDiagnostic(
    LiveTranscriptionProvider Provider,
    string State,
    int AudioChunksSent,
    int MessagesReceived,
    int? CloseStatus = null);

public interface ILiveTranscriptionDiagnostics
{
    void OnDiagnostic(LiveTranscriptionDiagnostic diagnostic);
}

public sealed record StreamingWebSocketConnectOptions(
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<string> SubProtocols)
{
    public override string ToString() =>
        $"StreamingWebSocketConnectOptions {{ Endpoint = {Uri.GetLeftPart(UriPartial.Path)}, HeaderNames = [{string.Join(",", Headers.Keys)}], SubProtocolCount = {SubProtocols.Count} }}";
}

public sealed record StreamingWebSocketFrame(
    byte[] Data,
    WebSocketMessageType MessageType,
    bool EndOfMessage = true,
    WebSocketCloseStatus? CloseStatus = null);

public interface IStreamingWebSocket : IAsyncDisposable
{
    Task ConnectAsync(StreamingWebSocketConnectOptions options, CancellationToken cancellationToken);
    Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType messageType, CancellationToken cancellationToken);
    Task<StreamingWebSocketFrame> ReceiveAsync(CancellationToken cancellationToken);
    Task CloseAsync(WebSocketCloseStatus status, CancellationToken cancellationToken);
}

public interface IStreamingWebSocketFactory
{
    IStreamingWebSocket Create();
}

public sealed class ClientStreamingWebSocketFactory : IStreamingWebSocketFactory
{
    public IStreamingWebSocket Create() => new ClientStreamingWebSocket();
}
