using System.Net;
using Kombin.Api.Common.Http;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// DNS for the guarded image clients (<see cref="OutboundGuard"/>): a host ending in <c>.internal.test</c> points inside (10.0.0.7),
/// <c>metadata.internal.test</c> at the cloud metadata address, anything else at a public address. Literal IPs never reach a resolver.
/// </summary>
internal sealed class FakeOutboundResolver : IOutboundResolver
{
    public const string PrivateHost = "images.internal.test";

    public const string MetadataHost = "metadata.internal.test";

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult<IPAddress[]>(host switch
    {
        MetadataHost => [IPAddress.Parse("169.254.169.254")],
        _ when host.EndsWith(".internal.test", StringComparison.OrdinalIgnoreCase) => [IPAddress.Parse("10.0.0.7")],
        _ => [IPAddress.Parse("93.184.215.14")],
    });
}
