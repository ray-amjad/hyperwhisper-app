using System.Net.Http.Headers;
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

    private static HttpRequestMessage BuildRequestMessage(HttpRequest request)
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
