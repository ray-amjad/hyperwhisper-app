namespace HyperWhisper.WhisperWorker;

/// <summary>
/// A Whisper worker request that failed. <see cref="Code"/> is one of
/// <see cref="WhisperWorkerErrorCodes"/>: the worker's own code, or the one the
/// client raised (crashed, start_failed, timeout).
/// </summary>
public sealed class WhisperWorkerException : Exception
{
    public WhisperWorkerException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }

    /// <summary>True when a new download of the model file can fix this failure (#1598).</summary>
    public bool IsModelFileFault => Code == WhisperWorkerErrorCodes.ModelFile;
}
