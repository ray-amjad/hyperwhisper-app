using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace HyperWhisper.LocalApi;

/// <summary>
/// The two guards <see cref="PortableLocalApi.Map"/> puts in front of every
/// route (issue #940). Register them in this order: the rebind guard, then the
/// bearer check.
/// </summary>
internal static class PortableLocalApiSecurity
{
    // DNS-rebind guard — runs before EVERY route, including the
    // unauthenticated /health, and before the bearer check. This head had
    // no such guard until issue #289; this is macOS's, shared through
    // hw-localapi rather than transliterated a third time. The guard runs
    // first on purpose: checking the token first would tell an
    // unauthenticated rebound page whether its guess was right.
    internal static void UseRebindGuard(WebApplication app, PortableLocalApiOptions options)
    {
        app.Use(async (context, next) =>
        {
            if (!LocalApiOriginGuard.IsAllowed(context, LocalApiOriginGuard.ResolvePort(context, options)))
            {
                await LocalApiSharedFailure.WriteForbiddenOriginAsync(context);
                options.OriginRejected?.Invoke(context);
                return;
            }
            await next(context);
        });
    }

    // The error envelope wraps only what runs after a passed bearer check: the
    // head's request gate and the route. /health is outside it, as before.
    internal static void UseBearerAuth(WebApplication app, PortableLocalApiOptions options)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }
            // The header parse moved into the shared core with the compare
            // (issue #289): this head used to match the `Bearer ` prefix here
            // and hash only the remainder, which is one of the three ways the
            // platforms disagreed.
            if (!LocalApiTokenStore.Authorize(context.Request.Headers.Authorization.ToString(), options.Token))
            {
                await LocalApiSharedFailure.WriteUnauthorizedAsync(context);
                return;
            }
            await PortableLocalApiErrors.RunWithErrorEnvelopeAsync(context, async () =>
            {
                if (options.AuthorizedRequestGate is { } gate && !await gate(context)) return;
                await next(context);
            });
        });
    }
}
