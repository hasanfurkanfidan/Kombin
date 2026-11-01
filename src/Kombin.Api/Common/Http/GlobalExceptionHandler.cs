using Microsoft.AspNetCore.Diagnostics;

namespace Kombin.Api.Common.Http;

/// <summary>Turns every unhandled exception into a generic 500 problem document and logs it exactly once.</summary>
internal sealed partial class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment environment)
    : IExceptionHandler
{
    // Non-standard status used by proxies for "client closed the request"; nothing is listening for the body.
    private const int ClientClosedRequest = 499;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            httpContext.Response.StatusCode = ClientClosedRequest;
            return true;
        }

        if (exception is BadHttpRequestException badRequest)
        {
            // Malformed bodies surface as exceptions when the host is configured to throw on bad requests (Development).
            var detail = environment.IsDevelopment() ? badRequest.Message : "The request could not be read.";
            await Problems.Create(httpContext, badRequest.StatusCode, detail: detail).ExecuteAsync(httpContext);
            return true;
        }

        LogUnhandledException(exception, httpContext.GetCorrelationId());

        await Problems.Create(
                httpContext,
                StatusCodes.Status500InternalServerError,
                title: "An unexpected error occurred.",
                detail: environment.IsDevelopment() ? exception.Message : null,
                errorCode: "Server.UnexpectedError")
            .ExecuteAsync(httpContext);

        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception. CorrelationId={CorrelationId}")]
    private partial void LogUnhandledException(Exception exception, string? correlationId);
}
