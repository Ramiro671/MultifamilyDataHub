namespace MDH.Shared.Common;

public class Result<T>
{
    public bool IsSuccess { get; private init; }
    public T? Value { get; private init; }
    public string? Error { get; private init; }

    private Result() { }

    public static Result<T> Success(T value) => new() { IsSuccess = true, Value = value };
    public static Result<T> Failure(string error) => new() { IsSuccess = false, Error = error };

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<string, TOut> onFailure)
        => IsSuccess ? onSuccess(Value!) : onFailure(Error!);
}

public class Result
{
    public bool IsSuccess { get; private init; }
    public string? Error { get; private init; }

    private Result() { }

    public static Result Success() => new() { IsSuccess = true };
    public static Result Failure(string error) => new() { IsSuccess = false, Error = error };
}
