using System.Security.Claims;

namespace Kombin.Api.Common.Auth;

internal interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>The authenticated user's ID. Reading it without authentication is a bug and throws.</summary>
    Guid UserId { get; }
}

internal sealed class CurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid UserId =>
        IsAuthenticated && Guid.TryParse(Principal!.FindFirstValue("sub"), out var userId)
            ? userId
            : throw new InvalidOperationException("There is no authenticated user with a valid 'sub' claim.");
}
