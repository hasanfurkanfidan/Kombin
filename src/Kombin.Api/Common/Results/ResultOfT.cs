namespace Kombin.Api.Common.Results;

internal sealed class Result<T> : Result
{
    private readonly T? _value;

    internal Result(T value)
        : base(true, Error.None) => _value = value;

    internal Result(Error error)
        : base(false, error)
    {
    }

    /// <summary>The success value. Reading it from a failed result is a bug and throws.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot read the value of a failed result.");

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure<T>(error);

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure) =>
        IsSuccess ? onSuccess(Value) : onFailure(Error);

    public Result<TOut> Map<TOut>(Func<T, TOut> map) =>
        IsSuccess ? Success(map(Value)) : Failure<TOut>(Error);
}
