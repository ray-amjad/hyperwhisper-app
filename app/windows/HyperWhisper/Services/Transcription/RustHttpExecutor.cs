// RUST HTTP EXECUTOR (Wave 3 / Win-2)
//
// Shared sans-I/O HTTP plumbing for the Rust shared core. The Rust core builds a
// fully-described `HttpRequest` value (URL, method, headers, body) and parses an
// `HttpResponse` value; the PLATFORM owns all actual network I/O. Audio bytes
// NEVER cross the FFI boundary — a file is referenced by path
// (`Body.FileStream` / `HwPart.FileRef`) and the platform streams it from disk.
//
// This executor takes a binding `HttpRequest`, performs the I/O with `HttpClient`
// (+ `StreamContent`/`FileStream`), and returns a binding `HttpResponse`. It is
// reused by EVERY cloud STT provider, so the four `Body` cases below must be
// exactly right — every later provider inherits any bug here. It mirrors the
// already-shipped macOS `RustHTTPExecutor.swift` 1:1.
//
// TODO-verify (Windows/CI): Rust shared-core swap — compile-only on macOS; not
// built against the C# binding here. Verify under `dotnet build` in CI.

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using HyperWhisper.Models;

// Binding types (`HttpRequest`, `HttpResponse`, `Header`, `Body`, `HwPart`,
// `HttpMethod`) live in this namespace. They are `internal`, which is fine — the
// app and the binding compile into a single assembly. We qualify aggressively
// below because several names collide with `System.Net.Http.*` and
// `HyperWhisper.*` types.
using uniffi.hyperwhisper_core;

// Disambiguate the binding `HttpMethod` from `System.Net.Http.HttpMethod` for the
// whole file. The binding type is the one the core emits.
using RustHttpMethod = uniffi.hyperwhisper_core.HttpMethod;

namespace HyperWhisper.Services.Transcription;

/// <summary>
/// Executes a Rust-core-built <see cref="HttpRequest"/> over an
/// <see cref="HttpClient"/> and captures an <see cref="HttpResponse"/> for the
/// core to parse. Reused by all 12 cloud STT providers.
/// </summary>
internal static class RustHttpExecutor
{
    /// <summary>
    /// Sentinel multipart field name marking a single-<c>FileRef</c> body that
    /// must be streamed as the RAW request body (not a <c>multipart/form-data</c>
    /// envelope). MUST stay byte-identical to <c>RAW_BODY_FIELD</c> in
    /// <c>shared-core-rs/crates/hw-net/src/providers/hyperwhisper_cloud.rs</c>:
    /// <code>pub const RAW_BODY_FIELD: &amp;str = "@raw";</code>
    ///
    /// HyperWhisper Cloud + the routed (Azure-MAI / Google-Chirp) providers encode
    /// their raw-streamed upload as <c>Body.Multipart</c> carrying exactly one
    /// <c>HwPart.FileRef(field: "@raw", …)</c>. We detect that shape and stream the
    /// file as the bare request body, with <c>Content-Type = fileRef.mime</c>.
    /// </summary>
    internal const string RawBodyField = "@raw";

    /// <summary>
    /// Perform <paramref name="request"/> over <paramref name="client"/> and
    /// capture an <see cref="HttpResponse"/> for the core to parse.
    /// </summary>
    /// <remarks>
    /// Throws <see cref="OperationCanceledException"/> when the token is cancelled.
    /// Transport errors (<see cref="HttpRequestException"/>, timeout
    /// <see cref="TaskCanceledException"/>) propagate untranslated so the retry
    /// wrapper can classify them.
    /// </remarks>
    internal static async Task<HttpResponse> ExecuteAsync(
        HttpRequest request,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Build the message + content together so the content (and any underlying
        // FileStream — multipart file parts, @raw raw-stream, .FileStream) is
        // disposed with the request after SendAsync completes. No temp envelope is
        // written to disk: MultipartFormDataContent streams each file part live.
        using var message = BuildRequestMessage(request);

        using var response = await client.SendAsync(
            message,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        var body = await response.Content
            .ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        return new HttpResponse(
            @status: (ushort)(int)response.StatusCode,
            @headers: CaptureHeaders(response),
            @body: body);
    }

    // MARK: - Request construction

    /// <summary>
    /// Build the <see cref="HttpRequestMessage"/>: URL, method, all core headers,
    /// and the body (dispatched by the four <see cref="Body"/> cases).
    /// </summary>
    private static HttpRequestMessage BuildRequestMessage(HttpRequest request)
    {
        var message = new HttpRequestMessage(MapMethod(request.@method), request.@url);

        // Attach the body content FIRST so per-content Content-Type from the core
        // headers below can override (or coexist with) the body's content type.
        switch (request.@body)
        {
            case Body.Empty:
                // No content.
                break;

            case Body.Bytes bytes:
            {
                var content = new ByteArrayContent(bytes.@data);
                TrySetContentType(content, bytes.@contentType);
                message.Content = content;
                break;
            }

            case Body.FileStream fileStream:
            {
                // Raw file body streamed from disk — audio is never buffered into
                // memory across FFI. The StreamContent owns the FileStream; both
                // are disposed when `message` is disposed after SendAsync.
                var stream = OpenFileForStreaming(fileStream.@path);
                var content = new StreamContent(stream);
                TrySetContentType(content, fileStream.@contentType);
                message.Content = content;
                break;
            }

            case Body.Multipart multipart:
            {
                var rawFile = RawStreamFileRef(multipart.@parts);
                if (rawFile != null)
                {
                    // === @raw SENTINEL PATH (HW Cloud / routed) ===
                    // A single FileRef whose field == "@raw" means: stream this
                    // file as the RAW request body, NOT a multipart envelope. The
                    // Content-Type is the fileRef's own mime.
                    var stream = OpenFileForStreaming(rawFile.@path);
                    var content = new StreamContent(stream);
                    TrySetContentType(content, rawFile.@mime);
                    message.Content = content;
                }
                else
                {
                    // === REAL multipart/form-data PATH ===
                    // (OpenAI / Groq / ElevenLabs / Mistral) Assemble the envelope
                    // from the core-provided parts, in order, using the
                    // core-provided boundary. File parts are streamed from disk so
                    // audio bytes still never cross FFI.
                    message.Content = BuildMultipartContent(multipart.@boundary, multipart.@parts);
                }
                break;
            }
        }

        // Apply every core-provided header verbatim, in order. Headers that belong
        // on the content (Content-Type) are routed to the content; everything else
        // goes on the request. This matches the macOS executor, which sets every
        // header on the URLRequest (URLSession routes content headers internally).
        foreach (var header in request.@headers)
        {
            ApplyHeader(message, header.@name, header.@value);
        }

        return message;
    }

    /// <summary>
    /// Detect the <c>@raw</c> sentinel: a multipart body with exactly ONE part
    /// that is a <c>FileRef</c> whose field name equals <see cref="RawBodyField"/>.
    /// Returns the <c>FileRef</c> when matched, else null.
    ///
    /// This is the load-bearing branch the verifier must confirm — getting it
    /// wrong wraps the audio in a multipart envelope and the backend 400s /
    /// transcribes garbage.
    /// </summary>
    private static HwPart.FileRef? RawStreamFileRef(List<HwPart> parts)
    {
        if (parts.Count == 1 && parts[0] is HwPart.FileRef fileRef && fileRef.@field == RawBodyField)
        {
            return fileRef;
        }
        return null;
    }

    /// <summary>
    /// Assemble a streamed <c>multipart/form-data</c> body. File parts are streamed
    /// from disk via <see cref="StreamContent"/>; field parts are inline strings.
    /// Parts are added in the core-provided order with the core-provided boundary.
    /// </summary>
    private static MultipartFormDataContent BuildMultipartContent(string boundary, List<HwPart> parts)
    {
        var content = new MultipartFormDataContent(boundary);
        foreach (var part in parts)
        {
            switch (part)
            {
                case HwPart.Field field:
                    content.Add(new StringContent(field.@value), field.@name);
                    break;

                case HwPart.FileRef fileRef:
                {
                    var stream = OpenFileForStreaming(fileRef.@path);
                    var fileContent = new StreamContent(stream);
                    if (MediaTypeHeaderValue.TryParse(fileRef.@mime, out var mediaType))
                    {
                        fileContent.Headers.ContentType = mediaType;
                    }
                    content.Add(fileContent, fileRef.@field, fileRef.@filename);
                    break;
                }
            }
        }
        return content;
    }

    /// <summary>
    /// Open an audio file for async streaming uploads. Read-shared so a concurrent
    /// cleanup reader/size-probe doesn't fight the upload.
    /// </summary>
    private static FileStream OpenFileForStreaming(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
    }

    /// <summary>
    /// Apply a core-provided header to the request, routing content headers
    /// (Content-Type) to the content object as required by
    /// <see cref="HttpRequestMessage"/>.
    /// </summary>
    private static void ApplyHeader(HttpRequestMessage message, string name, string value)
    {
        // Content-Type must live on the content. The core may emit it as a header
        // (e.g. the @raw multipart's audio mime); route it to the content.
        if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
        {
            if (message.Content != null && MediaTypeHeaderValue.TryParse(value, out var mediaType))
            {
                message.Content.Headers.ContentType = mediaType;
            }
            return;
        }

        // Try request headers first; fall back to content headers (e.g.
        // Content-Length, Content-Disposition) when the framework rejects them as
        // content-only. TryAddWithoutValidation is permissive on header names the
        // backend defines (X-STT-*, xi-api-key, Authorization).
        if (!message.Headers.TryAddWithoutValidation(name, value))
        {
            message.Content?.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static void TrySetContentType(HttpContent content, string contentType)
    {
        if (MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
        {
            content.Headers.ContentType = mediaType;
        }
    }

    /// <summary>Map the binding <see cref="RustHttpMethod"/> to a framework verb.</summary>
    private static System.Net.Http.HttpMethod MapMethod(RustHttpMethod method) => method switch
    {
        RustHttpMethod.Get => System.Net.Http.HttpMethod.Get,
        RustHttpMethod.Post => System.Net.Http.HttpMethod.Post,
        RustHttpMethod.Put => System.Net.Http.HttpMethod.Put,
        RustHttpMethod.Delete => System.Net.Http.HttpMethod.Delete,
        _ => System.Net.Http.HttpMethod.Post
    };

    /// <summary>
    /// Flatten an <see cref="HttpResponseMessage"/>'s response AND content headers
    /// into the binding <see cref="Header"/> list. Response headers must pass
    /// through — HW Cloud / routed / Gemini read response headers (X-Goog-Upload-*,
    /// Retry-After, credit balances). The core's lookup is case-insensitive.
    /// </summary>
    private static List<Header> CaptureHeaders(HttpResponseMessage response)
    {
        var headers = new List<Header>();
        foreach (var kvp in response.Headers)
        {
            headers.Add(new Header(@name: kvp.Key, @value: string.Join(",", kvp.Value)));
        }
        if (response.Content != null)
        {
            foreach (var kvp in response.Content.Headers)
            {
                headers.Add(new Header(@name: kvp.Key, @value: string.Join(",", kvp.Value)));
            }
        }
        return headers;
    }
}
