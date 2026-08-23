namespace HyperWhisper.Platform.Abstractions;

/// <summary>
/// A stable, platform-neutral description of an expected platform failure.
/// Error codes are intended for branching and diagnostics; messages are for logs
/// and must not contain secret values or captured user content.
/// </summary>
public sealed record PlatformError(string Code, string Message);

/// <summary>Result of a platform operation that has no return value.</summary>
public sealed record PlatformResult
{
    private PlatformResult(bool isSuccess, PlatformError? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public PlatformError? Error { get; }

    public static PlatformResult Success() => new(true, null);

    public static PlatformResult Failure(string code, string message)
        => new(false, new PlatformError(code, message));
}

/// <summary>Result of a platform operation that returns a value.</summary>
public sealed record PlatformResult<T>
{
    private PlatformResult(bool isSuccess, T? value, PlatformError? error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public T? Value { get; }
    public PlatformError? Error { get; }

    public static PlatformResult<T> Success(T value) => new(true, value, null);

    public static PlatformResult<T> Failure(string code, string message)
        => new(false, default, new PlatformError(code, message));
}
