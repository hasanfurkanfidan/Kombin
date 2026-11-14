using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kombin.Api.Common.Http;

/// <summary>Resolves a host name to its addresses; replaced in tests so a host can point anywhere without a real DNS.</summary>
internal interface IOutboundResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

internal sealed class DnsOutboundResolver : IOutboundResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>A download the guard refused: the URL, or a redirect it led to, points at a private, loopback, link-local or otherwise internal address.</summary>
internal sealed class OutboundRequestRefusedException(string reason) : HttpRequestException(reason);

/// <summary>
/// The rule for downloading a URL someone else chose (a catalog image URL comes from a scraped page): http(s) only, no credentials, at most
/// <see cref="MaxRedirects"/> redirects, and every host, the first and each redirect target, must resolve only to public addresses. The check
/// runs twice: <see cref="PublicOnlyHandler"/> refuses the request before it is sent, and <see cref="CreatePrimaryHandler"/> connects only to an
/// address it has just vetted, so a DNS answer that changes between the two (rebinding) still cannot reach the inside.
/// </summary>
internal static class OutboundGuard
{
    public const int MaxRedirects = 3;

    /// <summary>Whether an address is on the public internet: not loopback, private, link-local (cloud metadata), CGNAT, multicast or reserved.</summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();

            return !(b[0] == 0                                   // 0.0.0.0/8 "this network"
                || b[0] == 10                                    // 10.0.0.0/8 private
                || b[0] == 127                                   // loopback
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)    // 100.64.0.0/10 carrier-grade NAT
                || (b[0] == 169 && b[1] == 254)                  // link-local, cloud metadata (169.254.169.254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)     // 172.16.0.0/12 private (Docker networks)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)       // 192.0.0.0/24 IETF protocol assignments
                || (b[0] == 192 && b[1] == 168)                  // 192.168.0.0/16 private
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))   // 198.18.0.0/15 benchmarking
                || b[0] >= 224);                                 // multicast, reserved, broadcast
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6Any)
                || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal)
            {
                return false;
            }

            var b = address.GetAddressBytes();

            // 64:ff9b::/96 (NAT64) and 2002::/16 (6to4) carry an IPv4 address: judge that one.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(part => part == 0))
            {
                return IsPublic(new IPAddress(b[12..16]));
            }

            if (b[0] == 0x20 && b[1] == 0x02)
            {
                return IsPublic(new IPAddress(b[2..6]));
            }

            return true;
        }

        return false;
    }

    /// <summary>Why <paramref name="uri"/> may not be fetched before any resolution (scheme, credentials), or null.</summary>
    public static string? RefusalOf(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "only absolute http(s) URLs are fetched";
        }

        return uri.UserInfo.Length > 0 ? "URLs with credentials are not fetched" : null;
    }

    /// <summary>The public addresses of <paramref name="host"/>; throws <see cref="OutboundRequestRefusedException"/> when any address is not public.</summary>
    public static async Task<IPAddress[]> ResolvePublicAsync(IOutboundResolver resolver, string host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await resolver.ResolveAsync(host, cancellationToken);

        // All of them, not just the first: a host answering one public and one private address could be connected to either.
        if (addresses.Length == 0 || addresses.Any(address => !IsPublic(address)))
        {
            throw new OutboundRequestRefusedException($"The host {host} does not resolve to public addresses only.");
        }

        return addresses;
    }

    /// <summary>
    /// The socket handler for guarded clients: no automatic redirects (<see cref="PublicOnlyHandler"/> follows and checks them) and a connect step
    /// that resolves the host itself and connects to the vetted address.
    /// </summary>
    public static SocketsHttpHandler CreatePrimaryHandler(IOutboundResolver resolver) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await ResolvePublicAsync(resolver, context.DnsEndPoint.Host, cancellationToken);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}

/// <summary>
/// Refuses a request whose URL, or any redirect it leads to, breaks <see cref="OutboundGuard"/>'s rule; follows at most
/// <see cref="OutboundGuard.MaxRedirects"/> redirects itself (GET only, as the image downloads are). Sits above the primary handler, so it also
/// guards the clients whose socket handler the tests replace.
/// </summary>
internal sealed class PublicOnlyHandler(IOutboundResolver resolver) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var current = request;

        for (var hop = 0; ; hop++)
        {
            var uri = current.RequestUri ?? throw new OutboundRequestRefusedException("The request has no URL.");

            if (OutboundGuard.RefusalOf(uri) is { } refusal)
            {
                throw new OutboundRequestRefusedException(refusal);
            }

            await OutboundGuard.ResolvePublicAsync(resolver, uri.IdnHost, cancellationToken);

            var response = await base.SendAsync(current, cancellationToken);

            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
            {
                return response;
            }

            response.Dispose();

            if (hop >= OutboundGuard.MaxRedirects || current.Method != HttpMethod.Get)
            {
                throw new OutboundRequestRefusedException("Too many redirects.");
            }

            var next = new HttpRequestMessage(HttpMethod.Get, location.IsAbsoluteUri ? location : new Uri(uri, location));

            foreach (var header in request.Headers)
            {
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (!ReferenceEquals(current, request))
            {
                current.Dispose();
            }

            current = next;
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}

internal static class OutboundGuardExtensions
{
    /// <summary>Registers the resolver once and puts a named client behind <see cref="OutboundGuard"/>'s rule.</summary>
    public static IHttpClientBuilder AddPublicOnlyGuard(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<IOutboundResolver, DnsOutboundResolver>();
        builder.Services.TryAddTransient<PublicOnlyHandler>();

        return builder
            .ConfigurePrimaryHttpMessageHandler(provider => OutboundGuard.CreatePrimaryHandler(provider.GetRequiredService<IOutboundResolver>()))
            .AddHttpMessageHandler<PublicOnlyHandler>();
    }
}
