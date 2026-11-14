namespace Kombin.Api.Common.Http;

/// <summary>Builds every <c>application/problem+json</c> response so they all carry the same extensions.</summary>
internal static class Problems
{
    public static IResult Create(
        HttpContext context,
        int statusCode,
        string? title = null,
        string? detail = null,
        string? errorCode = null,
        IReadOnlyDictionary<string, object?>? extra = null) =>
        TypedResults.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            extensions: Extensions(context, errorCode, extra));

    public static IResult Validation(HttpContext context, IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(
            errors,
            title: "One or more validation errors occurred.",
            extensions: Extensions(context, "Validation.Failed"));

    private static Dictionary<string, object?> Extensions(HttpContext context, string? errorCode, IReadOnlyDictionary<string, object?>? extra = null)
    {
        var extensions = new Dictionary<string, object?> { ["correlationId"] = context.GetCorrelationId() };

        if (extra is not null)
        {
            foreach (var (name, value) in extra)
            {
                extensions[name] = value;
            }
        }

        if (errorCode is not null)
        {
            extensions["errorCode"] = errorCode;
        }

        return extensions;
    }
}

internal static class ProblemDetailsSetup
{
    public static IServiceCollection AddKombinProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context =>
            {
                // Covers responses produced by the framework itself (empty 401/403/404 bodies via status code pages).
                var extensions = context.ProblemDetails.Extensions;
                extensions["correlationId"] = context.HttpContext.GetCorrelationId();

                var errorCode = context.ProblemDetails.Status switch
                {
                    StatusCodes.Status401Unauthorized => "Auth.Unauthorized",
                    StatusCodes.Status403Forbidden => "Auth.Forbidden",
                    _ => null,
                };

                if (errorCode is not null)
                {
                    extensions.TryAdd("errorCode", errorCode);
                }
            });

        services.AddExceptionHandler<GlobalExceptionHandler>();

        return services;
    }
}
