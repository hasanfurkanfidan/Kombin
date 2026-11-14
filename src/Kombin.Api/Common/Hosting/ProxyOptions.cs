namespace Kombin.Api.Common.Hosting;

/// <summary>
/// <c>Proxy:*</c>: how the API sits behind its reverse proxy (Caddy in <c>docker/compose.prod.yaml</c>). Forwarded headers are trusted only from
/// <see cref="KnownNetworks"/> (and loopback), so a client cannot claim another address by sending <c>X-Forwarded-For</c> itself.
/// </summary>
internal sealed class ProxyOptions
{
    public const string SectionName = "Proxy";

    /// <summary>
    /// True when TLS ends at the proxy (always, in production): the API then redirects plain HTTP to HTTPS (308) and sends HSTS. Left false for
    /// a plain-HTTP development or compose stack.
    /// </summary>
    public bool TlsTerminated { get; set; }

    /// <summary>
    /// CIDR ranges whose <c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c> are believed. The default is Docker's default address pool, which
    /// holds the compose network; an entry may also be a comma-separated list (one environment variable).
    /// </summary>
    public List<string> KnownNetworks { get; set; } = ["172.16.0.0/12"];

    public IEnumerable<string> ParsedNetworks() =>
        KnownNetworks
            .SelectMany(entry => entry.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
