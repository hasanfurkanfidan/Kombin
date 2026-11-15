using System.Net;
using Kombin.Api.Common.Http;

namespace Kombin.Api.Tests;

public sealed class OutboundGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.7")]
    [InlineData("172.16.0.1")]
    [InlineData("172.30.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:a00:1::1")]
    public void InternalAddressesAreNotPublic(string address) => Assert.False(OutboundGuard.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("93.184.215.14")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void InternetAddressesArePublic(string address) => Assert.True(OutboundGuard.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("ftp://img.example.com/a.jpg")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:secret@img.example.com/a.jpg")]
    public void OnlyPlainHttpUrlsAreFetched(string url) => Assert.NotNull(OutboundGuard.RefusalOf(new Uri(url)));

    [Fact]
    public async Task AHostWithOnePrivateAnswerIsRefused()
    {
        var resolver = new MapResolver(new() { ["mixed.test"] = [IPAddress.Parse("93.184.215.14"), IPAddress.Parse("10.0.0.1")] });

        await Assert.ThrowsAsync<OutboundRequestRefusedException>(() => OutboundGuard.ResolvePublicAsync(resolver, "mixed.test", Ct));
    }

    [Fact]
    public async Task ARedirectToAnInternalHostIsRefusedBeforeItIsRequested()
    {
        var inner = new ScriptedHandler(request => request.RequestUri!.Host == "cdn.test"
            ? Redirect("http://internal.test/secret.jpg")
            : new HttpResponseMessage(HttpStatusCode.OK));
        var resolver = new MapResolver(new() { ["cdn.test"] = [IPAddress.Parse("93.184.215.14")], ["internal.test"] = [IPAddress.Parse("172.30.0.2")] });
        using var client = new HttpClient(new PublicOnlyHandler(resolver) { InnerHandler = inner });

        await Assert.ThrowsAsync<OutboundRequestRefusedException>(() => client.GetAsync(new Uri("https://cdn.test/a.jpg"), Ct));
        Assert.Equal(["cdn.test"], inner.Hosts);
    }

    [Fact]
    public async Task PublicRedirectsAreFollowedAtMostThreeTimes()
    {
        var inner = new ScriptedHandler(request => Redirect($"https://cdn.test/{Guid.NewGuid():N}"));
        var resolver = new MapResolver(new() { ["cdn.test"] = [IPAddress.Parse("93.184.215.14")] });
        using var client = new HttpClient(new PublicOnlyHandler(resolver) { InnerHandler = inner });

        await Assert.ThrowsAsync<OutboundRequestRefusedException>(() => client.GetAsync(new Uri("https://cdn.test/start"), Ct));
        Assert.Equal(OutboundGuard.MaxRedirects + 1, inner.Hosts.Count);
    }

    [Fact]
    public async Task APublicRedirectIsFollowed()
    {
        var inner = new ScriptedHandler(request => request.RequestUri!.AbsolutePath == "/start" ? Redirect("/final.jpg") : new HttpResponseMessage(HttpStatusCode.OK));
        var resolver = new MapResolver(new() { ["cdn.test"] = [IPAddress.Parse("93.184.215.14")] });
        using var client = new HttpClient(new PublicOnlyHandler(resolver) { InnerHandler = inner });

        using var response = await client.GetAsync(new Uri("https://cdn.test/start"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/final.jpg", response.RequestMessage!.RequestUri!.AbsolutePath);
    }

    // The second line of defence: even if the name resolved to something public a moment ago (DNS rebinding), the socket handler resolves
    // again and refuses to connect to an internal address.
    [Fact]
    public async Task TheSocketHandlerRefusesToConnectToAnInternalAddress()
    {
        var resolver = new MapResolver(new() { ["rebind.test"] = [IPAddress.Loopback] });
        using var client = new HttpClient(OutboundGuard.CreatePrimaryHandler(resolver));

        var exception = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(new Uri("http://rebind.test:1/x.jpg"), Ct));

        Assert.True(exception is OutboundRequestRefusedException || exception.InnerException is OutboundRequestRefusedException, exception.ToString());
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class MapResolver(Dictionary<string, IPAddress[]> map) : IOutboundResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult(map.GetValueOrDefault(host) ?? []);
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hosts.Add(request.RequestUri!.Host);
            var response = answer(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
