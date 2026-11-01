using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Profile;
using Npgsql;

namespace Kombin.Api.Features.Profile.ConfirmSize;

internal sealed record ConfirmSizeRequest(Guid ProductId, string Size, bool? Fitted);

internal static class ConfirmSizeEndpoint
{
    public const decimal DoubtedConfidence = 0.5m;

    /// <summary>
    /// The "beden uydu mu?" answer after a purchase. <c>fitted: true</c> stores the size as <see cref="SizeSource.Confirmed"/> for the
    /// product's brand and group. <c>fitted: false</c> only doubts an existing entry with that same size (confidence 0.5); it never deletes.
    /// </summary>
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/confirm", Handle)
            .WithName("ConfirmSize")
            .WithValidation<ConfirmSizeRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> Handle(ConfirmSizeRequest request, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;

        var product = await db.Products
            .AsNoTracking()
            .Where(candidate => candidate.Id == request.ProductId)
            .Select(candidate => new { candidate.BrandId, CategorySlug = candidate.Category.Slug })
            .SingleOrDefaultAsync(cancellationToken);

        if (product is null)
        {
            return CatalogErrors.ProductNotFound.ToHttpResult();
        }

        var group = SizeGroups.ForCategorySlug(product.CategorySlug);
        var size = SizeGroups.NormalizeSize(request.Size);

        // The second attempt only happens when a parallel request inserted the same entry between our read and our insert.
        for (var attempt = 1; ; attempt++)
        {
            var entry = await db.SizeEntries.SingleOrDefaultAsync(
                candidate => candidate.UserId == userId && candidate.BrandId == product.BrandId && candidate.Group == group, cancellationToken);

            if (request.Fitted == true)
            {
                if (entry is null)
                {
                    entry = new SizeEntry { UserId = userId, BrandId = product.BrandId, Group = group };
                    db.SizeEntries.Add(entry);
                }

                entry.Size = size;
                entry.Source = SizeSource.Confirmed;
                entry.Confidence = 1m;
            }
            else if (entry is not null && entry.Size == size)
            {
                entry.Confidence = DoubtedConfidence;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return TypedResults.NoContent();
            }
            catch (DbUpdateException exception) when (attempt == 1 && exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}
