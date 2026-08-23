using System.Net;
using System.Text;
using System.Text.Json;
using HyperWhisper.CloudAccount;
using HyperWhisper.Platform.Abstractions;

var tests = new (string Name, Func<Task> Run)[]
{
    ("activation validates and securely stores the key", ActivationStoresKey),
    ("activation validates required fields before HTTP", ActivationInputValidation),
    ("rejected activation neither stores nor leaks the key", RejectedActivationIsRedacted),
    ("a 200 invalid verdict cannot activate", InvalidVerdictCannotActivate),
    ("status reads secure storage and returns account details", StatusReturnsDetails),
    ("status reports an expired account without transport failure", ExpiredStatus),
    ("credit refresh uses the production usage contract", CreditRefresh),
    ("invalid credit values fail closed", InvalidCredits),
    ("deactivation acknowledges local-only server contract", Deactivation),
    ("deactivation preserves the key when server fails", FailedDeactivationPreservesKey),
    ("credential failures are stable and redacted", CredentialFailures),
    ("HTTP cancellation is distinct from timeout", CancellationAndTimeout),
    ("responses are bounded", BoundedResponse),
    ("redirects are not treated as success", RedirectRejected),
    ("account URLs match repository contracts", AccountLinks),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static async Task ActivationStoresKey()
{
    RequestSnapshot? seen = null;
    var handler = new StubHandler(async (request, _) =>
    {
        seen = await RequestSnapshot.FromAsync(request);
        return Json(HttpStatusCode.OK,
            "{\"valid\":true,\"customer_id\":\"customer-1\",\"customer_email\":\"ray@example.test\"}");
    });
    var store = new MemoryCredentialStore();
    using var service = Service(store, handler);
    var result = await service.ActivateAsync(new("  secret-account-key  ", "device-1", "Ray Linux"));

    Assert(result.IsSuccess && result.Value?.IsActive == true, "activation was not accepted");
    Assert(result.Value?.CustomerEmail == "ray@example.test", "customer email was not parsed");
    Assert(store.StoredText == "secret-account-key", "trimmed account key was not stored");
    Assert(seen?.Uri == "https://www.hyperwhisper.com/api/license/validate", "validate endpoint mismatch");
    Assert(seen?.Method == "POST", "validate method mismatch");
    using var body = JsonDocument.Parse(seen?.Body ?? "{}");
    Assert(body.RootElement.GetProperty("license_key").GetString() == "secret-account-key", "account key body mismatch");
    Assert(body.RootElement.GetProperty("device_id").GetString() == "device-1", "device identity missing");
    Assert(body.RootElement.GetProperty("device_name").GetString() == "Ray Linux", "device name missing");
}

static async Task ActivationInputValidation()
{
    var handler = new StubHandler((_, _) => throw new InvalidOperationException("HTTP should not run"));
    using var service = Service(new MemoryCredentialStore(), handler);
    var missingKey = await service.ActivateAsync(new(" ", "device", "Ray Linux"));
    var missingDevice = await service.ActivateAsync(new("key", "", "Ray Linux"));
    var nul = await service.ActivateAsync(new("key\0tail", "device", "Ray Linux"));
    Assert(missingKey.Failure?.Code == CloudAccountFailureCode.MissingAccountKey, "missing key was accepted");
    Assert(missingDevice.Failure?.Code == CloudAccountFailureCode.InvalidRequest, "missing device was accepted");
    Assert(nul.Failure?.Code == CloudAccountFailureCode.InvalidRequest, "NUL field was accepted");
    Assert(handler.CallCount == 0, "invalid activation reached HTTP");
}

static async Task RejectedActivationIsRedacted()
{
    const string secret = "account-key-never-leak";
    var handler = new StubHandler((_, _) => Task.FromResult(Json(
        HttpStatusCode.BadRequest, $"{{\"error\":\"bad {secret}\"}}")));
    var store = new MemoryCredentialStore();
    using var service = Service(store, handler);
    var result = await service.ActivateAsync(new(secret, "device", "host"));
    Assert(result.Failure?.Code == CloudAccountFailureCode.Rejected, "rejection code mismatch");
    Assert(!result.Failure!.Message.Contains(secret, StringComparison.Ordinal), "secret leaked in failure");
    Assert(store.StoredText is null, "rejected key was stored");
}

static async Task InvalidVerdictCannotActivate()
{
    var handler = new StubHandler((_, _) => Task.FromResult(Json(
        HttpStatusCode.OK, "{\"valid\":false,\"status\":\"invalid\",\"error\":\"nope\"}")));
    var store = new MemoryCredentialStore();
    using var service = Service(store, handler);
    var result = await service.ActivateAsync(new("secret", "device", "host"));
    Assert(result.Failure?.Code == CloudAccountFailureCode.Rejected, "invalid verdict activated");
    Assert(store.StoredText is null, "invalid verdict was persisted");
}

static async Task StatusReturnsDetails()
{
    var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
        "{\"status\":\"active\",\"customer_id\":\"c1\",\"customer_email\":\"ray@example.test\",\"expires_at\":\"2030-01-02T03:04:05Z\"}")));
    using var service = Service(new MemoryCredentialStore("stored-key"), handler);
    var result = await service.GetStatusAsync("device", "host");
    Assert(result.IsSuccess && result.Value?.Status == CloudAccountStatus.Active, "status mismatch");
    Assert(result.Value?.ExpiresAt == DateTimeOffset.Parse("2030-01-02T03:04:05Z"), "expiry mismatch");
}

static async Task ExpiredStatus()
{
    var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
        "{\"valid\":false,\"expired\":true,\"customer_email\":\"ray@example.test\"}")));
    using var service = Service(new MemoryCredentialStore("stored-key"), handler);
    var result = await service.GetStatusAsync("device", "host");
    Assert(result.IsSuccess && result.Value?.Status == CloudAccountStatus.Expired, "expired status was hidden as a transport failure");
}

static async Task CreditRefresh()
{
    RequestSnapshot? seen = null;
    var handler = new StubHandler(async (request, _) =>
    {
        seen = await RequestSnapshot.FromAsync(request);
        return Json(HttpStatusCode.OK,
            "{\"credits_remaining\":42.5,\"minutes_remaining\":7,\"credits_per_minute\":5.5,\"is_licensed\":true,\"is_anonymous\":false}");
    });
    using var service = Service(new MemoryCredentialStore("key with space"), handler);
    var result = await service.RefreshCreditsAsync();
    Assert(result.IsSuccess && result.Value?.CreditsRemaining == 42.5, "credits mismatch");
    Assert(seen?.Uri == "https://transcribe-prod-v2.hyperwhisper.com/usage?identifier=key%20with%20space&force_refresh=true", "usage URL mismatch");
    Assert(seen?.Method == "GET", "usage method mismatch");
}

static async Task InvalidCredits()
{
    foreach (var body in new[]
    {
        "{\"credits_remaining\":-1,\"minutes_remaining\":2,\"credits_per_minute\":5.5}",
        "{}",
    })
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, body)));
        using var service = Service(new MemoryCredentialStore("key"), handler);
        var result = await service.RefreshCreditsAsync();
        Assert(result.Failure?.Code == CloudAccountFailureCode.InvalidResponse, "invalid credits were accepted");
    }
}

static async Task Deactivation()
{
    RequestSnapshot? seen = null;
    var handler = new StubHandler(async (request, _) =>
    {
        seen = await RequestSnapshot.FromAsync(request);
        return Json(HttpStatusCode.OK, "{\"success\":true,\"message\":\"License deactivated successfully\"}");
    });
    var store = new MemoryCredentialStore("local-key");
    using var service = Service(store, handler);
    var result = await service.DeactivateAsync();
    Assert(result.Value is { ServerAcknowledged: true, ServerRevocationSupported: false }, "stub semantics were not explicit");
    Assert(store.StoredText is null && store.DeleteCount == 1, "local key was not removed");
    Assert(seen?.Uri == "https://www.hyperwhisper.com/api/license/deactivate", "deactivation endpoint mismatch");
}

static async Task FailedDeactivationPreservesKey()
{
    var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "{}")));
    var store = new MemoryCredentialStore("keep-me");
    using var service = Service(store, handler);
    var result = await service.DeactivateAsync();
    Assert(result.Failure?.Code == CloudAccountFailureCode.ServerUnavailable, "server failure mismatch");
    Assert(store.StoredText == "keep-me" && store.DeleteCount == 0, "key was deleted without acknowledgement");
}

static async Task CredentialFailures()
{
    var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{\"valid\":true}")));
    var readStore = new MemoryCredentialStore { FailRead = true };
    using var readService = Service(readStore, handler);
    var read = await readService.RefreshCreditsAsync();
    Assert(read.Failure?.Code == CloudAccountFailureCode.CredentialReadFailed, "read failure mismatch");

    var writeStore = new MemoryCredentialStore { FailWrite = true };
    using var writeService = Service(writeStore, handler);
    var write = await writeService.ActivateAsync(new("secret", "device", "host"));
    Assert(write.Failure?.Code == CloudAccountFailureCode.CredentialWriteFailed, "write failure mismatch");
    Assert(!write.Failure!.Message.Contains("secret", StringComparison.Ordinal), "write failure leaked key");
}

static async Task CancellationAndTimeout()
{
    var handler = new StubHandler(async (_, token) =>
    {
        await Task.Delay(TimeSpan.FromMinutes(1), token);
        return Json(HttpStatusCode.OK, "{}");
    });
    using var cancelledService = Service(new MemoryCredentialStore("key"), handler, TimeSpan.FromSeconds(2));
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var cancelled = await cancelledService.RefreshCreditsAsync(cts.Token);
    Assert(cancelled.Failure?.Code == CloudAccountFailureCode.Cancelled, "cancellation mismatch");

    using var timeoutService = Service(new MemoryCredentialStore("key"), handler, TimeSpan.FromMilliseconds(25));
    var timedOut = await timeoutService.RefreshCreditsAsync();
    Assert(timedOut.Failure?.Code == CloudAccountFailureCode.TimedOut, "timeout mismatch");
}

static async Task BoundedResponse()
{
    var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(new byte[65 * 1024]),
    }));
    using var service = Service(new MemoryCredentialStore("key"), handler);
    var result = await service.RefreshCreditsAsync();
    Assert(result.Failure?.Code == CloudAccountFailureCode.ResponseTooLarge, "oversized response was read");
}

static async Task RedirectRejected()
{
    var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
    {
        Headers = { Location = new Uri("https://attacker.example/") },
    }));
    using var service = Service(new MemoryCredentialStore(), handler);
    var result = await service.ActivateAsync(new("secret", "device", "host"));
    Assert(result.Failure?.Code == CloudAccountFailureCode.NetworkFailure, "redirect was accepted");
}

static Task AccountLinks()
{
    Assert(CloudAccountLinks.Purchase.AbsoluteUri == "https://www.hyperwhisper.com/credits", "purchase URL mismatch");
    Assert(CloudAccountLinks.ManageAccount.AbsoluteUri == "https://www.hyperwhisper.com/user", "manage URL mismatch");
    Assert(CloudAccountLinks.PurchaseFor("a b", true).AbsoluteUri == "https://www.hyperwhisper.com/credits?license_key=a%20b", "key purchase URL mismatch");
    Assert(CloudAccountLinks.PurchaseFor("device", false).AbsoluteUri == "https://www.hyperwhisper.com/credits?device_id=device", "device purchase URL mismatch");
    return Task.CompletedTask;
}

static PortableCloudAccountService Service(
    MemoryCredentialStore store,
    HttpMessageHandler handler,
    TimeSpan? timeout = null) =>
    new(store, new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, timeout);

static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
{
    Content = new StringContent(json, Encoding.UTF8, "application/json"),
};

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class MemoryCredentialStore : ICredentialStore
{
    private byte[]? _stored;

    public MemoryCredentialStore(string? initial = null)
    {
        if (initial is not null) _stored = Encoding.UTF8.GetBytes(initial);
    }

    public bool FailRead { get; init; }
    public bool FailWrite { get; init; }
    public int DeleteCount { get; private set; }
    public string? StoredText => _stored is null ? null : Encoding.UTF8.GetString(_stored);

    public PlatformResult<byte[]?> Read(string resource, string account) => FailRead
        ? PlatformResult<byte[]?>.Failure("read", "secret value")
        : PlatformResult<byte[]?>.Success(_stored?.ToArray());

    public PlatformResult Write(string resource, string account, ReadOnlySpan<byte> value)
    {
        if (FailWrite) return PlatformResult.Failure("write", "secret value");
        _stored = value.ToArray();
        return PlatformResult.Success();
    }

    public PlatformResult Delete(string resource, string account)
    {
        DeleteCount++;
        _stored = null;
        return PlatformResult.Success();
    }
}

sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    : HttpMessageHandler
{
    public int CallCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        return responder(request, cancellationToken);
    }
}

sealed record RequestSnapshot(string Method, string Uri, string Body)
{
    public static async Task<RequestSnapshot> FromAsync(HttpRequestMessage request) => new(
        request.Method.Method,
        request.RequestUri?.AbsoluteUri ?? "",
        request.Content is null ? "" : await request.Content.ReadAsStringAsync());
}
