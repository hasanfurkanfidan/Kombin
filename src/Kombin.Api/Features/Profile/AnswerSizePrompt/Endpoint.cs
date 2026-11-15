using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Results;
using Kombin.Api.Common.Time;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Profile.ConfirmSize;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Profile;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;

namespace Kombin.Api.Features.Profile.AnswerSizePrompt;

/// <summary><c>Size</c> is required for <c>BoughtFits</c>; for "too small" and "too large" it is the size that was bought, when the user says.</summary>
internal sealed record AnswerSizePromptRequest(string Answer, string? Size);

/// <summary><c>SizeEntry</c> is the user's entry for the brand and group after the answer (null when there is none).</summary>
internal sealed record AnswerSizePromptResponse(SizePromptAnswer Answer, SizeEntryResponse? SizeEntry);

internal static class AnswerSizePromptEndpoint
{
    /// <summary>
    /// <c>POST /me/size-prompts/{id}</c>. Stores the answer on the question (it leaves the list) and applies <see cref="SizePromptRules.EffectOf"/>
    /// to the size map with the rules of <c>POST /me/sizes/confirm</c>. 404 for an unknown or someone else's question, 409 once it expired or
    /// when it was answered differently; the same answer again is a no-op.
    /// </summary>
    public static void Map(RouteGroupBuilder group) =>
        group.MapPost("/{id:guid}", Handle)
            .WithName("AnswerSizePrompt")
            .WithValidation<AnswerSizePromptRequest>()
            .Produces<AnswerSizePromptResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static async Task<IResult> Handle(
        Guid id, AnswerSizePromptRequest request, ICurrentUser currentUser, KombinDbContext db, IClock clock, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        var now = clock.UtcNow;
        var answer = Enum.Parse<SizePromptAnswer>(request.Answer, ignoreCase: true);
        var size = string.IsNullOrWhiteSpace(request.Size) ? null : SizeGroups.NormalizeSize(request.Size);

        // The second attempt only happens when a parallel request inserted the same size entry between our read and our insert.
        for (var attempt = 1; ; attempt++)
        {
            var prompt = await db.SizePrompts.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.UserId == userId, cancellationToken);
            if (prompt is null)
            {
                return ProfileErrors.SizePromptNotFound.ToHttpResult();
            }

            var product = await db.Products.AsNoTracking()
                .Where(candidate => candidate.Id == prompt.ProductId)
                .Select(candidate => new
                {
                    candidate.BrandId,
                    BrandName = candidate.Brand.Name,
                    BrandSlug = candidate.Brand.Slug,
                    CategorySlug = candidate.Category.Slug,
                })
                .SingleAsync(cancellationToken);
            var group = SizeGroups.ForCategorySlug(product.CategorySlug);
            var entry = await db.SizeEntries.SingleOrDefaultAsync(
                candidate => candidate.UserId == userId && candidate.BrandId == product.BrandId && candidate.Group == group, cancellationToken);
            var brand = new BrandRef(product.BrandId, product.BrandName, product.BrandSlug);

            if (prompt.Answer is { } previous)
            {
                // A retry of the same answer (a lost response) is fine; changing one's mind is not: the size map has already moved.
                return previous == answer && prompt.AnsweredSize == size
                    ? Respond(answer, entry, brand)
                    : ProfileErrors.SizePromptAnswered.ToHttpResult();
            }

            if (SizePromptRules.IsExpired(prompt.ClickedAt, now))
            {
                return ProfileErrors.SizePromptExpired.ToHttpResult();
            }

            switch (SizePromptRules.EffectOf(answer, size, entry?.Size))
            {
                case SizeEffect.Confirm:
                    if (entry is null)
                    {
                        entry = new SizeEntry { UserId = userId, BrandId = product.BrandId, Group = group };
                        db.SizeEntries.Add(entry);
                    }

                    entry.Size = size!;
                    entry.Source = SizeSource.Confirmed;
                    entry.Confidence = 1m;
                    break;

                case SizeEffect.Doubt:
                    entry!.Confidence = Math.Min(entry.Confidence, ConfirmSizeEndpoint.DoubtedConfidence);
                    break;
            }

            prompt.Answer = answer;
            prompt.AnsweredSize = size;
            prompt.AnsweredAt = now;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return Respond(answer, entry, brand);
            }
            catch (DbUpdateException exception) when (attempt == 1 && exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private static Ok<AnswerSizePromptResponse> Respond(SizePromptAnswer answer, SizeEntry? entry, BrandRef brand) =>
        TypedResults.Ok(new AnswerSizePromptResponse(
            answer,
            entry is null ? null : new SizeEntryResponse(entry.Id, brand, entry.Group, entry.Size, entry.Source, entry.Confidence, entry.UpdatedAt)));
}
