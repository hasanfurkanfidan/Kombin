using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Engagement;
using Npgsql;

namespace Kombin.Api.Features.Engagement.CreateAlert;

internal sealed record CreateAlertRequest(Guid ProductId, string Kind, decimal? TargetPrice, IReadOnlyList<string>? Sizes);

internal static class CreateAlertEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/", Handle)
            .WithName("CreateAlert")
            .WithValidation<CreateAlertRequest>()
            .Produces<AlertResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static async Task<IResult> Handle(CreateAlertRequest request, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var kind = Enum.Parse<AlertKind>(request.Kind, ignoreCase: true);

        if (!await db.Products.AnyAsync(product => product.Id == request.ProductId, cancellationToken))
        {
            return CatalogErrors.ProductNotFound.ToHttpResult();
        }

        if (await db.PriceAlerts.AnyAsync(
                alert => alert.UserId == userId && alert.ProductId == request.ProductId && alert.Kind == kind && alert.IsActive,
                cancellationToken))
        {
            return EngagementErrors.AlertExists.ToHttpResult();
        }

        if (request.TargetPrice is { } target
            && !await AlertTargets.IsBelowCurrentAsync(db, request.ProductId, target, cancellationToken))
        {
            return EngagementErrors.TargetNotBelowCurrent.ToHttpResult();
        }

        var alert = new PriceAlert
        {
            UserId = userId,
            ProductId = request.ProductId,
            Kind = kind,
            TargetPrice = request.TargetPrice,
            Sizes = [.. request.Sizes?.Select(size => size.Trim()).Distinct(StringComparer.Ordinal) ?? []],
        };
        db.PriceAlerts.Add(alert);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost a race against a concurrent creation of the same alert.
            return EngagementErrors.AlertExists.ToHttpResult();
        }

        var response = (await AlertResponses.BuildAsync(db, [alert], cancellationToken))[0];

        return Results.Json(response, statusCode: StatusCodes.Status201Created);
    }
}
