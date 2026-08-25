using HyperWhisper.AppClassification;
using PlatformContracts = HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.Services.Platform;

internal static class WindowsApplicationContextMapper
{
    internal static PlatformContracts.ApplicationContextSnapshot ToPlatform(
        ApplicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new PlatformContracts.ApplicationContextSnapshot
        {
            ProcessName = context.ProcessName,
            WindowTitle = context.WindowTitle,
            Category = context.Category,
            BrowserTabTitle = context.BrowserTabTitle,
            BrowserHost = context.BrowserHost,
            FocusedElementType = context.FocusedElementType,
            FocusedContent = context.FocusedContent,
            TextFormat = context.TextFormat,
            AppType = context.AppType.ToPromptValue(),
            AppTypeConfidence = context.AppTypeConfidence,
            AppTypeSource = context.AppTypeSource,
            ScreenOcrText = context.ScreenOCRText
        };
    }
}
