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
    ("deactivation removes the key without a network call", Deactivation),
    ("deactivation works offline", OfflineDeactivationRemovesKey),
    ("deactivation reports a keyring delete failure", DeactivationReportsAKeyringFailure),
    ("deactivation reports a missing key", DeactivationWithoutAKeyIsReported),
    ("deactivation clears the cached verdict", DeactivationClearsTheCachedVerdict),
    ("credential failures are stable and redacted", CredentialFailures),
    ("a failed key write is not cached as an active account", FailedWriteIsNotCached),
    ("HTTP cancellation is distinct from timeout", CancellationAndTimeout),
    ("responses are bounded", BoundedResponse),
    ("redirects are not treated as success", RedirectRejected),
    ("account URLs match repository contracts", AccountLinks),
    ("status inside the validation cache makes no network call", ValidationCacheAnswersWithoutHttp),
    ("an explicit refresh ignores the validation cache", ForcedRefreshIgnoresTheCache),
    ("the validation cache expires after 24 hours", ValidationCacheExpires),
    ("a transient server failure falls back to the cached verdict", TransientFailuresUseTheCache),
    ("an offline status with no cached verdict reports the failure", OfflineWithoutACacheReportsTheFailure),
    ("the offline grace expires after seven days", OfflineGraceExpires),
    ("a cached verdict is not honoured for a different key", CachedVerdictIsKeyBound),
    ("a rejected replacement key cannot clobber the stored verdict", RejectedReplacementKeyCannotClobber),
    ("an unreadable expiry does not fail an active account", UnreadableExpiryIsNotAFailure),
    ("the server's own rejection message reaches the caller", ServerRejectionMessageIsSurfaced),
    ("license state routes the key to the keyring and the cache to a file", LicenseStateRouting),
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
    // Deactivation is local only. /api/license/deactivate is a no-op stub, so the
    // round-trip bought nothing (issue #290) — the handler below must never run.
    var handler = new StubHandler((_, _) =>
        Task.FromResult(Json(HttpStatusCode.OK, "{\"success\":true}")));
    var store = new MemoryCredentialStore("local-key");
    using var service = Service(store, handler);
    var result = await service.DeactivateAsync();
    Assert(result.Value is { ServerAcknowledged: false, ServerRevocationSupported: false }, "stub semantics were not explicit");
    Assert(store.StoredText is null && store.DeleteCount == 1, "local key was not removed");
    Assert(handler.CallCount == 0, "deactivation still made a network call");
}

static async Task OfflineDeactivationRemovesKey()
{
    // The bug this replaces: every transport failure was terminal, so an offline
    // user could not remove their key at all. Removal must not touch the network.
    var handler = new StubHandler((_, _) =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException("no route to host")));
    var store = new MemoryCredentialStore("remove-me");
    using var service = Service(store, handler);
    var result = await service.DeactivateAsync();
    Assert(result.IsSuccess, "offline removal failed");
    Assert(store.StoredText is null && store.DeleteCount == 1, "offline removal left the key in place");
    Assert(handler.CallCount == 0, "offline removal reached for the network");
}

static async Task DeactivationReportsAKeyringFailure()
{
    // ICredentialStore.Delete has a real error channel (LinuxCredentialStore shells
    // out to secret-tool). A failed delete must not be reported as a removal.
    var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
    var store = new MemoryCredentialStore("keep-me") { FailDelete = true };
    using var service = Service(store, handler);
    var result = await service.DeactivateAsync();
    Assert(result.Failure?.Code == CloudAccountFailureCode.CredentialDeleteFailed, "delete failure mismatch");
    Assert(store.StoredText == "keep-me", "a failed delete still dropped the key");
}

static async Task DeactivationWithoutAKeyIsReported()
{
    var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
    var store = new MemoryCredentialStore();
    using var service = Service(store, handler);
    var result = await service.DeactivateAsync();
    Assert(result.Failure?.Code == CloudAccountFailureCode.MissingAccountKey, "missing key mismatch");
    Assert(store.DeleteCount == 0, "a delete was attempted with no key stored");
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

// ---------------------------------------------------------------------------
// hw-license core: validation cache, offline grace, and the verdict guard.
//
// These exercise the real Rust core through the real credential-backed store,
// not a stand-in. The point of issue #290 is that Linux answered a validate
// reply differently from macOS and Windows, and only the core can settle that.
// ---------------------------------------------------------------------------

static async Task ValidationCacheAnswersWithoutHttp()
{
    var store = new MemoryCredentialStore("stored-key");
    var state = LicenseState(store);
    var clock = new TestClock();
    var handler = ActiveValidateHandler();
    using var service = ServiceWith(store, handler, state, clock);

    var first = await service.GetStatusAsync("device", "host");
    Assert(first.IsSuccess && first.Value?.Status == CloudAccountStatus.Active, "first status was not active");
    Assert(handler.CallCount == 1, "the first status did not validate against the server");

    // Inside the 24-hour cache the core answers, so no second request is made.
    clock.Advance(TimeSpan.FromHours(23));
    var second = await service.GetStatusAsync("device", "host");
    Assert(second.IsSuccess && second.Value?.Status == CloudAccountStatus.Active, "the cached status was not active");
    Assert(handler.CallCount == 1, "a cached status still reached the network");
}

static async Task ForcedRefreshIgnoresTheCache()
{
    var store = new MemoryCredentialStore("stored-key");
    var state = LicenseState(store);
    var clock = new TestClock();
    var handler = ActiveValidateHandler();
    using var service = ServiceWith(store, handler, state, clock);

    await service.GetStatusAsync("device", "host");
    var forced = await service.GetStatusAsync("device", "host", forceRevalidate: true);
    Assert(forced.IsSuccess && forced.Value?.CustomerEmail == "ray@example.test",
        "the forced refresh did not return the server's account detail");
    Assert(handler.CallCount == 2, "the forced refresh was answered from the cache");
}

static async Task ValidationCacheExpires()
{
    var store = new MemoryCredentialStore("stored-key");
    var state = LicenseState(store);
    var clock = new TestClock();
    var handler = ActiveValidateHandler();
    using var service = ServiceWith(store, handler, state, clock);

    await service.GetStatusAsync("device", "host");
    clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
    await service.GetStatusAsync("device", "host");
    Assert(handler.CallCount == 2, "the validation cache outlived its 24-hour window");
}

static async Task TransientFailuresUseTheCache()
{
    // 429 and 5xx were terminal on Linux and transient on macOS and Windows.
    // A congested server must not downgrade an account that validated an hour ago.
    foreach (var transient in new Func<HttpResponseMessage>[]
    {
        () => Json(HttpStatusCode.TooManyRequests, "{\"error\":\"slow down\"}"),
        () => Json(HttpStatusCode.ServiceUnavailable, "{\"error\":\"maintenance\"}"),
        () => throw new HttpRequestException("no route to host"),
    })
    {
        var store = new MemoryCredentialStore("stored-key");
        var state = LicenseState(store);
        var clock = new TestClock();
        var active = true;
        var handler = new StubHandler((_, _) => active
            ? Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":\"active\"}"))
            : Task.FromResult(transient()));
        using var service = ServiceWith(store, handler, state, clock);

        await service.GetStatusAsync("device", "host");
        active = false;
        clock.Advance(TimeSpan.FromDays(2));

        var offline = await service.GetStatusAsync("device", "host");
        Assert(offline.IsSuccess && offline.Value?.Status == CloudAccountStatus.Active,
            "a transient failure did not fall back to the cached verdict");
    }
}

static async Task OfflineWithoutACacheReportsTheFailure()
{
    // Nothing was ever validated, so there is no verdict to stand in. Reporting
    // Invalid here would tell a user their account is bad when it is the network.
    var store = new MemoryCredentialStore("stored-key");
    var handler = new StubHandler((_, _) =>
        Task.FromException<HttpResponseMessage>(new HttpRequestException("no route to host")));
    using var service = Service(store, handler);

    var result = await service.GetStatusAsync("device", "host");
    Assert(result.Failure?.Code == CloudAccountFailureCode.NetworkFailure,
        "an unverifiable account was reported as a verdict");
}

static async Task OfflineGraceExpires()
{
    var store = new MemoryCredentialStore("stored-key");
    var state = LicenseState(store);
    var clock = new TestClock();
    var reachable = true;
    var handler = new StubHandler((_, _) => reachable
        ? Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":\"active\"}"))
        : Task.FromException<HttpResponseMessage>(new HttpRequestException("no route to host")));
    using var service = ServiceWith(store, handler, state, clock);

    await service.GetStatusAsync("device", "host");
    reachable = false;

    clock.Advance(TimeSpan.FromDays(7));
    var inGrace = await service.GetStatusAsync("device", "host");
    Assert(inGrace.IsSuccess && inGrace.Value?.Status == CloudAccountStatus.Active,
        "the seven-day offline grace ended early");

    clock.Advance(TimeSpan.FromSeconds(1));
    var pastGrace = await service.GetStatusAsync("device", "host");
    Assert(pastGrace.Failure?.Code == CloudAccountFailureCode.NetworkFailure,
        "the offline grace outlived its seven-day window");
}

static async Task CachedVerdictIsKeyBound()
{
    // The cache is one global entry tied to the key on file. An offline
    // activation of the SAME key may use it; a different key may not, or an
    // unverified key reads as active. macOS and Windows carry the same rule.
    var store = new MemoryCredentialStore();
    var state = LicenseState(store);
    var clock = new TestClock();
    var reachable = true;
    var handler = new StubHandler((_, _) => reachable
        ? Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":\"active\"}"))
        : Task.FromException<HttpResponseMessage>(new HttpRequestException("no route to host")));
    using var service = ServiceWith(store, handler, state, clock);

    await service.ActivateAsync(new("first-key", "device", "host"));
    reachable = false;

    var other = await service.ActivateAsync(new("second-key", "device", "host"));
    Assert(other.Failure?.Code == CloudAccountFailureCode.NetworkFailure,
        "a different key inherited the stored key's cached verdict");
    Assert(store.StoredText == "first-key", "an unverified key replaced the stored key");

    var same = await service.ActivateAsync(new("first-key", "device", "host"));
    Assert(same.IsSuccess && same.Value?.Status == CloudAccountStatus.Active,
        "the stored key could not be re-entered offline");
}

static async Task RejectedReplacementKeyCannotClobber()
{
    // A valid user who mistypes a second key must not be locked out for the
    // 24-hour cache window. The core owns this guard; this pins it end to end.
    var store = new MemoryCredentialStore();
    var state = LicenseState(store);
    var clock = new TestClock();
    var good = true;
    var handler = new StubHandler((_, _) => Task.FromResult(good
        ? Json(HttpStatusCode.OK, "{\"status\":\"active\"}")
        : Json(HttpStatusCode.OK, "{\"valid\":false,\"status\":\"invalid\"}")));
    using var service = ServiceWith(store, handler, state, clock);

    var activated = await service.ActivateAsync(new("good-key", "device", "host"));
    Assert(activated.IsSuccess, "the first activation failed");

    good = false;
    var typo = await service.ActivateAsync(new("typo-key", "device", "host"));
    Assert(typo.Failure?.Code == CloudAccountFailureCode.Rejected, "the mistyped key was accepted");
    Assert(store.StoredText == "good-key", "the mistyped key replaced the stored key");

    var status = await service.GetStatusAsync("device", "host");
    Assert(status.IsSuccess && status.Value?.Status == CloudAccountStatus.Active,
        "the mistyped key's verdict clobbered the stored key's cached status");
}

static async Task UnreadableExpiryIsNotAFailure()
{
    // An expiry this client cannot read used to turn a 200-OK active account
    // into an InvalidResponse on Linux alone.
    var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
        "{\"status\":\"active\",\"expires_at\":\"whenever\"}")));
    using var service = Service(new MemoryCredentialStore("stored-key"), handler);
    var result = await service.GetStatusAsync("device", "host");
    Assert(result.IsSuccess && result.Value?.Status == CloudAccountStatus.Active,
        "an unreadable expiry failed an active account");
    Assert(result.Value?.ExpiresAt is null, "an unreadable expiry was reported as a date");
}

static async Task ServerRejectionMessageIsSurfaced()
{
    // The old ValidateResponse never declared `error`, so the server's own
    // explanation was thrown away and every rejection read the same.
    var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
        "{\"valid\":false,\"status\":\"expired\",\"error\":\"Subscription lapsed\"}")));
    using var service = Service(new MemoryCredentialStore(), handler);
    var result = await service.ActivateAsync(new("lapsed-key", "device", "host"));
    Assert(result.Failure?.Code == CloudAccountFailureCode.Rejected, "the lapsed key was accepted");
    Assert(result.Failure!.Message == "Subscription lapsed", "the server's explanation was discarded");
}

static async Task DeactivationClearsTheCachedVerdict()
{
    var store = new MemoryCredentialStore();
    var state = LicenseState(store);
    var clock = new TestClock();
    var reachable = true;
    var handler = new StubHandler((_, _) => reachable
        ? Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":\"active\"}"))
        : Task.FromException<HttpResponseMessage>(new HttpRequestException("no route to host")));
    using var service = ServiceWith(store, handler, state, clock);

    await service.ActivateAsync(new("stored-key", "device", "host"));
    var removed = await service.DeactivateAsync();
    Assert(removed.IsSuccess, "removal failed");

    // The key is put back by hand: without the cache clear, the seven-day grace
    // would still report the removed account as active.
    reachable = false;
    store.Write("HyperWhisper", "LicenseKey", Encoding.UTF8.GetBytes("stored-key"));
    var status = await service.GetStatusAsync("device", "host");
    Assert(status.Failure?.Code == CloudAccountFailureCode.NetworkFailure,
        "removal left a cached verdict behind");
}

static async Task FailedWriteIsNotCached()
{
    // A locked keyring must not leave an active verdict cached against a key
    // that was never stored — the grace would report an unusable account.
    var store = new MemoryCredentialStore { FailWrite = true };
    var state = LicenseState(store);
    var clock = new TestClock();
    var reachable = true;
    var handler = new StubHandler((_, _) => reachable
        ? Task.FromResult(Json(HttpStatusCode.OK, "{\"status\":\"active\"}"))
        : Task.FromException<HttpResponseMessage>(new HttpRequestException("no route to host")));
    using var service = ServiceWith(store, handler, state, clock);

    var write = await service.ActivateAsync(new("secret", "device", "host"));
    Assert(write.Failure?.Code == CloudAccountFailureCode.CredentialWriteFailed, "write failure mismatch");

    // Put the key in place by hand, then go offline. With nothing cached the
    // grace has nothing to report, which is why the write comes first.
    store.FailWrite = false;
    store.Write("HyperWhisper", "LicenseKey", Encoding.UTF8.GetBytes("secret"));
    reachable = false;
    var status = await service.GetStatusAsync("device", "host");
    Assert(status.Failure?.Code == CloudAccountFailureCode.NetworkFailure,
        "a failed key write still cached an active verdict");
}

static Task LicenseStateRouting()
{
    // The key belongs in the keyring, 1:1 with what was stored before this
    // store existed. Everything else belongs in one flat file, so the cached
    // verdict survives a restart.
    var credentials = new MemoryCredentialStore();
    var files = new MemoryPrivateFileService();
    var state = new PortableLicenseStateStore(credentials, files, "/license-state.json");

    state.Set("com.hyperwhisper.license.key", "routed-key");
    state.Set("com.hyperwhisper.license.cachedStatus", "Active");
    Assert(credentials.StoredText == "routed-key", "the license key did not reach the credential store");
    Assert(files.ReadAllText("/license-state.json").Value?.Contains("cachedStatus", StringComparison.Ordinal) == true,
        "the cached verdict did not reach the state file");
    Assert(files.ReadAllText("/license-state.json").Value?.Contains("routed-key", StringComparison.Ordinal) == false,
        "the license key was written to the state file");

    var reloaded = new PortableLicenseStateStore(credentials, files, "/license-state.json");
    Assert(reloaded.Get("com.hyperwhisper.license.cachedStatus") == "Active",
        "the state file did not survive a restart");
    Assert(reloaded.Get("com.hyperwhisper.license.key") == "routed-key",
        "the license key was not read back from the credential store");

    state.Delete("com.hyperwhisper.license.key");
    Assert(credentials.StoredText is null, "the license key was not removed from the credential store");
    return Task.CompletedTask;
}

static StubHandler ActiveValidateHandler() => new((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
    "{\"status\":\"active\",\"customer_id\":\"c1\",\"customer_email\":\"ray@example.test\"}")));

static PortableLicenseStateStore LicenseState(MemoryCredentialStore store) =>
    new(store, new MemoryPrivateFileService(), "/license-state.json");

static PortableCloudAccountService Service(
    MemoryCredentialStore store,
    HttpMessageHandler handler,
    TimeSpan? timeout = null) =>
    ServiceWith(store, handler, LicenseState(store), new TestClock(), timeout);

static PortableCloudAccountService ServiceWith(
    MemoryCredentialStore store,
    HttpMessageHandler handler,
    PortableLicenseStateStore licenseState,
    TestClock clock,
    TimeSpan? timeout = null) =>
    new(store, licenseState, new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, timeout, clock);

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
    public bool FailWrite { get; set; }
    public bool FailDelete { get; init; }
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
        if (FailDelete) return PlatformResult.Failure("delete", "secret value");
        DeleteCount++;
        _stored = null;
        return PlatformResult.Success();
    }
}

/// <summary>An owner-only private file service, in memory.</summary>
sealed class MemoryPrivateFileService : IPrivateFileService
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public PlatformResult WriteAllBytesAtomically(string path, ReadOnlySpan<byte> contents)
    {
        _files[path] = contents.ToArray();
        return PlatformResult.Success();
    }

    public PlatformResult WriteAllTextAtomically(string path, string contents) =>
        WriteAllBytesAtomically(path, Encoding.UTF8.GetBytes(contents));

    public PlatformResult<byte[]?> ReadAllBytes(string path) =>
        PlatformResult<byte[]?>.Success(_files.TryGetValue(path, out var value) ? value.ToArray() : null);

    public PlatformResult<string?> ReadAllText(string path) =>
        PlatformResult<string?>.Success(
            _files.TryGetValue(path, out var value) ? Encoding.UTF8.GetString(value) : null);

    public PlatformResult Delete(string path)
    {
        _files.Remove(path);
        return PlatformResult.Success();
    }

    public PlatformResult<bool> IsRestrictedToCurrentUser(string path) =>
        PlatformResult<bool>.Success(_files.ContainsKey(path));
}

/// <summary>
/// A clock the test moves by hand. The core takes `now` at every time-dependent
/// call, so the 24-hour cache and the 7-day grace are testable without waiting.
/// </summary>
sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
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
