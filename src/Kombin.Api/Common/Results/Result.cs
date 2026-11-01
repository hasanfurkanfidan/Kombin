namespace Kombin.Api.Common.Results;

/// <summary>
/// Outcome of an operation that can fail in an expected way. Expected failures are values;
/// exceptions are reserved for bugs.
/// </summary>
internal class Result
{
    protected Result(bool isSuccess, Error error)
    {
        if (isSuccess == (error != Error.None))
        {
            throw new InvalidOperationException("A successful result cannot carry an error, and a failed result must carry one.");
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result Failure(Error error) => new(false, error);

    public static Result<T> Success<T>(T value) => new(value);

    public static Result<T> Failure<T>(Error error) => new(error);

    public static implicit operator Result(Error error) => Failure(error);

    public TOut Match<TOut>(Func<TOut> onSuccess, Func<Error, TOut> onFailure) =>
        IsSuccess ? onSuccess() : onFailure(Error);
}
