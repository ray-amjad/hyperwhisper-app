using HyperWhisper.Models;
using PlatformContracts = HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Services.Platform;

internal static class WindowsTextInjectionMapper
{
    internal static PlatformContracts.TextInjectionOutcome ToPlatform(SmartPasteResult result)
        => result switch
        {
            SmartPasteResult.Pasted => PlatformContracts.TextInjectionOutcome.Pasted,
            SmartPasteResult.CopiedToClipboard =>
                PlatformContracts.TextInjectionOutcome.CopiedToClipboard,
            SmartPasteResult.SecureFieldSkipped =>
                PlatformContracts.TextInjectionOutcome.SecureFieldSkipped,
            SmartPasteResult.Failed => PlatformContracts.TextInjectionOutcome.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, null)
        };
}
