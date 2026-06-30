namespace HyperWhisper.Models;

public enum SmartPasteResult
{
    Pasted,              // Successfully pasted into focused field
    CopiedToClipboard,   // No text field focused — copied to clipboard only
    SecureFieldSkipped,  // Password field detected — copied to clipboard only
    Failed               // Paste simulation failed
}
