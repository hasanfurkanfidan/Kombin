using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Profile;
using Npgsql;

namespace Kombin.Api.Features.Profile.UpsertSize;

internal sealed record UpsertSizeRequest(string BrandSlug, string Group, string Size);

internal static class UpsertSizeEndpoint
{
    /// <summary>Stores what the user says they wear: source <see cref="SizeSource.UserEntered"/>, confidence 1. Replaces any entry for the brand and group.</summary>
    public static void Map(RouteGroupBuilder group) =>
        group.MapPut("/", Handle)
            .WithName("UpsertSize")
            .WithValidation<UpsertSizeRequest>()
            .Produces<SizeEntryResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(UpsertSizeRequest request, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var slug = request.BrandSlug.Trim().ToLowerInvariant();

        var brand = await db.Brands.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Slug == slug, cancellationToken);
        if (brand is null)
        {
            return CatalogErrors.BrandNotFound.ToHttpResult();
        }

        var group = Enum.Parse<SizeGroup>(request.Group, ignoreCase: true);
        var size = SizeGroups.NormalizeSize(request.Size);

        // The second attempt only happens when a parallel request inserted the same entry between our read and our insert.
        for (var attempt = 1; ; attempt++)
        {
            var entry = await db.SizeEntries.SingleOrDefaultAsync(
                candidate => candidate.UserId == userId && candidate.BrandId == brand.Id && candidate.Group == group, cancellationToken);

            if (entry is null)
            {
                entry = new SizeEntry { UserId = userId, BrandId = brand.Id, Group = group };
                db.SizeEntries.Add(entry);
            }

            entry.Size = size;
            entry.Source = SizeSource.UserEntered;
            entry.Confidence = 1m;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok(SizeEntryResponse.From(entry, brand));
            }
            catch (DbUpdateException exception) when (attempt == 1 && exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}
