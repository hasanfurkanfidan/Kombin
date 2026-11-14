using Kombin.Api.Common.Http;

namespace Kombin.Api.Common.Results;

internal static class ResultHttpExtensions
{
    public static IResult ToHttpResult(this Result result) =>
        result.IsSuccess ? TypedResults.NoContent() : result.Error.ToHttpResult();

    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult>? onSuccess = null) =>
        result.IsSuccess
            ? onSuccess?.Invoke(result.Value) ?? TypedResults.Ok(result.Value)
            : result.Error.ToHttpResult();

    public static IResult ToHttpResult(this Error error) => new ErrorHttpResult(error);

    public static int ToStatusCode(this ErrorType errorType) => errorType switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>Defers building the problem document until the request context (and correlation ID) is available.</summary>
    private sealed class ErrorHttpResult(Error error) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            var problem = Problems.Create(
                httpContext,
                error.ErrorType.ToStatusCode(),
                detail: error.Description,
                errorCode: error.Code,
                extra: error.Extensions);

            return problem.ExecuteAsync(httpContext);
        }
    }
}
