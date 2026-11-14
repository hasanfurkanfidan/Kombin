using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Common.Http;

/// <summary><c>RequestLimits:*</c>: how large a request body may be.</summary>
internal sealed class RequestLimitsOptions
{
    public const string SectionName = "RequestLimits";

    /// <summary>Any body: 10 MB (a wardrobe photo is uploaded straight to storage, never through the API).</summary>
    public long DefaultBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>A JSON body on a public or app endpoint: 1 MB. The internal worker endpoints (upsert of 500 offers) keep the default.</summary>
    public long JsonBytes { get; set; } = 1024 * 1024;
}

/// <summary>
/// Refuses an oversized body with a 413 problem document before the handler reads it: by <c>Content-Length</c> when the client sent one,
/// and for chunked bodies through the server's own limit (Kestrel's <c>MaxRequestBodySize</c>, which this sets per request). An endpoint with
/// its own <see cref="IRequestSizeLimitMetadata"/> (the visual search's 8 MB photo) keeps that limit; everything else gets
/// <see cref="RequestLimitsOptions.JsonBytes"/> when the body is JSON and <see cref="RequestLimitsOptions.DefaultBytes"/> otherwise.
/// </summary>
internal sealed class RequestBodyLimitMiddleware(RequestDelegate next, IOptions<RequestLimitsOptions> options)
{
    public const string ErrorCode = "Request.TooLarge";

    private const string InternalPrefix = "/api/v1/internal";

    public async Task InvokeAsync(HttpContext context)
    {
        var limit = LimitFor(context);

        if (context.Request.ContentLength > limit)
        {
            await Problems.Create(
                    context,
                    StatusCodes.Status413PayloadTooLarge,
                    title: "The request is too large.",
                    detail: $"The request body may be at most {limit} bytes.",
                    errorCode: ErrorCode)
                .ExecuteAsync(context);

            return;
        }

        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = limit;
        }

        await next(context);
    }

    private long LimitFor(HttpContext context)
    {
        var explicitLimit = context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize;
        if (explicitLimit is { } fixedLimit)
        {
            return fixedLimit;
        }

        var json = context.Request.HasJsonContentType() && !context.Request.Path.StartsWithSegments(InternalPrefix, StringComparison.OrdinalIgnoreCase);

        return json ? options.Value.JsonBytes : options.Value.DefaultBytes;
    }
}
