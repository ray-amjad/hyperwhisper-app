using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace HyperWhisper.LocalApi;

/// <summary>
/// Maps an exception thrown past the bearer check onto the Local API error
/// envelope (issue #940 moved it out of <see cref="PortableLocalApi.Map"/>).
/// </summary>
internal static class PortableLocalApiErrors
{
    internal static async Task RunWithErrorEnvelopeAsync(HttpContext context, Func<Task> run)
    {
        try
        {
            await run();
        }
        // FIRST, and deliberately (issue #356). Everything below maps a CLR
        // exception TYPE onto one of two wire codes; this one carries the
        // envelope `hw-localapi` already decided, so a backend can ask for
        // any of the closed fourteen. `MODE_NAME_TAKEN` was declared on this
        // head and never emitted until it existed.
        catch (LocalApiFailureException failure)
        {
            if (context.Response.HasStarted) throw;
            context.Response.StatusCode = failure.HttpStatus;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(failure.Json);
        }
        catch (ArgumentException)
        {
            if (context.Response.HasStarted) throw;
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new LocalApiFailure(new(LocalApiErrorCodes.InvalidRequest, "The request contains invalid or conflicting values.")));
        }
        catch (BadHttpRequestException)
        {
            if (context.Response.HasStarted) throw;
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new LocalApiFailure(new(LocalApiErrorCodes.InvalidRequest, "The request body is invalid.")));
        }
        catch (JsonException)
        {
            if (context.Response.HasStarted) throw;
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new LocalApiFailure(new(LocalApiErrorCodes.InvalidRequest, "The request body is invalid JSON.")));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (context.Response.HasStarted) throw;
            // `CANCELLED` was never in the closed set, and 408 was never a
            // status the docs allowed for a business outcome (issue #289).
            // `TIMEOUT` is the documented code for "ran out of time".
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsJsonAsync(new LocalApiFailure(new(LocalApiErrorCodes.Timeout, "The request was cancelled.")));
        }
        catch (InvalidOperationException)
        {
            if (context.Response.HasStarted) throw;
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsJsonAsync(new LocalApiFailure(new(LocalApiErrorCodes.EngineUnavailable, "The requested application capability is unavailable.")));
        }
    }
}
