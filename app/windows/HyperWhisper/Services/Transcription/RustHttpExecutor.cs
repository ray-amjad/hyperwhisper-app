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
// reused by EVERY cloud STT provider, so the five `Body` cases below must be
// exactly right — every later provider inherits any bug here. It mirrors the
// already-shipped macOS `RustHTTPExecutor.swift` 1:1.
//
// The one exception to "audio never crosses the boundary" is
// `Body.JsonWithBase64File` (Gemini 3.5 Transcribe): that vendor demands the
// audio inline in the JSON, so the PLATFORM reads and base64-encodes it here.
// Rust still only ever sees the path.
//
// TODO-verify (Windows/CI): Rust shared-core swap — compile-only on macOS; not
// built against the C# binding here. Verify under `dotnet build` in CI.

using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
    /// and the body (dispatched by the five <see cref="Body"/> cases).
    /// </summary>
    /// <remarks>
    /// internal (not private): test seam for HyperWhisper.SmokeTests via
    /// InternalsVisibleTo (see HyperWhisper.csproj). The inline-base64 body is
    /// produced lazily as it is written to the socket, so the only way to prove
    /// the bytes are what the vendor expects is to build the message and read its
    /// content back.
    /// </remarks>
    internal static HttpRequestMessage BuildRequestMessage(HttpRequest request)
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

            case Body.JsonWithBase64File json:
            {
                // === INLINE BASE64 JSON PATH (Gemini 3.5 Transcribe) ===
                // The vendor's /v1beta/interactions endpoint has no file-reference
                // form, so the audio must sit inside the JSON. The core hands us
                // two literal JSON fragments and a path; the body is
                // prefix ++ base64(file) ++ suffix.
                //
                // STREAMED, not buffered. The obvious version — ReadAllBytes, then
                // ToBase64String, then a concatenated array — holds the file, its
                // base64, and the joined result live at once: measured 88.7 MB of
                // large-object heap for a 14 MB recording, which is the provider's
                // own cap, and paid again on every RustRetry attempt (up to 8).
                // JsonWithBase64FileContent encodes as it writes to the socket
                // instead, matching the shared-.NET twin (RustHttpTransport) and
                // macOS's chunked RustHTTPExecutor. The file read is async there
                // and runs under SendAsync, so it also honours the attempt's
                // cancellation token, which File.ReadAllBytes on this thread did
                // not.
                //
                // C# `switch` over the binding's Body records is NOT exhaustive:
                // omitting this case would send a body-less POST and the vendor
                // would 400. There is no compiler check here — see the default arm.
                var content = new JsonWithBase64FileContent(json.@prefix, json.@path, json.@suffix);
                TrySetContentType(content, "application/json");
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

            default:
                // This switch IS the request body. C# switches over the binding's
                // Body records are not exhaustive, so a variant added to the core
                // and not handled here would fall straight through and send a
                // BODY-LESS request — a bare 400 from the vendor with nothing
                // naming the cause. This repo has been bitten by that shape
                // before. Fail loudly instead; the shared-.NET twin
                // (HyperWhisper.SharedCore/RustHttpTransport) has the same arm.
                throw new NotSupportedException(
                    $"Unhandled Rust request body variant: {request.@body.GetType().Name}");
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
    /// A <see cref="Body.JsonWithBase64File"/> body written as
    /// <c>prefix ++ base64(file) ++ suffix</c>, encoded straight onto the request
    /// stream instead of assembled in memory.
    ///
    /// The prefix/suffix are already valid JSON fragments emitted by the core;
    /// base64 is ASCII by construction, so a byte-level splice cannot corrupt
    /// either side's escaping. <see cref="ToBase64Transform"/> emits the standard
    /// padded, non-URL-safe alphabet with no line breaks — exactly what
    /// <see cref="Convert.ToBase64String"/> produced before, and what the core's
    /// fragments assume.
    ///
    /// Mirrors <c>JsonWithBase64FileContent</c> in
    /// <c>app/shared-dotnet/HyperWhisper.SharedCore/RustHttpTransport.cs</c>.
    /// </summary>
    private sealed class JsonWithBase64FileContent : HttpContent
    {
        private readonly byte[] _prefix;
        private readonly string _path;
        private readonly byte[] _suffix;

        internal JsonWithBase64FileContent(byte[] prefix, string path, byte[] suffix)
        {
            _prefix = prefix;
            _path = path;
            _suffix = suffix;
        }

        // HttpClient calls the cancellable overload, so the token that reaches
        // here is the one ExecuteAsync was given — which is how the file read
        // finally honours a cancelled retry attempt.
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            WriteAsync(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) => WriteAsync(stream, cancellationToken);

        private async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
        {
            await stream.WriteAsync(_prefix, cancellationToken).ConfigureAwait(false);

            await using (var file = OpenFileForStreaming(_path))
            {
                var encoder = new CryptoStream(stream, new ToBase64Transform(), CryptoStreamMode.Write, leaveOpen: true);
                await using (encoder.ConfigureAwait(false))
                {
                    await file.CopyToAsync(encoder, cancellationToken).ConfigureAwait(false);
                    // Emits the padding for a trailing partial group. Explicit
                    // rather than left to Dispose so it provably happens before
                    // the suffix is written.
                    await encoder.FlushFinalBlockAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await stream.WriteAsync(_suffix, cancellationToken).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            long fileBytes;
            try
            {
                fileBytes = new FileInfo(_path).Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Fall back to chunked transfer rather than failing here; the send
                // itself will surface a real error if the file is gone.
                return false;
            }

            // Base64 is 4 output bytes per 3 input bytes, padded up.
            length = _prefix.LongLength + ((fileBytes + 2) / 3) * 4 + _suffix.LongLength;
            return true;
        }
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
