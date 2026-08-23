using System.Text;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Injection;

internal sealed record AtSpiFocusInfo(string Identity, SecureFieldState SecureState);

internal interface IAtSpiFocusQuery
{
    bool IsAvailable { get; }
    ValueTask<AtSpiFocusInfo?> GetFocusedAsync(CancellationToken cancellationToken);
}

internal sealed class AtSpiSecureFieldGuard : ISecureFieldGuard
{
    private readonly IAtSpiFocusQuery _query;
    public AtSpiSecureFieldGuard() : this(new PythonAtSpiFocusQuery()) { }
    internal AtSpiSecureFieldGuard(IAtSpiFocusQuery query) => _query = query;
    public bool IsAvailable => _query.IsAvailable;
    public async ValueTask<SecureFieldState> GetFocusedFieldStateAsync(CancellationToken cancellationToken) =>
        (await _query.GetFocusedAsync(cancellationToken).ConfigureAwait(false))?.SecureState ?? SecureFieldState.Unknown;
}

internal sealed class PythonAtSpiFocusQuery : IAtSpiFocusQuery
{
    private const string ProbeScript = "import gi;gi.require_version('Atspi','2.0');from gi.repository import Atspi";
    private const string FocusScript = """
import gi
gi.require_version('Atspi','2.0')
from gi.repository import Atspi
try:
    stack=[(Atspi.get_desktop(0),())]
    seen=0
    while stack and seen < 10000:
        node,path=stack.pop(); seen+=1
        try:
            if node.get_state_set().contains(Atspi.StateType.FOCUSED):
                role=node.get_role()
                pid=node.get_process_id()
                secure='SECURE' if role == Atspi.Role.PASSWORD_TEXT else 'CLEAR'
                print('FOCUS|%s|%s|%s' % (pid,'.'.join(map(str,path)),secure))
                raise SystemExit(0)
            for i in range(node.get_child_count()-1,-1,-1):
                child=node.get_child_at_index(i)
                if child is not None: stack.append((child,path+(i,)))
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
            if (_python is null) return (_available = false).Value;
            try
            {
                var result = ExternalProcessRunner.RunAsync(_python, ["-c", ProbeScript], null,
                    CancellationToken.None).GetAwaiter().GetResult();
                return (_available = result.ExitCode == 0).Value;
            }
            catch { return (_available = false).Value; }
        }
    }

    public async ValueTask<AtSpiFocusInfo?> GetFocusedAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable || _python is null) return null;
        try
        {
            var result = await ExternalProcessRunner.RunAsync(_python, ["-c", FocusScript], null,
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0) return null;
            var parts = Encoding.UTF8.GetString(result.Output).Trim().Split('|');
            if (parts.Length != 4 || parts[0] != "FOCUS") return null;
            var state = parts[3] == "SECURE" ? SecureFieldState.Secure
                : parts[3] == "CLEAR" ? SecureFieldState.NotSecure : SecureFieldState.Unknown;
            return new AtSpiFocusInfo($"{parts[1]}:{parts[2]}", state);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
    }
}

internal sealed class AtSpiCapturedTargetService : ICapturedTargetService
{
    private readonly IAtSpiFocusQuery _query;
    public AtSpiCapturedTargetService() : this(new PythonAtSpiFocusQuery()) { }
    internal AtSpiCapturedTargetService(IAtSpiFocusQuery query) => _query = query;
    public bool CanRestoreFocus => _query.IsAvailable;

    public PlatformResult<CapturedTarget?> Capture()
    {
        try
        {
            var focused = _query.GetFocusedAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return focused is null
                ? PlatformResult<CapturedTarget?>.Failure("target_capture_failed", "The focused accessible could not be captured.")
                : PlatformResult<CapturedTarget?>.Success(new CapturedTarget(focused.Identity));
        }
        catch { return PlatformResult<CapturedTarget?>.Failure("target_capture_failed", "The focused accessible could not be captured."); }
    }

    public async ValueTask<TargetFocusState> ValidateAndFocusAsync(CapturedTarget target, CancellationToken cancellationToken)
    {
        var focused = await _query.GetFocusedAsync(cancellationToken).ConfigureAwait(false);
        return focused is null ? TargetFocusState.Lost
            : focused.Identity == target.OpaqueId ? TargetFocusState.Ready : TargetFocusState.Changed;
    }
}
