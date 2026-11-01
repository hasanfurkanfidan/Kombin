using System.Text.RegularExpressions;

namespace Kombin.Api.Common.Http;

/// <summary>
/// Accepts a well-formed <c>X-Correlation-ID</c> from the caller or generates one, exposes it to the rest of the
/// pipeline through <see cref="HttpContext.Items"/>, echoes it on the response and adds it to the log scope.
/// </summary>
internal sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";

    internal const string ItemKey = "Kombin.CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = ValidCorrelationId().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = correlationId;

        // Set when the response starts: the exception handler clears headers before it writes its own response.
        context.Response.OnStarting(
            static state =>
            {
                var (httpContext, id) = ((HttpContext, string))state;
                httpContext.Response.Headers[HeaderName] = id;
                return Task.CompletedTask;
            },
            (context, correlationId));

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9\-_.:]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidCorrelationId();
}

internal static class CorrelationIdHttpContextExtensions
{
    public static string? GetCorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var value) ? value as string : null;
}
