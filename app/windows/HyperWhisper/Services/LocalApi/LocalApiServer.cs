using System.ComponentModel;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using HyperWhisper.LocalApi;
using HyperWhisper.Services.Transcription;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services.LocalApi;

/// <summary>
/// LOCAL API SERVER
///
/// Loopback-only HTTP server (Kestrel, in-process) that exposes a small set
/// of endpoints for MCP clients, benchmark scripts, and shell automation.
/// Off by default; opt-in via Settings → Local API.
///
/// Mirrors the macOS LocalAPIServer surface 1:1 so the same cURL / MCP
/// snippet works against either platform unchanged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LocalApiServer : INotifyPropertyChanged
{
    private static readonly Lazy<LocalApiServer> _instance = new(() => new LocalApiServer());
    public static LocalApiServer Instance => _instance.Value;

    // MARK: - Published state (bind to from XAML / code-behind)

    private int _listeningPort;
    public int ListeningPort
    {
        get => _listeningPort;
        private set { if (_listeningPort != value) { _listeningPort = value; OnChanged(); } }
    }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (_isRunning != value) { _isRunning = value; OnChanged(); } }
    }

    private string _bearerToken = "";
    public string BearerToken
    {
        get => _bearerToken;
        private set { if (_bearerToken != value) { _bearerToken = value; OnChanged(); } }
    }

    private string? _lastError;
    public string? LastError
    {
        get => _lastError;
        private set { if (_lastError != value) { _lastError = value; OnChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));

    // MARK: - Dependencies (set by Configure once during App.OnStartup)

    private ModelLibraryManager? _modelLibrary;
    private WhisperModelService? _whisperModels;
    private ParakeetModelService? _parakeetModels;
    private LocalLlmModelService? _localLlmModels;
    private CloudProviderHealthService? _cloudHealth;
    private ApiKeyService? _apiKeys;
    private TranscriptionOrchestrator? _orchestrator;
    private ITranscriptionProvider? _localTranscriptionProvider;
    private ITranscriptionProvider? _parakeetTranscriptionProvider;

    internal ModelLibraryManager? ModelLibrary => _modelLibrary;
    internal WhisperModelService? WhisperModels => _whisperModels;
    internal ParakeetModelService? ParakeetModels => _parakeetModels;
    internal LocalLlmModelService? LocalLlmModels => _localLlmModels;
    internal CloudProviderHealthService? CloudHealth => _cloudHealth;
    internal ApiKeyService? ApiKeys => _apiKeys;
    internal TranscriptionOrchestrator? TranscriptionOrchestrator => _orchestrator;
    internal ITranscriptionProvider? LocalTranscriptionProvider => _localTranscriptionProvider;
    internal ITranscriptionProvider? ParakeetTranscriptionProvider => _parakeetTranscriptionProvider;

    // MARK: - Runtime state

    private WebApplication? _app;
    private Task? _runTask;
    private Task? _stopTask;
    private readonly object _lifecycleLock = new();

    // Kestrel's own wait for in-flight requests is 2 s (Stop below). Quit
    // waits a little past it so a cancelled request can reach its finally
    // and delete its scratch file before the process exits (#1476).
    private static readonly TimeSpan ShutdownStopWait = TimeSpan.FromSeconds(3);

    private LocalApiServer() { }

    /// <summary>
    /// Inject dependencies. Call once during application startup, before the
    /// server can serve traffic. Idempotent — last call wins.
    /// </summary>
    public void Configure(
        ModelLibraryManager modelLibrary,
        WhisperModelService whisperModels,
        ParakeetModelService parakeetModels,
        LocalLlmModelService localLlmModels,
        CloudProviderHealthService cloudHealth,
        ApiKeyService apiKeys,
        TranscriptionOrchestrator orchestrator,
        ITranscriptionProvider localProvider,
        ITranscriptionProvider parakeetProvider)
    {
        _modelLibrary = modelLibrary;
        _whisperModels = whisperModels;
        _parakeetModels = parakeetModels;
        _localLlmModels = localLlmModels;
        _cloudHealth = cloudHealth;
        _apiKeys = apiKeys;
        _orchestrator = orchestrator;
        _localTranscriptionProvider = localProvider;
        _parakeetTranscriptionProvider = parakeetProvider;
    }

    // MARK: - Lifecycle

    /// <summary>
    /// Bind 127.0.0.1 on the persisted port (or ephemeral if 0/taken), register
    /// the route table, and write the discovery file. Idempotent — calling
    /// while already running is a no-op.
    /// </summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_app != null)
            {
                LoggingService.Debug("LocalApiServer.Start() called while already running");
                return;
            }

            LastError = null;
            BearerToken = LocalApiAuth.LoadOrCreateToken();

            var preferredPort = SettingsService.Instance.LocalApiServerPersistedPort;
            try
            {
                _app = BuildApp(preferredPort);
            }
            catch (Exception ex)
            {
                LastError = $"Failed to construct HTTP host: {ex.Message}";
                LoggingService.Error($"LocalApiServer: build failed: {ex.Message}", ex);
                _app = null;
                return;
            }

            _runTask = Task.Run(async () =>
            {
                // Bind failures get the ephemeral-port fallback below, so ONLY
                // StartAsync sits in this first try — a failure in port
                // persistence, discovery-file write, or WaitForShutdownAsync must
                // not be misread as "port unavailable" and trigger a pointless
                // rebind loop.
                try
                {
                    await _app.StartAsync();
                }
                catch (Exception bindEx) when (LocalApiBindFallback.ShouldRetryWithEphemeral(bindEx, preferredPort))
                {
                    // The preferred port is advisory only — clients rediscover the
                    // live port via local-api.json, so any failure to bind it (port
                    // taken, reserved by a Hyper-V/WSL excluded range → WSAEACCES,
                    // address not available, …) is recoverable: wipe the preference
                    // and retry on an ephemeral port, which effectively never fails.
                    // The preferredPort != 0 guard means we only fall back once.
                    // Note: Kestrel wraps EADDRINUSE in an IOException but surfaces
                    // WSAEACCES as a bare SocketException, so we catch broadly here.
                    LoggingService.Info($"LocalApiServer: persisted port {preferredPort} unavailable ({LocalApiBindFallback.Describe(bindEx)}); clearing preference and retrying ephemeral");
                    SettingsService.Instance.LocalApiServerPersistedPort = 0;
                    await DisposeAppQuietlyAsync();
                    Start();
                    return;
                }
                catch (Exception ex)
                {
                    await HandleRunFailureAsync(ex.Message);
                    return;
                }

                try
                {
                    var boundPort = ExtractPort(_app);
                    if (boundPort <= 0)
                    {
                        await HandleRunFailureAsync("Kestrel started but exposed no IPv4 address");
                        return;
                    }

                    SettingsService.Instance.LocalApiServerPersistedPort = boundPort;

                    var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";
                    var discoveryWarning = LocalApiDiscoveryFile.Write(boundPort, BearerToken, appVersion);

                    ListeningPort = boundPort;
                    IsRunning = true;
                    LastError = discoveryWarning;
                    LoggingService.Info($"LocalApiServer listening on 127.0.0.1:{boundPort}");

                    await _app.WaitForShutdownAsync();
                }
                catch (Exception ex)
                {
                    await HandleRunFailureAsync(ex.Message);
                }
            });
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (_app == null) return;
            var app = _app;
            _app = null;
            // Not awaited: this runs inside lock (_lifecycleLock), and awaiting
            // shutdown while holding the lock would block every other lifecycle
            // call. Shutdown waits on the stored task, bounded, at Quit.
            _stopTask = Task.Run(async () =>
            {
                try { await app.StopAsync(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
                try { await app.DisposeAsync(); } catch { /* shutting down */ }
            });
            LocalApiDiscoveryFile.Delete();
            IsRunning = false;
            ListeningPort = 0;
            LoggingService.Info("LocalApiServer stopped");
        }
    }

    /// <summary>
    /// Shut down the server. Called from <c>App.OnExit</c> only — clean
    /// lifecycle teardown at process exit. The orchestrator and local
    /// provider are owned by <see cref="TranscriptionRuntime"/> (shared with
    /// the GUI <c>MainViewModel</c>) so their disposal is left to process
    /// exit; the OS reclaims the native handles.
    /// </summary>
    public void Shutdown()
    {
        Stop();

        // Stop only starts the host's shutdown, and the process used to exit
        // 8 ms later, so a request still running never reached the finally
        // that deletes its decoded upload or snapshot (#1476). Wait, bounded,
        // only when a request holds a scratch file; then delete what is left.
        if (LocalApiScratchFiles.InFlightCount == 0)
        {
            return;
        }

        Task? stopTask;
        lock (_lifecycleLock) { stopTask = _stopTask; }
        try { stopTask?.Wait(ShutdownStopWait); } catch { /* shutting down */ }

        var deleted = LocalApiScratchFiles.DeleteInFlight();
        if (deleted > 0)
        {
            LoggingService.Info($"LocalApiServer: deleted {deleted} scratch file(s) of interrupted /transcribe requests");
        }
    }

    /// <summary>Restart so dependency changes take effect.</summary>
    public void Restart()
    {
        Stop();
        Start();
    }

    /// <summary>
    /// Wipe and regenerate the bearer token, then restart the server so the
    /// new token gets written into local-api.json. Wired to Settings →
    /// "Regenerate token".
    /// </summary>
    public void RegenerateBearerToken()
    {
        LocalApiAuth.RegenerateToken();
        if (IsRunning)
        {
            Restart();
        }
        else
        {
            BearerToken = LocalApiAuth.LoadOrCreateToken();
        }
    }

    // MARK: - Kestrel host construction

    /// <summary>
    /// Construct the shared Local API server (<see cref="PortableLocalApi"/>)
    /// over this head's backend. <c>internal</c> so the smoke suite can drive
    /// the REAL host rather than a look-alike it built itself; nothing binds a
    /// socket until <c>StartAsync</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Since issue #356 item 6 this head no longer has a route table of its own.
    /// The shared server owns the routes, the DNS-rebind origin guard, the
    /// bearer check, the body and upload limits and the <c>file</c> path
    /// policy, the same code the Linux head runs. Windows plugs in through the
    /// option hooks, each of which is null, and so absent, on Linux:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="PortableLocalApiOptions.AuthorizedRequestGate"/> is the
    ///     first-run gate below.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="PortableLocalApiOptions.AllowedFileRoots"/>,
    ///     <see cref="PortableLocalApiOptions.IsAdditionalAllowedFile"/> and
    ///     <see cref="PortableLocalApiOptions.VerifyOpenedFile"/> are the
    ///     recordings roots and the opened-handle check (#740), in
    ///     <see cref="LocalApiFilePolicy"/>.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="PortableLocalApiOptions.OriginRejected"/> keeps the
    ///     warning this head logged for a refused Host/Origin.
    ///   </description></item>
    /// </list>
    /// </remarks>
    internal WebApplication BuildApp(int preferredPort)
    {
        var options = new PortableLocalApiOptions(
            BearerToken,
            preferredPort,
            LocalApiLimits.MaxRequestBytes,
            checked((int)LocalApiLimits.MaxUploadBytes),
            AllowedFileRoots: LocalApiFilePolicy.AllowedRoots)
        {
            AuthorizedRequestGate = OnboardingGateAsync,
            IsAdditionalAllowedFile = LocalApiFilePolicy.IsAdditionalAllowedFile,
            VerifyOpenedFile = LocalApiFilePolicy.VerifyOpenedFile,
            OriginRejected = ctx => LoggingService.Warn(
                $"LocalApiServer: rejected request with disallowed Host/Origin (possible DNS-rebinding) — {ctx.Request.Method} {ctx.Request.Path}"),
        };

        return PortableLocalApi.Build([], options, new WindowsLocalApiBackend(this), builder =>
        {
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                // Transcription/post-processing jobs can run much longer than
                // Kestrel's defaults — match macOS's 600 s ceiling.
                kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(600);
                kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(600);
            });
        });
    }

    /// <summary>
    /// First-run gate — one gate for the whole mutating surface, run after the
    /// origin guard and the bearer check.
    /// </summary>
    /// <remarks>
    /// The onboarding window stages the DEFAULT MODE ROW and the active Mode
    /// selection, then blind-restores its snapshot on "Set Up Later" with no
    /// version check. A client that PATCHed that row while the flow sat on
    /// the Try It step had its write destroyed silently. In the other
    /// direction /transcribe loads a model into the very
    /// TranscriptionRuntime.Orchestrator and Parakeet provider the Try It
    /// step is using.
    ///
    /// The rule is per-METHOD rather than per-route on purpose: the reads
    /// (/health, /models, GET /modes, /recordings) are harmless and stay
    /// available for a client that is mid-poll, while every write and every
    /// engine-driving call is refused — including one added to the shared
    /// server later, which is exactly the leak the per-call-site shape had.
    ///
    /// ENGINE_UNAVAILABLE, not a new code: the wire codes are a closed
    /// fourteen shared with macOS through hw-localapi.
    /// </remarks>
    internal static async ValueTask<bool> OnboardingGateAsync(HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (IsReadOnlyMethod(ctx.Request.Method) || !OnboardingSession.IsActive)
        {
            return true;
        }

        LoggingService.Info(
            $"LocalApiServer: refused {ctx.Request.Method} {ctx.Request.Path} while the first-run window is open");
        var failure = HyperwhisperCoreMethods.LocalApiBusinessFailure(
            HwLocalApiErrorCode.EngineUnavailable, OnboardingBusyMessage, OnboardingBusyHint);
        // 200, like every other business failure on this server.
        ctx.Response.StatusCode = failure.httpStatus;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(failure.json);
        return false;
    }

    /// <summary>
    /// GET and HEAD read; everything else writes, drives the shared
    /// transcription engine, or both. Internal so the smoke suite asserts on the
    /// same predicate the gate runs rather than a second copy of it.
    /// </summary>
    internal static bool IsReadOnlyMethod(string? method) =>
        string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);

    /// <summary>The message and hint the first-run gate answers with.</summary>
    internal const string OnboardingBusyMessage =
        "HyperWhisper first-run setup is open and owns the default Mode and the local transcription engine";

    internal const string OnboardingBusyHint =
        "Finish or dismiss the HyperWhisper setup window, then retry.";

    private static int ExtractPort(WebApplication app)
    {
        var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        var addresses = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
        if (addresses == null) return 0;

        foreach (var addr in addresses)
        {
            if (Uri.TryCreate(addr, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return uri.Port;
            }
        }
        return 0;
    }

    private async Task HandleRunFailureAsync(string message)
    {
        LastError = message;
        IsRunning = false;
        ListeningPort = 0;
        LocalApiDiscoveryFile.Delete();
        LoggingService.Error($"LocalApiServer stopped with error: {message}");
        await DisposeAppQuietlyAsync();
    }

    private async Task DisposeAppQuietlyAsync()
    {
        WebApplication? snapshot;
        lock (_lifecycleLock) { snapshot = _app; _app = null; }
        if (snapshot == null) return;
        try { await snapshot.DisposeAsync(); } catch { /* swallow */ }
    }
}
