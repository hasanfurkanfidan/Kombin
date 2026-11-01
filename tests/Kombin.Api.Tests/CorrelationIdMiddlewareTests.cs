using Kombin.Api.Common.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kombin.Api.Tests;

public class CorrelationIdMiddlewareTests
{
    private static async Task<string?> RunAsync(string? incomingHeader)
    {
        var context = new DefaultHttpContext();

        if (incomingHeader is not null)
        {
            context.Request.Headers[CorrelationIdMiddleware.HeaderName] = incomingHeader;
        }

        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask, NullLogger<CorrelationIdMiddleware>.Instance);
        await middleware.InvokeAsync(context);

        return context.GetCorrelationId();
    }

    [Theory]
    [InlineData("abc-123")]
    [InlineData("trace:0af7651916cd43dd8448eb211c80319c")]
    [InlineData("a.b_c-D:9")]
    public async Task WellFormedIncomingIdIsKept(string incoming)
    {
        Assert.Equal(incoming, await RunAsync(incoming));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("line\r\nbreak")]
    public async Task MalformedIncomingIdIsReplaced(string incoming)
    {
        var correlationId = await RunAsync(incoming);

        Assert.NotNull(correlationId);
        Assert.NotEqual(incoming, correlationId);
        Assert.Matches("^[0-9a-f]{32}$", correlationId);
    }

    [Fact]
    public async Task OverlongIncomingIdIsReplaced()
    {
        var incoming = new string('a', 65);

        Assert.NotEqual(incoming, await RunAsync(incoming));
    }

    [Fact]
    public async Task MaximumLengthIncomingIdIsKept()
    {
        var incoming = new string('a', 64);

        Assert.Equal(incoming, await RunAsync(incoming));
    }

    [Fact]
    public async Task MissingHeaderGeneratesAnId()
    {
        var first = await RunAsync(null);
        var second = await RunAsync(null);

        Assert.NotNull(first);
        Assert.NotEqual(first, second);
    }
}
