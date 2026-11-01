using System.Security.Claims;

namespace Kombin.Api.Common.Auth;

internal interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>The authenticated user's ID. Reading it without authentication is a bug and throws.</summary>
    Guid UserId { get; }

    /// <summary>The authenticated user's ID, or null for an anonymous caller. For endpoints that work signed in or out.</summary>
    Guid? UserIdOrNull { get; }
}

internal sealed class CurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid UserId =>
        UserIdOrNull ?? throw new InvalidOperationException("There is no authenticated user with a valid 'sub' claim.");

    public Guid? UserIdOrNull =>
        IsAuthenticated && Guid.TryParse(Principal!.FindFirstValue("sub"), out var userId) ? userId : null;
}
