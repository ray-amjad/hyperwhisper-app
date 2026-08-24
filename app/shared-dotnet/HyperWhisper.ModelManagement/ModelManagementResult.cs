namespace HyperWhisper.ModelManagement;

public enum ModelManagementError
{
    InvalidRequest,
    NotFound,
    Network,
    Cancelled,
    Validation,
    Storage
}

public sealed record ModelManagementFailure(ModelManagementError Code, string Message);

public sealed record ModelManagementResult<T>(T? Value, ModelManagementFailure? Failure)
{
    public bool IsSuccess => Failure is null;
    public static ModelManagementResult<T> Success(T value) => new(value, null);
    public static ModelManagementResult<T> Fail(ModelManagementError code, string message) =>
        new(default, new(code, message));
}

public sealed record ModelDownloadProgress(string ModelId, long BytesReceived, long? TotalBytes, double? Fraction);
