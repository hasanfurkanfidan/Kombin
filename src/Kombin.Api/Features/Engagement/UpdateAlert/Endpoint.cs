using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Http;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Validation;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Engagement;
using Npgsql;

namespace Kombin.Api.Features.Engagement.UpdateAlert;

/// <summary>A partial update: a null field is left as it is. <c>Sizes: []</c> clears the size filter.</summary>
internal sealed record UpdateAlertRequest(decimal? TargetPrice, IReadOnlyList<string>? Sizes, bool? IsActive);

internal static class UpdateAlertEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPatch("/{id:guid}", Handle)
            .WithName("UpdateAlert")
            .WithValidation<UpdateAlertRequest>()
            .Produces<AlertResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static async Task<IResult> Handle(
        Guid id, UpdateAlertRequest request, ICurrentUser currentUser, KombinDbContext db, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        var alert = await db.PriceAlerts.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == userId, cancellationToken);
        if (alert is null)
        {
            return EngagementErrors.AlertNotFound.ToHttpResult();
        }

        if (request.TargetPrice is { } target)
        {
            if (alert.Kind != AlertKind.PriceDrop)
            {
                return Problems.Validation(httpContext, new Dictionary<string, string[]> { ["targetPrice"] = ["'Target Price' is only allowed for PriceDrop alerts."] });
            }

            if (!await AlertTargets.IsBelowCurrentAsync(db, alert.ProductId, target, cancellationToken))
            {
                return EngagementErrors.TargetNotBelowCurrent.ToHttpResult();
            }

            alert.TargetPrice = target;
        }

        if (request.Sizes is { } sizes)
        {
            if (alert.Kind != AlertKind.BackInStock && sizes.Count > 0)
            {
                return Problems.Validation(httpContext, new Dictionary<string, string[]> { ["sizes"] = ["'Sizes' is only allowed for BackInStock alerts."] });
            }

            alert.Sizes = [.. sizes.Select(size => size.Trim()).Distinct(StringComparer.Ordinal)];
        }

        if (request.IsActive is { } isActive && isActive != alert.IsActive)
        {
            // Switching on must not leave two live alerts of one kind for the product.
            if (isActive && await db.PriceAlerts.AnyAsync(
                    other => other.UserId == userId && other.ProductId == alert.ProductId && other.Kind == alert.Kind && other.IsActive && other.Id != alert.Id,
                    cancellationToken))
            {
                return EngagementErrors.AlertExists.ToHttpResult();
            }

            alert.IsActive = isActive;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost a race against a concurrent creation or reactivation of the same alert.
            return EngagementErrors.AlertExists.ToHttpResult();
        }

        return Results.Ok((await AlertResponses.BuildAsync(db, [alert], cancellationToken))[0]);
    }
}
