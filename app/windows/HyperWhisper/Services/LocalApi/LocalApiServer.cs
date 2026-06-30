using System.ComponentModel;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using HyperWhisper.Services.LocalApi.Endpoints;
using HyperWhisper.Services.Transcription;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    private readonly object _lifecycleLock = new();

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
                try
                {
                    await _app.StartAsync();
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
            _ = Task.Run(async () =>
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

    private WebApplication BuildApp(int preferredPort)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.WebHost.ConfigureKestrel(options =>
        {
            // Transcription/post-processing jobs can run much longer than
            // Kestrel's defaults — match macOS's 600 s ceiling.
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(600);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(600);
            options.Listen(IPAddress.Loopback, preferredPort);
        });

        var app = builder.Build();

        // Auth middleware — runs before any route except /health. Compares
        // the bearer token using FixedTimeEquals to avoid timing leaks.
        app.Use(async (ctx, next) =>
        {
            if (string.Equals(ctx.Request.Path, "/health", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            var header = ctx.Request.Headers["Authorization"].ToString();
            const string prefix = "Bearer ";
            if (string.IsNullOrEmpty(header) ||
                !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                await WriteUnauthorizedAsync(ctx);
                return;
            }

            var provided = header.Substring(prefix.Length).Trim();
            if (!LocalApiAuth.ConstantTimeEquals(provided, BearerToken))
            {
                await WriteUnauthorizedAsync(ctx);
                return;
            }

            await next();
        });

        // Route table — phase 3: /health, /models, /modes, /transcribe,
        // /post-process, /recordings/*. Full endpoint parity with macOS.
        HealthEndpoints.Map(app, this);
        ModelsEndpoints.Map(app, this);
        ModesEndpoints.Map(app, this);
        TranscribeEndpoints.Map(app, this);
        PostProcessEndpoints.Map(app, this);
        RecordingsEndpoints.Map(app, this);

        return app;
    }

    private static async Task WriteUnauthorizedAsync(HttpContext ctx)
    {
        ctx.Response.StatusCode = 401;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers["WWW-Authenticate"] = "Bearer realm=\"hyperwhisper\"";
        await ctx.Response.WriteAsync("{\"ok\":false,\"error\":{\"code\":\"INVALID_REQUEST\",\"message\":\"Missing or invalid bearer token\"}}");
    }

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
