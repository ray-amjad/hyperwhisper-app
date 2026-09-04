using System.Net;
using System.Buffers.Binary;
using System.Text;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

/// <summary>
/// Portable I/O shell over the Rust-owned cloud STT request/response contract.
/// Provider URLs, headers, multipart fields, prompts and response parsing stay
/// in the generated UniFFI binding; this class owns HTTP, cancellation and flow.
/// </summary>
public sealed class CloudTranscriptionService : IDisposable
{
    private const int MaxTransportAttempts = 4;
    private const int MaxPollAttempts = 500;
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(300);
    // Mirrors the Windows file-import provider limits. This final transport
    // gate stats the owned artifact immediately before request construction so
    // preflight/copy races cannot upload an oversized file.
    private static readonly IReadOnlyDictionary<CloudTranscriptionProvider, long> MaximumFileBytes =
        new Dictionary<CloudTranscriptionProvider, long>
        {
            [CloudTranscriptionProvider.OpenAi] = 25L * 1024 * 1024,
            [CloudTranscriptionProvider.Groq] = 25L * 1024 * 1024,
            [CloudTranscriptionProvider.Deepgram] = 2L * 1024 * 1024 * 1024,
            [CloudTranscriptionProvider.AssemblyAi] = 5L * 1024 * 1024 * 1024,
            [CloudTranscriptionProvider.ElevenLabs] = 3L * 1024 * 1024 * 1024,
            [CloudTranscriptionProvider.Mistral] = 100L * 1024 * 1024,
            [CloudTranscriptionProvider.Soniox] = 1L * 1024 * 1024 * 1024,
            [CloudTranscriptionProvider.Gemini] = 2L * 1024 * 1024 * 1024,
            // The interactions endpoint takes the audio INLINE as base64, which
            // inflates ~33%, so the raw-file cap is the request ceiling scaled
            // down: 14 MB raw ≈ 19 MB on the wire. There is no Files-API
            // overflow path for this model.
            [CloudTranscriptionProvider.GeminiTranscribe] = 14L * 1024 * 1024,
            [CloudTranscriptionProvider.Grok] = 500L * 1024 * 1024,
            [CloudTranscriptionProvider.AzureMai] = 300L * 1024 * 1024,
            [CloudTranscriptionProvider.GoogleChirp] = 9_500_000L,
            [CloudTranscriptionProvider.HyperWhisperCloud] = 2L * 1024 * 1024 * 1024,
            [CloudTranscriptionProvider.Meta] = 32L * 1024 * 1024,
        };

    private static readonly CloudProviderDescriptor[] ProviderCatalog =
    [
        new(CloudTranscriptionProvider.Groq, "groqWhisper", "Groq Whisper", false, true),
        new(CloudTranscriptionProvider.Deepgram, "deepgramNova3", "Deepgram Nova 3", false, true),
        new(CloudTranscriptionProvider.Grok, "grokStt", "Grok STT", false, true),
        new(CloudTranscriptionProvider.AzureMai, "azureMaiTranscribe", "Microsoft MAI-Transcribe", false, true),
        new(CloudTranscriptionProvider.GoogleChirp, "googleChirp3", "Google Chirp 3", false, true),
        new(CloudTranscriptionProvider.ElevenLabs, "elevenLabsScribeV2", "ElevenLabs Scribe v2", false, true),
        new(CloudTranscriptionProvider.OpenAi, "openaiWhisper", "OpenAI Whisper", false, true),
        new(CloudTranscriptionProvider.AssemblyAi, "assemblyAI", "AssemblyAI", true, true),
        new(CloudTranscriptionProvider.Mistral, "mistralVoxtral", "Mistral Voxtral", false, true),
        new(CloudTranscriptionProvider.Soniox, "soniox", "Soniox", true, true),
        new(CloudTranscriptionProvider.Gemini, "gemini", "Google Gemini", true, true),
        new(CloudTranscriptionProvider.GeminiTranscribe, "geminiTranscribe", "Gemini 3.5 Transcribe", false, true),
        new(CloudTranscriptionProvider.HyperWhisperCloud, "hyperwhisperCloud", "HyperWhisper Cloud", false, true),
        new(CloudTranscriptionProvider.Meta, "metaMuse", "Meta Muse", false, true),
    ];

    private readonly HttpClient _client;
    private readonly ICloudCredentialSource _credentials;
    private readonly ICloudTranscriptionDelay _delay;
    private readonly ICloudTranscriptionObserver? _observer;
    private readonly Func<bool> _shareAnonymousSpeedData;
    private readonly ulong _retryBudgetMs;
    private bool _disposed;

    /// <param name="shareAnonymousSpeedData">
    /// Reads the user's "share anonymous speed data" setting. <c>true</c> means
    /// SHARE, which means the request carries no header; <c>false</c> is the
    /// opt-out and makes the core send <c>X-Latency-Opt-Out: 1</c>. Invoked once
    /// per <see cref="TranscribeAsync"/> call, so a settings change applies to
    /// the next transcription without rebuilding this service.
    ///
    /// Deliberately REQUIRED, with no default, mirroring the core's required
    /// <c>TranscribeParams.share_anonymous_speed_data</c>: a new host that
    /// forgets the user's privacy choice must fail to compile rather than
    /// silently default to sharing.
    /// </param>
    /// <param name="retryBudgetMs">
    /// Total BACKOFF budget, in milliseconds, for each retry sequence (issue
    /// #379) — the sleeping only, not the requests' own duration.
    /// <c>null</c> takes the core's interactive default
    /// (<c>RetryDefaultBudgetMs()</c> == 30s); <c>0</c> means unbounded, i.e. the
    /// pre-#379 behaviour of 8 attempts and ~127s of sleep. Injectable so a batch
    /// host can be more patient than a dictation one, and so tests can pin a
    /// small budget without waiting out a real backoff series. This shared batch
    /// driver does not add jitter, so the admitted core delay is the actual sleep.
    /// </param>
    public CloudTranscriptionService(
        HttpMessageHandler handler,
        ICloudCredentialSource credentials,
        Func<bool> shareAnonymousSpeedData,
        ICloudTranscriptionDelay? delay = null,
        ICloudTranscriptionObserver? observer = null,
        ulong? retryBudgetMs = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(shareAnonymousSpeedData);
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _credentials = credentials;
        _delay = delay ?? new SystemCloudTranscriptionDelay();
        _observer = observer;
        _shareAnonymousSpeedData = shareAnonymousSpeedData;
        _retryBudgetMs = retryBudgetMs ?? HyperwhisperCoreMethods.RetryDefaultBudgetMs();
    }

    public static IReadOnlyList<CloudProviderDescriptor> Providers => ProviderCatalog;

    public async Task<CloudTranscriptionResult> TranscribeAsync(
        CloudTranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        var state = new ExecutionState();
        try
        {
            Validate(request);
            var credential = await _credentials
                .GetCredentialAsync(request.Provider, cancellationToken)
                .ConfigureAwait(false);
            var coreParams = CreateParams(request, credential);
            var transcript = request.Provider switch
            {
                CloudTranscriptionProvider.AssemblyAi =>
                    await TranscribeAssemblyAiAsync(coreParams, request.Provider, state, cancellationToken).ConfigureAwait(false),
                CloudTranscriptionProvider.Soniox =>
                    await TranscribeSonioxAsync(coreParams, request.Provider, state, cancellationToken).ConfigureAwait(false),
                CloudTranscriptionProvider.Gemini =>
                    await TranscribeGeminiAsync(coreParams, request.Provider, state, cancellationToken).ConfigureAwait(false),
                _ => await TranscribeSingleShotAsync(coreParams, request.Provider, state, cancellationToken).ConfigureAwait(false),
            };
            return CloudTranscriptionResult.Success(ToPublic(transcript), state.Attempts);
        }
        catch (OperationCanceledException)
        {
            return CloudTranscriptionResult.Failed(
                new CloudTranscriptionFailure(
                    CloudTranscriptionErrorCode.Cancelled,
                    "Transcription was cancelled.",
                    request.Provider),
                state.Attempts);
        }
        catch (CloudFailureException exception)
        {
            return CloudTranscriptionResult.Failed(exception.Failure, state.Attempts);
        }
        catch (HwTranscriptionException exception)
        {
            return CloudTranscriptionResult.Failed(MapFailure(exception, request.Provider), state.Attempts);
        }
        catch (HttpRequestException)
        {
            return CloudTranscriptionResult.Failed(
                new CloudTranscriptionFailure(
                    CloudTranscriptionErrorCode.Network,
                    "The transcription service could not be reached.",
                    request.Provider),
                state.Attempts);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _client.Dispose();
    }

    private static void Validate(CloudTranscriptionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AudioPath))
        {
            throw new ArgumentException("Audio path is required.", nameof(request));
        }
        if (!File.Exists(request.AudioPath))
        {
            throw new CloudFailureException(new CloudTranscriptionFailure(
                CloudTranscriptionErrorCode.InvalidRequest,
                "The audio file does not exist.",
                request.Provider));
        }
        long length;
        try
        {
            length = new FileInfo(request.AudioPath).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CloudFailureException(new CloudTranscriptionFailure(
                CloudTranscriptionErrorCode.InvalidRequest,
                "The audio file could not be read.",
                request.Provider));
        }
        if (length <= 0)
        {
            throw new CloudFailureException(new CloudTranscriptionFailure(
                CloudTranscriptionErrorCode.InvalidRequest,
                "The audio file is empty.",
                request.Provider));
        }
        var museTarget = request.Provider == CloudTranscriptionProvider.Meta
            || request.Provider == CloudTranscriptionProvider.HyperWhisperCloud
                && string.Equals(request.RoutedProvider, "meta", StringComparison.OrdinalIgnoreCase);
        var maximumBytes = museTarget
            ? 32L * 1024 * 1024
            : MaximumFileBytes.GetValueOrDefault(request.Provider, long.MaxValue);
        if (length > maximumBytes)
        {
            throw new CloudFailureException(new CloudTranscriptionFailure(
                CloudTranscriptionErrorCode.FileTooLarge,
                "The audio file exceeds the selected provider's upload limit.",
                request.Provider));
        }
        if (museTarget) ValidateMuseWave(request);
    }

    /// <summary>
    /// Re-opens the final artifact immediately before the Rust request builder.
    /// Import preflight runs earlier, but this transport gate closes the race
    /// where an owned normalized file changes before upload. It reads RIFF
    /// metadata only; audio data remains streamed from disk by the HTTP layer.
    /// </summary>
    private static void ValidateMuseWave(CloudTranscriptionRequest request)
    {
        try
        {
            using var stream = new FileStream(request.AudioPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.SequentialScan);
            Span<byte> header = stackalloc byte[12];
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("RIFF"u8)
                || !header[8..].SequenceEqual("WAVE"u8))
                throw InvalidMuseWave(request.Provider);

            ushort? encoding = null, channels = null, blockAlign = null, bits = null;
            uint? sampleRate = null, byteRate = null;
            uint? dataBytes = null;
            Span<byte> chunk = stackalloc byte[8];
            Span<byte> format = stackalloc byte[16];
            for (var count = 0; count < 256 && stream.Position + 8 <= stream.Length; count++)
            {
                stream.ReadExactly(chunk);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                var payloadStart = stream.Position;
                var payloadEnd = checked(payloadStart + size);
                if (payloadEnd > stream.Length) throw InvalidMuseWave(request.Provider);
                if (chunk[..4].SequenceEqual("fmt "u8))
                {
                    if (size < 16) throw InvalidMuseWave(request.Provider);
                    stream.ReadExactly(format);
                    encoding = BinaryPrimitives.ReadUInt16LittleEndian(format);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
                    sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..]);
                    byteRate = BinaryPrimitives.ReadUInt32LittleEndian(format[8..]);
                    blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
                }
                else if (chunk[..4].SequenceEqual("data"u8))
                {
                    dataBytes = size;
                }
                stream.Position = payloadEnd + (size & 1);
                if (stream.Position > stream.Length) throw InvalidMuseWave(request.Provider);
                if (encoding.HasValue && dataBytes.HasValue) break;
            }

            if (encoding != 1 || channels != 1 || bits != 16 || blockAlign != 2
                || sampleRate is not (16_000 or 24_000) || byteRate != sampleRate * 2
                || dataBytes is not > 0)
                throw InvalidMuseWave(request.Provider);
            var durationSeconds = dataBytes.Value / (sampleRate.Value * 2d);
            if (!double.IsFinite(durationSeconds) || durationSeconds > 600d)
                throw new CloudFailureException(new CloudTranscriptionFailure(
                    CloudTranscriptionErrorCode.InvalidRequest,
                    "The audio exceeds Meta Muse's duration limit.", request.Provider));
        }
        catch (CloudFailureException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or OverflowException or ArgumentOutOfRangeException)
        {
            throw InvalidMuseWave(request.Provider);
        }
    }

    private static CloudFailureException InvalidMuseWave(CloudTranscriptionProvider provider) =>
        new(new CloudTranscriptionFailure(
            CloudTranscriptionErrorCode.InvalidRequest,
            "Meta Muse requires a mono PCM16 WAV at 16 kHz or 24 kHz.", provider));

    /// <summary>
    /// Build the core <see cref="TranscribeParams"/> for one request.
    ///
    /// The privacy flag is read here, once per request, so a settings change
    /// reaches the next transcription — the same freshness the transport-level
    /// handler this replaced used to give. <c>true</c> means SHARE and sends
    /// nothing; the core sends <c>X-Latency-Opt-Out</c> only when it is
    /// <c>false</c>, and only from the HyperWhisper Cloud / routed builders — so
    /// a direct-vendor request cannot carry it regardless of this value, at any
    /// base URL.
    /// </summary>
    private TranscribeParams CreateParams(
        CloudTranscriptionRequest request,
        CloudCredential? credential)
    {
        var usesLicense = request.Provider is CloudTranscriptionProvider.AzureMai
            or CloudTranscriptionProvider.GoogleChirp
            or CloudTranscriptionProvider.HyperWhisperCloud;
        var apiKey = credential?.ApiKey?.Trim() ?? string.Empty;
        var licenseKey = credential?.LicenseKey?.Trim();
        if (usesLicense ? string.IsNullOrWhiteSpace(licenseKey) : string.IsNullOrWhiteSpace(apiKey))
        {
            throw new CloudFailureException(new CloudTranscriptionFailure(
                CloudTranscriptionErrorCode.Unauthorized,
                usesLicense ? "A HyperWhisper account key is required." : "A provider API key is required.",
                request.Provider));
        }

        return new TranscribeParams(
            @apiKey: apiKey,
            @model: request.Model ?? string.Empty,
            @language: NormalizeOptional(request.Language),
            @vocabulary: request.Vocabulary?.Where(value => !string.IsNullOrWhiteSpace(value)).ToList() ?? [],
            @prompt: NormalizeOptional(request.Prompt),
            @temperature: null,
            @audioPath: Path.GetFullPath(request.AudioPath),
            @audioMime: NormalizeOptional(request.AudioMime) ?? MimeType(request.AudioPath),
            @baseUrl: NormalizeOptional(request.BaseUrl),
            @licenseKey: licenseKey,
            @deviceId: NormalizeOptional(credential?.DeviceId),
            @routedProvider: NormalizeOptional(request.RoutedProvider),
            @routedModel: NormalizeOptional(request.RoutedModel),
            @routedDomain: NormalizeOptional(request.RoutedDomain),
            @shareAnonymousSpeedData: _shareAnonymousSpeedData());
    }

    private async Task<HwTranscript> TranscribeSingleShotAsync(
        TranscribeParams parameters,
        CloudTranscriptionProvider provider,
        ExecutionState state,
        CancellationToken cancellationToken)
    {
        var response = await SendWithRetryAsync(
            () => BuildSingleRequest(provider, parameters),
            response => ParseSingleResponse(provider, response),
            provider,
            "transcribe",
            state,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return ParseSingleResponse(provider, response);
    }

    private async Task<HwTranscript> TranscribeAssemblyAiAsync(
        TranscribeParams parameters,
        CloudTranscriptionProvider provider,
        ExecutionState state,
        CancellationToken cancellationToken)
    {
        var upload = await SendWithRetryAsync(
            () => HyperwhisperCoreMethods.AssemblyaiBuildUploadRequest(parameters),
            response => HyperwhisperCoreMethods.AssemblyaiParseUploadResponse(response),
            provider, "upload", state, cancellationToken).ConfigureAwait(false);
        var audioUrl = HyperwhisperCoreMethods.AssemblyaiParseUploadResponse(upload);
        var create = await SendWithRetryAsync(
            () => HyperwhisperCoreMethods.AssemblyaiBuildCreateRequest(parameters, audioUrl),
            response => HyperwhisperCoreMethods.AssemblyaiParseCreateResponse(response),
            provider, "create", state, cancellationToken).ConfigureAwait(false);
        var id = HyperwhisperCoreMethods.AssemblyaiParseCreateResponse(create);
        for (var poll = 0; poll < MaxPollAttempts; poll++)
        {
            var response = await SendWithRetryAsync(
                () => HyperwhisperCoreMethods.AssemblyaiBuildPollRequest(parameters, id),
                value => HyperwhisperCoreMethods.AssemblyaiParsePollResponse(value),
                provider, "poll", state, cancellationToken).ConfigureAwait(false);
            if (HyperwhisperCoreMethods.AssemblyaiParsePollResponse(response) is AssemblyaiPollOutcome.Done done)
            {
                return done.@transcript;
            }
            await _delay.DelayAsync(PollDelay, cancellationToken).ConfigureAwait(false);
        }
        throw PollTimeout(provider);
    }

    private async Task<HwTranscript> TranscribeSonioxAsync(
        TranscribeParams parameters,
        CloudTranscriptionProvider provider,
        ExecutionState state,
        CancellationToken cancellationToken)
    {
        string? fileId = null;
        string? transcriptionId = null;
        try
        {
            var upload = await SendWithRetryAsync(
                () => HyperwhisperCoreMethods.SonioxBuildUploadRequest(parameters),
                response => HyperwhisperCoreMethods.SonioxParseUploadResponse(response),
                provider, "upload", state, cancellationToken).ConfigureAwait(false);
            fileId = HyperwhisperCoreMethods.SonioxParseUploadResponse(upload);
            var create = await SendWithRetryAsync(
                () => HyperwhisperCoreMethods.SonioxBuildCreateRequest(parameters, fileId),
                response => HyperwhisperCoreMethods.SonioxParseCreateResponse(response),
                provider, "create", state, cancellationToken).ConfigureAwait(false);
            transcriptionId = HyperwhisperCoreMethods.SonioxParseCreateResponse(create);
            for (var poll = 0; poll < MaxPollAttempts; poll++)
            {
                var status = await SendWithRetryAsync(
                    () => HyperwhisperCoreMethods.SonioxBuildStatusRequest(parameters, transcriptionId),
                    response => HyperwhisperCoreMethods.SonioxParseStatusResponse(response),
                    provider, "poll", state, cancellationToken).ConfigureAwait(false);
                if (HyperwhisperCoreMethods.SonioxParseStatusResponse(status) == SonioxPollStatus.Completed)
                {
                    var transcript = await SendWithRetryAsync(
                        () => HyperwhisperCoreMethods.SonioxBuildTranscriptRequest(parameters, transcriptionId),
                        response => HyperwhisperCoreMethods.SonioxParseTranscriptResponse(response),
                        provider, "fetch", state, cancellationToken).ConfigureAwait(false);
                    return HyperwhisperCoreMethods.SonioxParseTranscriptResponse(transcript);
                }
                await _delay.DelayAsync(PollDelay, cancellationToken).ConfigureAwait(false);
            }
            throw PollTimeout(provider);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(transcriptionId))
            {
                await BestEffortAsync(
                    () => HyperwhisperCoreMethods.SonioxBuildDeleteTranscriptionRequest(parameters, transcriptionId),
                    cancellationToken).ConfigureAwait(false);
            }
            if (!string.IsNullOrWhiteSpace(fileId))
            {
                await BestEffortAsync(
                    () => HyperwhisperCoreMethods.SonioxBuildDeleteFileRequest(parameters, fileId),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<HwTranscript> TranscribeGeminiAsync(
        TranscribeParams parameters,
        CloudTranscriptionProvider provider,
        ExecutionState state,
        CancellationToken cancellationToken)
    {
        GeminiFile? uploaded = null;
        try
        {
            var fileLength = new FileInfo(parameters.@audioPath).Length;
            var start = await SendWithRetryAsync(
                () =>
                {
                    var request = HyperwhisperCoreMethods.GeminiBuildUploadStartRequest(parameters);
                    request.@headers.Add(new Header("X-Goog-Upload-Header-Content-Length", fileLength.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    return request;
                },
                response => HyperwhisperCoreMethods.GeminiParseUploadStartResponse(response),
                provider, "upload-start", state, cancellationToken).ConfigureAwait(false);
            var uploadUrl = HyperwhisperCoreMethods.GeminiParseUploadStartResponse(start);
            var upload = await SendWithRetryAsync(
                () => HyperwhisperCoreMethods.GeminiBuildUploadBytesRequest(parameters, uploadUrl),
                response => HyperwhisperCoreMethods.GeminiParseUploadBytesResponse(response),
                provider, "upload", state, cancellationToken).ConfigureAwait(false);
            uploaded = HyperwhisperCoreMethods.GeminiParseUploadBytesResponse(upload);
            var active = uploaded;
            for (var poll = 0; !string.Equals(active.@state, "ACTIVE", StringComparison.OrdinalIgnoreCase); poll++)
            {
                if (poll >= MaxPollAttempts || string.IsNullOrWhiteSpace(active.@name))
                {
                    throw PollTimeout(provider);
                }
                await _delay.DelayAsync(PollDelay, cancellationToken).ConfigureAwait(false);
                var response = await SendWithRetryAsync(
                    () => HyperwhisperCoreMethods.GeminiBuildPollRequest(parameters, active.@name!),
                    value => HyperwhisperCoreMethods.GeminiParsePollResponse(value),
                    provider, "poll", state, cancellationToken).ConfigureAwait(false);
                if (HyperwhisperCoreMethods.GeminiParsePollResponse(response) is GeminiFilePollOutcome.Active done)
                {
                    active = done.@file;
                }
            }
            var generate = await SendWithRetryAsync(
                () => HyperwhisperCoreMethods.GeminiBuildGenerateRequest(parameters, active),
                response => HyperwhisperCoreMethods.GeminiParseGenerateResponse(response),
                provider, "generate", state, cancellationToken).ConfigureAwait(false);
            return HyperwhisperCoreMethods.GeminiParseGenerateResponse(generate);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(uploaded?.@name))
            {
                await BestEffortAsync(
                    () => HyperwhisperCoreMethods.GeminiBuildDeleteRequest(parameters, uploaded.@name!),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Drive one request stage through the core-owned retry policy.
    ///
    /// Backoff budget (issue #379): 8 attempts of raw exponential backoff is
    /// ~127s of sleep, so a hard-down provider used to take ~150s to fail. The
    /// core owns the rule (<c>NextRetryWithinBudget</c>); this shell just carries
    /// the running total of the delays the core handed it (<c>sleptMs</c>) back
    /// into the next decision. The budget is per STAGE, matching the per-call
    /// semantics of the macOS/Windows <c>RustRetry</c> drivers — a multi-step
    /// provider's upload/poll/fetch legs each get their own.
    ///
    /// <c>sleptMs</c> is BACKOFF ONLY. The time a failed request itself took is
    /// deliberately NOT charged to it: a large upload (Linux routes file imports
    /// through here via <c>LinuxModeAwareTranscriptionFactory</c>) would
    /// otherwise blow a 30s budget before its first error even arrived and get
    /// zero retries. Counting only the returned delays also keeps the budget
    /// deterministic under an injected <see cref="ICloudTranscriptionDelay"/>,
    /// so a test can pin it without sleeping for real.
    /// </summary>
    private async Task<HttpResponse> SendWithRetryAsync<T>(
        Func<HttpRequest> build,
        Func<HttpResponse, T> parse,
        CloudTranscriptionProvider provider,
        string stage,
        ExecutionState state,
        CancellationToken cancellationToken)
    {
        uint attempt = 0;
        var transportFailures = 0;
        // Running total of the backoff this sequence actually sleeps. This batch
        // driver adds no platform jitter, so it equals the core's returned delay.
        // Accumulated across the whole loop and never reset, so the budget bounds
        // the sequence rather than any single sleep.
        var sleptMs = 0UL;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;
            state.Attempts++;
            try
            {
                var response = await RustHttpTransport.ExecuteAsync(build(), _client, cancellationToken).ConfigureAwait(false);
                Observe(new CloudTranscriptionEvent(provider, checked((int)attempt), response.@status, stage));
                if (response.@status is >= 200 and <= 299)
                {
                    return response;
                }
                var retryAfter = RetryAfter(response);
                var decision = HyperwhisperCoreMethods.NextRetryWithinBudget(
                    attempt,
                    response.@status,
                    Encoding.UTF8.GetString(response.@body),
                    retryAfter.HasValue ? (ulong)Math.Max(0, retryAfter.Value) : null,
                    sleptMs,
                    _retryBudgetMs);
                if (decision is RetryDecision.Retry retry)
                {
                    sleptMs += retry.@delayMs;
                    await _delay.DelayAsync(TimeSpan.FromMilliseconds(retry.@delayMs), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    _ = parse(response);
                }
                catch (HwTranscriptionException exception)
                {
                    throw new CloudFailureException(MapFailure(exception, provider, response.@status, retryAfter));
                }
                throw new CloudFailureException(new CloudTranscriptionFailure(
                    CloudTranscriptionErrorCode.Unknown,
                    "The provider returned an unsuccessful response.",
                    provider,
                    response.@status));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException) when (++transportFailures < MaxTransportAttempts)
            {
                Observe(new CloudTranscriptionEvent(provider, checked((int)attempt), null, stage));
                var decision = HyperwhisperCoreMethods.NextRetryWithinBudget(
                    attempt,
                    503,
                    string.Empty,
                    null,
                    sleptMs,
                    _retryBudgetMs);
                if (decision is not RetryDecision.Retry retry)
                {
                    throw;
                }
                sleptMs += retry.@delayMs;
                await _delay.DelayAsync(TimeSpan.FromMilliseconds(retry.@delayMs), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task BestEffortAsync(Func<HttpRequest> build, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        try
        {
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cleanup.CancelAfter(TimeSpan.FromSeconds(5));
            await RustHttpTransport.ExecuteAsync(build(), _client, cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cleanup is deliberately non-fatal and never logged: request URLs can
            // carry credentials for some providers.
        }
    }

    private void Observe(CloudTranscriptionEvent value)
    {
        try
        {
            _observer?.OnEvent(value);
        }
        catch (Exception)
        {
            // Diagnostics must never affect transcription.
        }
    }

    private static HttpRequest BuildSingleRequest(CloudTranscriptionProvider provider, TranscribeParams parameters) => provider switch
    {
        CloudTranscriptionProvider.OpenAi => HyperwhisperCoreMethods.OpenaiBuildTranscribeRequest(parameters),
        CloudTranscriptionProvider.Groq => HyperwhisperCoreMethods.GroqBuildTranscribeRequest(parameters),
        CloudTranscriptionProvider.ElevenLabs => HyperwhisperCoreMethods.ElevenlabsBuildTranscribeRequest(parameters),
        CloudTranscriptionProvider.Mistral => HyperwhisperCoreMethods.MistralBuildTranscribeRequest(parameters),
        CloudTranscriptionProvider.Grok => HyperwhisperCoreMethods.GrokBuildTranscribeRequest(parameters),
        CloudTranscriptionProvider.Deepgram => HyperwhisperCoreMethods.DeepgramBuildTranscribeRequest(parameters),
        CloudTranscriptionProvider.AzureMai => HyperwhisperCoreMethods.AzureMaiBuildTranscribeRequest(parameters),
        CloudTranscriptionProvider.GoogleChirp => HyperwhisperCoreMethods.GoogleChirpBuildTranscribeRequest(parameters),
        // Single-shot despite Gemini being multi-step: the interactions endpoint
        // takes the audio inline, so there is no upload/poll dance.
        CloudTranscriptionProvider.GeminiTranscribe => HyperwhisperCoreMethods.GeminiTranscribeBuildTranscribeRequest(parameters),
        CloudTranscriptionProvider.HyperWhisperCloud => HyperwhisperCoreMethods.HyperwhisperCloudBuildTranscribeRequest(parameters),
        CloudTranscriptionProvider.Meta => HyperwhisperCoreMethods.MetaBuildTranscribeRequest(parameters),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private static HwTranscript ParseSingleResponse(CloudTranscriptionProvider provider, HttpResponse response) => provider switch
    {
        CloudTranscriptionProvider.OpenAi => HyperwhisperCoreMethods.OpenaiParseTranscribeResponse(response),
        CloudTranscriptionProvider.Groq => HyperwhisperCoreMethods.GroqParseTranscribeResponse(response),
        CloudTranscriptionProvider.ElevenLabs => HyperwhisperCoreMethods.ElevenlabsParseTranscribeResponse(response),
        CloudTranscriptionProvider.Mistral => HyperwhisperCoreMethods.MistralParseTranscribeResponse(response),
        CloudTranscriptionProvider.Grok => HyperwhisperCoreMethods.GrokParseTranscribeResponse(response),
        CloudTranscriptionProvider.Deepgram => HyperwhisperCoreMethods.DeepgramParseTranscribeResponse(response),
        CloudTranscriptionProvider.AzureMai => HyperwhisperCoreMethods.AzureMaiParseTranscribeResponse(response),
        CloudTranscriptionProvider.GoogleChirp => HyperwhisperCoreMethods.GoogleChirpParseTranscribeResponse(response),
        CloudTranscriptionProvider.GeminiTranscribe => HyperwhisperCoreMethods.GeminiTranscribeParseTranscribeResponse(response),
        CloudTranscriptionProvider.HyperWhisperCloud => HyperwhisperCoreMethods.HyperwhisperCloudParseTranscribeResponse(response),
        CloudTranscriptionProvider.Meta => HyperwhisperCoreMethods.MetaParseTranscribeResponse(response),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private static CloudTranscript ToPublic(HwTranscript value) =>
        new(value.@text, value.@creditsRemaining, value.@cost, value.@rawProvider);

    private static CloudTranscriptionFailure MapFailure(
        HwTranscriptionException exception,
        CloudTranscriptionProvider provider,
        int? status = null,
        int? retryAfter = null) => exception switch
    {
        HwTranscriptionException.Unauthorized => new(CloudTranscriptionErrorCode.Unauthorized, "The provider rejected the supplied credential.", provider, status),
        HwTranscriptionException.QuotaExceeded => new(CloudTranscriptionErrorCode.QuotaExceeded, "The provider quota is exhausted.", provider, status),
        HwTranscriptionException.FileTooLarge => new(CloudTranscriptionErrorCode.FileTooLarge, "The audio file exceeds the provider limit.", provider, status ?? 413),
        HwTranscriptionException.RateLimited rate => new(CloudTranscriptionErrorCode.RateLimited, "The provider rate limit was reached.", provider, status ?? 429, retryAfter ?? SaturatingInt(rate.@retryAfterSecs)),
        HwTranscriptionException.ProviderUnavailable unavailable => new(CloudTranscriptionErrorCode.ProviderUnavailable, "The provider is temporarily unavailable.", provider, unavailable.@status),
        HwTranscriptionException.NoSpeech => new(CloudTranscriptionErrorCode.NoSpeech, "No speech was detected.", provider, status),
        HwTranscriptionException.BadRequest => new(CloudTranscriptionErrorCode.InvalidRequest, "The provider rejected the transcription request.", provider, status),
        HwTranscriptionException.Parse => new(CloudTranscriptionErrorCode.InvalidRequest, "The provider returned an invalid response.", provider, status),
        _ => new(CloudTranscriptionErrorCode.Unknown, "Cloud transcription failed.", provider, status),
    };

    private static int? SaturatingInt(ulong? value) => value.HasValue
        ? (int)Math.Min(value.Value, int.MaxValue)
        : null;

    private static int? RetryAfter(HttpResponse response)
    {
        var value = response.@headers.FirstOrDefault(header =>
            string.Equals(header.@name, "Retry-After", StringComparison.OrdinalIgnoreCase))?.@value;
        return int.TryParse(value, out var seconds) ? Math.Max(0, seconds) : null;
    }

    private static CloudFailureException PollTimeout(CloudTranscriptionProvider provider) =>
        new(new CloudTranscriptionFailure(
            CloudTranscriptionErrorCode.ProviderUnavailable,
            "Timed out waiting for provider processing.",
            provider));

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".flac" => "audio/flac",
        ".ogg" => "audio/ogg",
        ".webm" => "audio/webm",
        _ => "application/octet-stream",
    };

    private sealed class ExecutionState
    {
        public int Attempts { get; set; }
    }

    private sealed class CloudFailureException(CloudTranscriptionFailure failure) : Exception(failure.Message)
    {
        public CloudTranscriptionFailure Failure { get; } = failure;
    }
}
