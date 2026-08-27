using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.PortableApplication.Transcription;

public static class TranscriptionTextDelivery
{
    public static async ValueTask<TextInjectionOutcome> DeliverAsync(
        ITextInjectionService textInjection,
        string text,
        bool pasteResultText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(textInjection);
        ArgumentNullException.ThrowIfNull(text);
        if (pasteResultText)
            return await textInjection.InjectTranscriptAsync(text, cancellationToken).ConfigureAwait(false);
        var copied = await textInjection.CopyToClipboardAsync(text, cancellationToken).ConfigureAwait(false);
        return copied.IsSuccess ? TextInjectionOutcome.CopiedToClipboard : TextInjectionOutcome.Failed;
    }
}
