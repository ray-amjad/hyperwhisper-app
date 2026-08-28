using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using uniffi.hyperwhisper_core;
using RustHttpMethod = uniffi.hyperwhisper_core.HttpMethod;

namespace HyperWhisper.SharedCore;

internal static class RustHttpTransport
{
    internal const string RawBodyField = "@raw";

    internal static async Task<HttpResponse> ExecuteAsync(
        HttpRequest request,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var message = BuildRequestMessage(request);
        using var response = await client.SendAsync(
            message,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return new HttpResponse((ushort)(int)response.StatusCode, CaptureHeaders(response), body);
    }

    /// <summary>
    /// Materialize a Rust-built request. Internal rather than private because
    /// <see cref="LlmPostProcessing"/> hands the message back to a caller that
    /// owns its own send loop (timeout, retry, response-size cap) instead of
    /// using <see cref="ExecuteAsync"/>.
    /// </summary>
    internal static HttpRequestMessage BuildRequestMessage(HttpRequest request)
    {
        var message = new HttpRequestMessage(MapMethod(request.@method), request.@url);
        switch (request.@body)
        {
            case Body.Empty:
                break;
            case Body.Bytes bytes:
                message.Content = new ByteArrayContent(bytes.@data);
                TrySetContentType(message.Content, bytes.@contentType);
                break;
            case Body.FileStream file:
                message.Content = new StreamContent(OpenFile(file.@path));
                TrySetContentType(message.Content, file.@contentType);
                break;
            case Body.Multipart multipart:
                var raw = multipart.@parts.Count == 1
                    ? multipart.@parts[0] as HwPart.FileRef
                    : null;
                if (raw?.@field == RawBodyField)
                {
                    message.Content = new StreamContent(OpenFile(raw.@path));
                    TrySetContentType(message.Content, raw.@mime);
                }
                else
                {
                    message.Content = BuildMultipart(multipart.@boundary, multipart.@parts);
                }
                break;
            case Body.JsonWithBase64File json:
                message.Content = BuildJsonWithBase64File(json);
                TrySetContentType(message.Content, "application/json");
                break;
            default:
                // This switch is the whole body of the request. C# switches are
                // not exhaustive, so a new Body variant that lands here silently
                // sends a BODY-LESS request — a 400 from the vendor with no
                // clue why. Fail loudly instead.
                throw new NotSupportedException(
                    $"Unhandled Rust request body variant: {request.@body.GetType().Name}");
        }

        foreach (var header in request.@headers)
        {
            ApplyHeader(message, header.@name, header.@value);
        }
        return message;
    }

    private static MultipartFormDataContent BuildMultipart(string boundary, List<HwPart> parts)
    {
        var content = new MultipartFormDataContent(boundary);
        foreach (var part in parts)
        {
            switch (part)
            {
                case HwPart.Field field:
                    content.Add(new StringContent(field.@value), field.@name);
                    break;
                case HwPart.FileRef file:
                    var fileContent = new StreamContent(OpenFile(file.@path));
                    TrySetContentType(fileContent, file.@mime);
                    content.Add(fileContent, file.@field, file.@filename);
                    break;
            }
        }
        return content;
    }

    /// <summary>
    /// Materialize a <see cref="Body.JsonWithBase64File"/>: the literal
    /// <c>prefix</c> bytes, then the standard base64 of the file at
    /// <c>path</c>, then the literal <c>suffix</c> bytes.
    ///
    /// The audio is base64-encoded **as it is written to the socket** rather
    /// than buffered — a 14 MB recording would otherwise cost 14 MB for the file
    /// plus ~19 MB for its encoding, held at once, on every transcription.
    /// <see cref="ToBase64Transform"/> emits the standard padded alphabet with
    /// no line breaks, which is what the vendor requires.
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

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(_prefix).ConfigureAwait(false);
            await using (var file = OpenFile(_path))
            {
                var encoder = new CryptoStream(stream, new ToBase64Transform(), CryptoStreamMode.Write, leaveOpen: true);
                await using (encoder.ConfigureAwait(false))
                {
                    await file.CopyToAsync(encoder).ConfigureAwait(false);
                    // Emits the padding for a trailing partial group. Explicit
                    // rather than left to Dispose so it provably happens before
                    // the suffix is written.
                    await encoder.FlushFinalBlockAsync().ConfigureAwait(false);
                }
            }
            await stream.WriteAsync(_suffix).ConfigureAwait(false);
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
                // Fall back to chunked transfer rather than failing here; the
                // send itself will surface a real error if the file is gone.
                return false;
            }
            // Base64 is 4 output bytes per 3 input bytes, padded up.
            length = _prefix.LongLength + ((fileBytes + 2) / 3) * 4 + _suffix.LongLength;
            return true;
        }
    }

    private static HttpContent BuildJsonWithBase64File(Body.JsonWithBase64File body) =>
        new JsonWithBase64FileContent(body.@prefix, body.@path, body.@suffix);

    private static FileStream OpenFile(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        81920,
        useAsync: true);

    private static void ApplyHeader(HttpRequestMessage message, string name, string value)
    {
        if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
        {
            if (message.Content is not null)
            {
                TrySetContentType(message.Content, value);
            }
            return;
        }
        if (!message.Headers.TryAddWithoutValidation(name, value))
        {
            message.Content?.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static void TrySetContentType(HttpContent content, string contentType)
    {
        if (MediaTypeHeaderValue.TryParse(contentType, out var parsed))
        {
            content.Headers.ContentType = parsed;
        }
    }

    private static System.Net.Http.HttpMethod MapMethod(RustHttpMethod method) => method switch
    {
        RustHttpMethod.Get => System.Net.Http.HttpMethod.Get,
        RustHttpMethod.Post => System.Net.Http.HttpMethod.Post,
        RustHttpMethod.Put => System.Net.Http.HttpMethod.Put,
        RustHttpMethod.Delete => System.Net.Http.HttpMethod.Delete,
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    private static List<Header> CaptureHeaders(HttpResponseMessage response)
    {
        var result = response.Headers
            .Select(value => new Header(value.Key, string.Join(",", value.Value)))
            .ToList();
        result.AddRange(response.Content.Headers
            .Select(value => new Header(value.Key, string.Join(",", value.Value))));
        return result;
    }
}
