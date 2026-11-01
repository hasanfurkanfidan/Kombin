using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Kombin.Api.IntegrationTests;

public sealed class DeviceTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RegisteringADeviceStoresItActiveForTheCaller()
    {
        using var user = await factory.CreateUserAsync(Ct);

        var token = await user.Client.RegisterDeviceAsync(cancellationToken: Ct);

        var device = Assert.Single(await factory.DevicesWithTokenAsync(token, Ct));
        Assert.Equal(user.Id, device.UserId);
        Assert.True(device.IsActive);
        Assert.Equal("Android", device.Platform.ToString());
        Assert.Equal("1.0.0", device.AppVersion);
        Assert.Equal("Pixel", device.DeviceName);
        Assert.Null(device.DeactivationReason);
    }

    [Fact]
    public async Task RegisteringTheSameTokenAgainUpdatesTheRowInsteadOfAddingOne()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var token = await user.Client.RegisterDeviceAsync(cancellationToken: Ct);

        using var again = await user.Client.PutDeviceAsync(token, "Ios", "2.0.0", "iPhone", Ct);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);

        var device = Assert.Single(await factory.DevicesWithTokenAsync(token, Ct));
        Assert.Equal("Ios", device.Platform.ToString());
        Assert.Equal("2.0.0", device.AppVersion);
        Assert.Equal("iPhone", device.DeviceName);
    }

    [Fact]
    public async Task ATokenThatMovesToAnotherUserIsDeactivatedAsReplacedForTheFirst()
    {
        using var first = await factory.CreateUserAsync(Ct);
        using var second = await factory.CreateUserAsync(Ct);
        var token = await first.Client.RegisterDeviceAsync(cancellationToken: Ct);

        await second.Client.RegisterDeviceAsync(token, Ct);

        var rows = await factory.DevicesWithTokenAsync(token, Ct);
        Assert.Equal(2, rows.Count);

        var old = Assert.Single(rows, row => row.UserId == first.Id);
        Assert.False(old.IsActive);
        Assert.Equal("Replaced", old.DeactivationReason);
        Assert.NotNull(old.DeactivatedAt);

        var current = Assert.Single(rows, row => row.UserId == second.Id);
        Assert.True(current.IsActive);

        // And back again: the first user's old row is reused, the second's is replaced in turn.
        await first.Client.RegisterDeviceAsync(token, Ct);

        rows = await factory.DevicesWithTokenAsync(token, Ct);
        Assert.Equal(2, rows.Count);
        Assert.Equal(first.Id, Assert.Single(rows, row => row.IsActive).UserId);
        Assert.Equal("Replaced", Assert.Single(rows, row => row.UserId == second.Id).DeactivationReason);
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("ExponentPushToken[]")]
    [InlineData("ExponentPushToken[with space]")]
    [InlineData("ExponentPushToken[abc")]
    [InlineData("Other[abcdef]")]
    public async Task AMalformedTokenIsRejectedWithItsErrorCode(string token)
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var response = await user.Client.PutDeviceAsync(token, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Notification.InvalidPushToken", (await response.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task BothSpellingsOfTheTokenPrefixAreAccepted()
    {
        using var user = await factory.CreateUserAsync(Ct);

        await user.Client.RegisterDeviceAsync($"ExpoPushToken[{Guid.NewGuid():N}]", Ct);
    }

    [Fact]
    public async Task InvalidDeviceFieldsAreValidationErrors()
    {
        using var user = await factory.CreateUserAsync(Ct);

        using var noToken = await user.Client.PutDeviceAsync(string.Empty, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.True((await noToken.ReadProblemAsync(Ct)).GetProperty("errors").TryGetProperty("expoPushToken", out _));

        using var badPlatform = await user.Client.PutDeviceAsync(NotificationClientExtensions.NewToken(), "Windows", cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badPlatform.StatusCode);
        Assert.True((await badPlatform.ReadProblemAsync(Ct)).GetProperty("errors").TryGetProperty("platform", out _));

        using var tooLong = await user.Client.PutDeviceAsync($"ExponentPushToken[{new string('a', 200)}]", cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task DeletingADeviceDeactivatesItAndIsIdempotent()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var token = await user.Client.RegisterDeviceAsync(cancellationToken: Ct);

        using var removed = await user.Client.DeleteDeviceAsync(token, Ct);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        var device = Assert.Single(await factory.DevicesWithTokenAsync(token, Ct));
        Assert.False(device.IsActive);
        Assert.Equal("UserRemoved", device.DeactivationReason);

        using var again = await user.Client.DeleteDeviceAsync(token, Ct);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);

        using var unknown = await user.Client.DeleteDeviceAsync(NotificationClientExtensions.NewToken(), Ct);
        Assert.Equal(HttpStatusCode.NoContent, unknown.StatusCode);
    }

    [Fact]
    public async Task ADeviceCanBeRegisteredAgainAfterItWasRemoved()
    {
        using var user = await factory.CreateUserAsync(Ct);
        var token = await user.Client.RegisterDeviceAsync(cancellationToken: Ct);
        (await user.Client.DeleteDeviceAsync(token, Ct)).Dispose();

        await user.Client.RegisterDeviceAsync(token, Ct);

        var device = Assert.Single(await factory.DevicesWithTokenAsync(token, Ct));
        Assert.True(device.IsActive);
        Assert.Null(device.DeactivatedAt);
        Assert.Null(device.DeactivationReason);
    }

    [Fact]
    public async Task DeletingSomeoneElsesDeviceChangesNothing()
    {
        using var owner = await factory.CreateUserAsync(Ct);
        using var other = await factory.CreateUserAsync(Ct);
        var token = await owner.Client.RegisterDeviceAsync(cancellationToken: Ct);

        using var response = await other.Client.DeleteDeviceAsync(token, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(Assert.Single(await factory.DevicesWithTokenAsync(token, Ct)).IsActive);
    }

    [Fact]
    public async Task LogoutWithAPushTokenDeactivatesThatDeviceOnly()
    {
        using var client = factory.CreateAnonymousClient();
        var tokens = await client.RegisterAndReadAsync(cancellationToken: Ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var phone = await client.RegisterDeviceAsync(cancellationToken: Ct);
        var tablet = await client.RegisterDeviceAsync(cancellationToken: Ct);

        using var response = await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = tokens.RefreshToken, expoPushToken = phone }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(Assert.Single(await factory.DevicesWithTokenAsync(phone, Ct)).IsActive);
        Assert.Equal("UserRemoved", Assert.Single(await factory.DevicesWithTokenAsync(phone, Ct)).DeactivationReason);
        Assert.True(Assert.Single(await factory.DevicesWithTokenAsync(tablet, Ct)).IsActive);
    }

    [Fact]
    public async Task LogoutWithoutAPushTokenStillWorksAndLeavesDevicesAlone()
    {
        using var client = factory.CreateAnonymousClient();
        var tokens = await client.RegisterAndReadAsync(cancellationToken: Ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var phone = await client.RegisterDeviceAsync(cancellationToken: Ct);

        using var response = await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = tokens.RefreshToken }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(Assert.Single(await factory.DevicesWithTokenAsync(phone, Ct)).IsActive);
    }

    [Fact]
    public async Task LogoutCannotDeactivateAnotherUsersDevice()
    {
        using var owner = await factory.CreateUserAsync(Ct);
        var token = await owner.Client.RegisterDeviceAsync(cancellationToken: Ct);

        using var client = factory.CreateAnonymousClient();
        var tokens = await client.RegisterAndReadAsync(cancellationToken: Ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        using var response = await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = tokens.RefreshToken, expoPushToken = token }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(Assert.Single(await factory.DevicesWithTokenAsync(token, Ct)).IsActive);
    }

    [Fact]
    public async Task DeviceEndpointsNeedAuthentication()
    {
        using var anonymous = factory.CreateAnonymousClient();

        using var put = await anonymous.PutDeviceAsync(NotificationClientExtensions.NewToken(), cancellationToken: Ct);
        using var delete = await anonymous.DeleteDeviceAsync(NotificationClientExtensions.NewToken(), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
        Assert.Equal("Auth.Unauthorized", (await put.ReadProblemAsync(Ct)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task TheHangfireDashboardIsNotServedWhenHangfireIsDisabled()
    {
        using var client = factory.CreateAnonymousClient();

        using var response = await client.GetAsync("/hangfire", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
