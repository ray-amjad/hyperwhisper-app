using System.Text;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Linux.Platform.Injection;

/// <summary>
/// Best-effort AT-SPI caret classifier. The companion process reads at most
/// 64 characters before the caret, classifies them in-process, and emits only
/// START/MID/UNKNOWN.
/// No target text crosses the process boundary or is retained by this service.
/// </summary>
public sealed class AtSpiInsertionContextProvider : IInsertionContextProvider
{
    private const string CursorScript = """
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
                if node.get_role() == Atspi.Role.PASSWORD_TEXT:
                    print('UNKNOWN'); raise SystemExit(0)
                text=node.query_text()
                caret=text.get_caret_offset()
                if caret == 0:
                    print('START'); raise SystemExit(0)
                if caret > 0:
                    preceding=text.get_text(max(0,caret-64),caret)[-64:]
                    result='START'
                    for character in reversed(preceding):
                        if character in '\n\r': result='START'; break
                        if character.isspace(): continue
                        result='START' if character in '.!?…¡¿;' else 'MID'
                        break
                    print(result); raise SystemExit(0)
                print('UNKNOWN'); raise SystemExit(0)
            for i in range(node.get_child_count()-1,-1,-1):
                child=node.get_child_at_index(i)
                if child is not None: stack.append(child)
        except Exception:
            pass
except Exception:
    pass
print('UNKNOWN')
""";

    private readonly string? _python = CommandClipboardBackend.FindExecutable("python3");

    public async ValueTask<InsertionCursorContext> GetCursorContextAsync(
        CancellationToken cancellationToken = default)
    {
        if (_python is null) return InsertionCursorContext.Unknown;
        try
        {
            var result = await ExternalProcessRunner.RunAsync(
                _python, ["-c", CursorScript], null, cancellationToken,
                timeout: TimeSpan.FromSeconds(1), maximumOutputBytes: 16).ConfigureAwait(false);
            if (result.ExitCode != 0) return InsertionCursorContext.Unknown;
            return ParseClassificationOutput(result.Output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return InsertionCursorContext.Unknown; }
    }

    internal static InsertionCursorContext ClassifyPreceding(int caretOffset, ReadOnlySpan<char> precedingText)
    {
        if (caretOffset == 0) return InsertionCursorContext.StartOfSentence;
        if (caretOffset < 0) return InsertionCursorContext.Unknown;
        var start = Math.Max(0, precedingText.Length - 64);
        for (var index = precedingText.Length - 1; index >= start; index--)
        {
            var character = precedingText[index];
            if (character is '\n' or '\r') return InsertionCursorContext.StartOfSentence;
            if (char.IsWhiteSpace(character)) continue;
            return character is '.' or '!' or '?' or '…' or '¡' or '¿' or ';'
                ? InsertionCursorContext.StartOfSentence
                : InsertionCursorContext.MidSentence;
        }
        return InsertionCursorContext.StartOfSentence;
    }

    internal static InsertionCursorContext ParseClassificationOutput(ReadOnlySpan<byte> output) =>
        Encoding.UTF8.GetString(output).Trim() switch
        {
            "START" => InsertionCursorContext.StartOfSentence,
            "MID" => InsertionCursorContext.MidSentence,
            "UNKNOWN" => InsertionCursorContext.Unknown,
            _ => InsertionCursorContext.Unknown,
        };

}
