namespace SealedTransfer.Core.Transfers;

/// <summary>Outcome of an operation that returns nothing on success.</summary>
public readonly record struct Result(TransferError? Error, string? Detail = null)
{
    public bool IsSuccess => Error is null;

    public static Result Ok() => new(null);

    public static Result Fail(TransferError error, string? detail = null) => new(error, detail);
}

/// <summary>Outcome of an operation that returns a value on success.</summary>
public readonly record struct Result<T>(T? Value, TransferError? Error, string? Detail = null)
{
    public bool IsSuccess => Error is null;

    public static Result<T> Ok(T value) => new(value, null);

    public static Result<T> Fail(TransferError error, string? detail = null) => new(default, error, detail);
}
