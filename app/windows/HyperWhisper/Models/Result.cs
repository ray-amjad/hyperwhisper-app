// RESULT<T> PATTERN - Explicit Error Handling
// Forces callers to handle both success and failure cases explicitly.
// Prevents silent failures and makes error handling visible in the code.
//
// USAGE:
// - Return Result<T> instead of throwing exceptions for expected failures
// - Use Match() to handle both cases, or check IsSuccess/IsFailure
// - Provides Error message and optional Exception for diagnostics
//
// EXAMPLE:
// var result = service.DoSomething();
// result.Match(
//     onSuccess: value => ProcessValue(value),
//     onFailure: error => ShowError(error)
// );

namespace HyperWhisper.Models;

/// <summary>
/// Represents the result of an operation that can succeed or fail.
/// Forces callers to explicitly handle both cases.
/// </summary>
/// <typeparam name="T">The type of the success value.</typeparam>
public readonly struct Result<T>
{
    /// <summary>
    /// True if the operation succeeded and Value is available.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// True if the operation failed and Error is available.
    /// </summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// The success value. Only valid when IsSuccess is true.
    /// </summary>
    public T? Value { get; }

    /// <summary>
    /// The error message. Only valid when IsFailure is true.
    /// </summary>
    public string? Error { get; }

    /// <summary>
    /// The exception that caused the failure, if any.
    /// </summary>
    public Exception? Exception { get; }

    private Result(T value)
    {
        IsSuccess = true;
        Value = value;
        Error = null;
        Exception = null;
    }

    private Result(string error, Exception? exception = null)
    {
        IsSuccess = false;
        Value = default;
        Error = error;
        Exception = exception;
    }

    /// <summary>
    /// Creates a successful result with the given value.
    /// </summary>
    public static Result<T> Success(T value) => new(value);

    /// <summary>
    /// Creates a failed result with the given error message.
    /// </summary>
    public static Result<T> Failure(string error) => new(error);

    /// <summary>
    /// Creates a failed result from an exception.
    /// </summary>
    public static Result<T> Failure(Exception ex) => new(ex.Message, ex);

    /// <summary>
    /// Creates a failed result with a custom message and exception.
    /// </summary>
    public static Result<T> Failure(string error, Exception ex) => new(error, ex);

    /// <summary>
    /// Execute action based on success/failure. Ensures both cases are handled.
    /// </summary>
    /// <param name="onSuccess">Action to execute if operation succeeded.</param>
    /// <param name="onFailure">Action to execute if operation failed.</param>
    public void Match(Action<T> onSuccess, Action<string> onFailure)
    {
        if (IsSuccess)
            onSuccess(Value!);
        else
            onFailure(Error!);
    }

    /// <summary>
    /// Transform the result value if successful, or propagate the error.
    /// </summary>
    public Result<TNew> Map<TNew>(Func<T, TNew> mapper)
    {
        if (IsSuccess)
            return Result<TNew>.Success(mapper(Value!));

        return Exception != null
            ? Result<TNew>.Failure(Error!, Exception)
            : Result<TNew>.Failure(Error!);
    }

    /// <summary>
    /// Get the value or a default if failed.
    /// </summary>
    public T GetValueOrDefault(T defaultValue = default!)
    {
        return IsSuccess ? Value! : defaultValue;
    }

    /// <summary>
    /// Get the value or throw the exception if failed.
    /// </summary>
    public T GetValueOrThrow()
    {
        if (IsSuccess)
            return Value!;

        if (Exception != null)
            throw Exception;

        throw new InvalidOperationException(Error);
    }
}

/// <summary>
/// Represents the result of an operation that has no return value.
/// Use this for void operations that can fail.
/// </summary>
public readonly struct Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public string? Error { get; }
    public Exception? Exception { get; }

    private Result(bool success, string? error = null, Exception? exception = null)
    {
        IsSuccess = success;
        Error = error;
        Exception = exception;
    }

    public static Result Success() => new(true);
    public static Result Failure(string error) => new(false, error);
    public static Result Failure(Exception ex) => new(false, ex.Message, ex);
    public static Result Failure(string error, Exception ex) => new(false, error, ex);

    public void Match(Action onSuccess, Action<string> onFailure)
    {
        if (IsSuccess)
            onSuccess();
        else
            onFailure(Error!);
    }
}
