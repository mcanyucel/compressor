namespace Core.Models;

public sealed class JobItemError
{
    public required string Message { get; init; }
    public Exception? Exception { get; init; }
}
