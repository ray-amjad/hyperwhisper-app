using System;

namespace HyperWhisper.Models;

/// <summary>
/// Event arguments for showing file transcription progress.
/// Used by MainViewModel to communicate file name and cancel handler to the progress window.
/// </summary>
public class FileTranscriptionProgressEventArgs : EventArgs
{
    /// <summary>
    /// The name of the file being transcribed.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Callback invoked when user clicks cancel button.
    /// </summary>
    public Action OnCancel { get; }

    public FileTranscriptionProgressEventArgs(string fileName, Action onCancel)
    {
        FileName = fileName;
        OnCancel = onCancel;
    }
}
