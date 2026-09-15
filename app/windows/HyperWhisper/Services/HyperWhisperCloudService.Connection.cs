// HYPERWHISPER CLOUD SERVICE — CONNECTION
// Owns the HttpClient the service sends on: how it is built, how a request is
// stamped, how the TLS/HTTP2 connection is pre-warmed, and how the client is
// rebuilt after a DNS-shaped failure.

using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using HyperWhisper.Configuration;

namespace HyperWhisper.Services;

public partial class HyperWhisperCloudService
{
    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds),
        };
    }

    // HTTP version preference applied per-request. We prefer HTTP/2 so the
    // keepalive HEAD /warmup and a concurrent POST /transcribe multiplex as
    // parallel streams on the same pooled connection instead of queuing
    // (H1.1) or opening a second connection and paying another TCP+TLS
    // handshake. Fly's edge supports H2 via ALPN; RequestVersionOrLower
    // means we gracefully fall back to H1.1 if the server drops it.
    //
    // Why per-request and not `HttpClient.DefaultRequestVersion`:
    // `SendAsync(HttpRequestMessage)` does NOT honor the client-level
    // defaults — the HttpRequestMessage's own `Version` / `VersionPolicy`
    // wins. Setting it per-request is the only reliable way to opt in.
    // (macOS URLSession auto-negotiates H2 — no equivalent opt-in.)
    private static readonly Version PreferredHttpVersion = HttpVersion.Version20;
    private const HttpVersionPolicy PreferredVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

    // Also stamps the platform + app version headers, so every natively built
    // request through this service is attributable in the backend logs.
    private static HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Version = PreferredHttpVersion,
            VersionPolicy = PreferredVersionPolicy,
        };
        ClientInfoHeaders.Apply(request);
        return request;
    }

    // =========================================================================
    // CONNECTION PRE-WARM
    // =========================================================================

    private DateTime _lastWarmupAt = DateTime.MinValue;
    private static readonly TimeSpan WarmupMinInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Fires a HEAD /warmup to pre-establish the TLS/HTTP2 connection to Fly.
    /// Call on hotkey-down paths so the handshake runs in parallel with the user
    /// starting to speak. Fire-and-forget — never throws, never blocks. Routes
    /// through the same <see cref="_httpClient"/> as /transcribe so the pooled
    /// connection is reused for the subsequent POST.
    /// </summary>
    public void PrewarmConnection()
    {
        if (DateTime.UtcNow - _lastWarmupAt < WarmupMinInterval)
            return;

        SendWarmup();
    }

    /// <summary>
    /// Bypasses the 60s warmup debounce. Used by the foreground keepalive
    /// ticker, which fires on its own ~45s cadence and would otherwise be
    /// absorbed into the debounce and throttled back to 60s — defeating the
    /// purpose of ticking faster than SocketsHttpHandler's pool-idle window.
    /// </summary>
    public void PrewarmConnectionForced()
    {
        SendWarmup();
    }

    private void SendWarmup()
    {
        _lastWarmupAt = DateTime.UtcNow;

        // ast-grep-ignore: no-discarded-task-run -- SendWarmup is on the hotkey-down path; HttpClient.SendAsync can write headers synchronously on a warm pool, so the hop is the point
        _ = Task.Run(async () =>
        {
            // Snapshot the current client so a concurrent rebuild (DNS recovery)
            // can't dispose it out from under us mid-flight.
            var client = Volatile.Read(ref _httpClient);
            try
            {
                var url = NetworkConfig.HyperWhisperCloudBaseUrl + "/warmup";
                using var req = CreateRequest(HttpMethod.Head, url);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                var region = response.Headers.TryGetValues("fly-region", out var vals)
                    ? string.Join(",", vals) : "?";
                LoggingService.Debug($"Cloud warmup ok · status={(int)response.StatusCode} · region={region} · httpVersion={response.Version}");
            }
            catch (Exception ex)
            {
                // Failed attempt shouldn't burn the debounce window — clear so the next hotkey retries.
                _lastWarmupAt = DateTime.MinValue;
                LoggingService.Debug($"Cloud warmup failed · {ex.Message}");
                if (IsDnsError(ex))
                {
                    RebuildHttpClient();
                }
            }
        });
    }

    /// <summary>
    /// True for errors that look like a stale/poisoned DNS cache — typical
    /// after a network flip (captive portal, VPN toggle, tether swap). Used
    /// to gate a one-shot HttpClient rebuild in the warmup callback and the
    /// transcribe retry.
    /// </summary>
    private static bool IsDnsError(Exception ex)
    {
        for (Exception? cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur is SocketException se &&
                (se.SocketErrorCode == SocketError.HostNotFound ||
                 se.SocketErrorCode == SocketError.TryAgain))
            {
                return true;
            }
        }
        return false;
    }

    private DateTime _lastRebuildAt = DateTime.MinValue;
    private static readonly TimeSpan MinRebuildInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Atomically swaps in a fresh <see cref="HttpClient"/> (and the
    /// underlying <see cref="SocketsHttpHandler"/>) so the next request
    /// re-resolves DNS and reopens TCP/TLS. Reactive: only called when an
    /// error looks DNS-shaped, so the cost is paid only when it would help.
    ///
    /// DOES NOT dispose the old client. In-flight sends that already
    /// snapshotted the old reference (via Volatile.Read) would otherwise see
    /// ObjectDisposedException when the handler's socket is yanked, which
    /// the HttpRequestException-only catches above would miss — killing a
    /// user's active transcription instead of recovering it. The old client
    /// is released for GC; its SocketsHttpHandler drains via
    /// PooledConnectionIdleTimeout (5 min) in the background.
    ///
    /// Gated by:
    ///   - _disposed: prevents resurrecting a torn-down service via a
    ///     fire-and-forget warmup completing post-Dispose.
    ///   - MinRebuildInterval: coarse cross-call gate so a flapping network
    ///     or an unaware caller (warmup + transcribe both seeing DNS errors
    ///     back-to-back) can't churn the pool.
    /// </summary>
    private void RebuildHttpClient()
    {
        if (_disposed) return;
        if (DateTime.UtcNow - _lastRebuildAt < MinRebuildInterval) return;
        _lastRebuildAt = DateTime.UtcNow;

        var fresh = CreateHttpClient();
        Interlocked.Exchange(ref _httpClient, fresh);
        LoggingService.Info("HyperWhisperCloudService: HttpClient rebuilt (DNS recovery)");
    }
}
