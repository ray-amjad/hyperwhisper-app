using System;
using System.IO;
using System.Net.Sockets;

namespace HyperWhisper.Services.LocalApi;

internal static class LocalApiBindFallback
{
    public static bool ShouldRetryWithEphemeral(Exception exception, int preferredPort)
    {
        if (preferredPort == 0)
        {
            return false;
        }

        return IsPreferredPortBindFailure(exception);
    }

    public static string Describe(Exception exception)
    {
        var ioException = exception as IOException;
        if (ioException != null && ioException.InnerException != null)
        {
            return ioException.InnerException.Message;
        }

        return exception.Message;
    }

    private static bool IsPreferredPortBindFailure(Exception exception)
    {
        // The preferred port is only an optimization. Any socket-level failure
        // while starting Kestrel on that port can be recovered by retrying on
        // an ephemeral port instead.
        var socketException = exception as SocketException;
        if (socketException != null || exception.InnerException is SocketException)
        {
            return true;
        }

        return exception.Message.IndexOf("address already in use", StringComparison.OrdinalIgnoreCase) >= 0
            || exception.Message.IndexOf("access permissions", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
