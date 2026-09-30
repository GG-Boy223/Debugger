namespace DogeDebugger.Core.Process;

public sealed class RemoteOperationResult
{
    public bool Succeeded { get; init; }

    public ulong ReturnValue { get; init; }

    public string ErrorMessage { get; init; } = string.Empty;

    public TimeSpan Elapsed { get; init; }

    public static RemoteOperationResult Success(ulong returnValue, TimeSpan elapsed) =>
        new()
        {
            Succeeded = true,
            ReturnValue = returnValue,
            Elapsed = elapsed
        };

    public static RemoteOperationResult Failure(string errorMessage, TimeSpan elapsed) =>
        new()
        {
            Succeeded = false,
            ErrorMessage = errorMessage,
            Elapsed = elapsed
        };
}
