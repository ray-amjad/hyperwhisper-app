using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HyperWhisper.LocalApi;

/// <summary>
/// Issue #2014. `ReadFromJsonAsync` throws `InvalidOperationException` on a
/// body that is not JSON by its Content-Type or names an unknown charset,
/// and the error envelope maps that type to 200 `ENGINE_UNAVAILABLE`. A
/// malformed request is the caller's fault, so every JSON route must answer
/// 400 `INVALID_REQUEST` instead. Runs over a real loopback listener, the
/// same server Linux and Windows host.
/// </summary>
static class JsonContentTypeTests
{
    // `curl -d` sends form-urlencoded, the case the issue was found with. An
    // unknown charset on a JSON type and a look-alike of `application/json`
    // make `ReadFromJsonAsync` throw the same exception.
    static readonly string?[] MalformedContentTypes =
        ["application/x-www-form-urlencoded", "text/plain", null, "application/json; charset=bogus", "application/jsonx"];

    public static async Task JsonRoutesRefuseANonJsonContentType()
    {
        await using var app = PortableLocalApi.Build([], new PortableLocalApiOptions(Fixture.Token, 0), new FakeBackend());
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", Fixture.Token);

        (HttpMethod Method, string Path, string Body)[] routes =
        [
            (HttpMethod.Post, "/modes", """{"name":"x"}"""),
            (HttpMethod.Patch, "/modes/some-id", """{"name":"x"}"""),
            (HttpMethod.Post, "/post-process", """{"text":"raw words","preset":"hyper"}"""),
            (HttpMethod.Post, "/transcribe", """{"engine":"cloud","audio_base64":"AAAA"}"""),
        ];
        foreach (var (method, path, body) in routes)
        {
            foreach (var contentType in MalformedContentTypes)
            {
                using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
                if (contentType is not null) content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                using var request = new HttpRequestMessage(method, path) { Content = content };
                using var response = await client.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                var label = $"{method} {path} with Content-Type {contentType ?? "(none)"}";
                Check(response.StatusCode == HttpStatusCode.BadRequest, $"{label} answered {(int)response.StatusCode}, not 400: {text}");
                using var json = JsonDocument.Parse(text);
                Check(!json.RootElement.GetProperty("ok").GetBoolean(), $"{label} answered ok:true");
                var code = json.RootElement.GetProperty("error").GetProperty("code").GetString();
                Check(code == LocalApiErrorCodes.InvalidRequest, $"{label} answered {code}, not {LocalApiErrorCodes.InvalidRequest}");
                // /transcribe keeps its own Content-Type check and form path.
                if (path == "/transcribe") continue;
                var expected = contentType is not null && contentType.Contains("charset=bogus", StringComparison.Ordinal)
                    ? "The request body's charset is not supported." : "The request body must be application/json.";
                var message = json.RootElement.GetProperty("error").GetProperty("message").GetString();
                Check(message == expected, $"{label} answered \"{message}\", not \"{expected}\"");
            }
        }

        // The JSON Content-Types a client really sends still reach the route.
        foreach (var contentType in new[] { "application/json", "application/json; charset=utf-8", "application/merge-patch+json" })
        {
            using var content = new StringContent("""{"name":"x"}""", Encoding.UTF8);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            using var response = await client.PostAsync("/modes", content);
            Check(response.StatusCode == HttpStatusCode.OK, $"POST /modes with {contentType} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
