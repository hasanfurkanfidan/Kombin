using System.Security.Cryptography;
using System.Text;
using Kombin.Api.Common.Results;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Common.Auth;

/// <summary>Rejects requests that do not carry the <c>X-Internal-Key</c> header matching <c>Internal:ApiKey</c>.</summary>
internal sealed class InternalKeyFilter(IOptions<InternalOptions> options) : IEndpointFilter
{
    public const string HeaderName = "X-Internal-Key";

    public static readonly Error InvalidKey =
        Error.Unauthorized("Catalog.InvalidInternalKey", "The internal API key is missing or invalid.");

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();

        return IsValid(provided, options.Value.ApiKey)
            ? await next(context)
            : InvalidKey.ToHttpResult();
    }

    /// <summary>Compares SHA-256 digests in constant time so neither the key's content nor its length leaks through timing.</summary>
    internal static bool IsValid(string provided, string expected) =>
        provided.Length > 0
        && expected.Length > 0
        && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}

internal static class InternalKeyExtensions
{
    public static TBuilder RequireInternalKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter<TBuilder, InternalKeyFilter>();
}
