using System.Diagnostics;

namespace HyperWhisper.Linux.Platform.Injection;

internal sealed class AtSpiSecureFieldGuard : ISecureFieldGuard
{
    private const string ProbeScript = "import gi;gi.require_version('Atspi','2.0');from gi.repository import Atspi";
    private const string FocusScript = """
import gi
gi.require_version('Atspi','2.0')
from gi.repository import Atspi
try:
    stack=[Atspi.get_desktop(0)]
    seen=0
    while stack and seen < 10000:
        node=stack.pop(); seen+=1
        try:
            if node.get_state_set().contains(Atspi.StateType.FOCUSED):
                role=node.get_role()
                print('SECURE' if role == Atspi.Role.PASSWORD_TEXT else 'CLEAR')
                raise SystemExit(0)
            for i in range(node.get_child_count()):
                child=node.get_child_at_index(i)
                if child is not None: stack.append(child)
        except Exception:
            pass
except Exception:
    pass
print('UNKNOWN')
""";

    private readonly string? _python = CommandClipboardBackend.FindExecutable("python3");
    private bool? _available;

    public bool IsAvailable
    {
        get
        {
            if (_available.HasValue) return _available.Value;
            var probe = _python is null ? (-1, string.Empty) : Run(ProbeScript, CancellationToken.None);
            return (_available = probe.Item1 == 0).Value;
        }
    }

    public async ValueTask<SecureFieldState> GetFocusedFieldStateAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable) return SecureFieldState.Unknown;
        var result = await Task.Run(() => Run(FocusScript, cancellationToken), cancellationToken).ConfigureAwait(false);
        return result.Output.Trim() switch
        {
            "SECURE" => SecureFieldState.Secure,
            "CLEAR" => SecureFieldState.NotSecure,
            _ => SecureFieldState.Unknown,
        };
    }

    private (int ExitCode, string Output) Run(string script, CancellationToken token)
    {
        if (_python is null) return (-1, string.Empty);
        try
        {
            var start = new ProcessStartInfo(_python) { UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add(script);
            using var process = Process.Start(start);
            if (process is null) return (-1, string.Empty);
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            process.WaitForExitAsync(token).GetAwaiter().GetResult();
            _ = error.GetAwaiter().GetResult();
            return (process.ExitCode, output.GetAwaiter().GetResult());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return (-1, string.Empty); }
    }
}
