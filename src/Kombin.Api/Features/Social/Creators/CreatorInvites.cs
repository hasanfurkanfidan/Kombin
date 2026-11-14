using Kombin.Api.Common.Results;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Social;

namespace Kombin.Api.Features.Social.Creators;

/// <summary>
/// The founder's creator tools (no self-signup in v1): invite a user or update their page, and revoke. Shared by the internal endpoints and
/// the admin panel, so a handle is normalised and checked for uniqueness in one place. The request has to pass <c>PutCreatorValidator</c> first.
/// </summary>
internal sealed class CreatorInvites(KombinDbContext db)
{
    /// <summary>Makes a user a creator or updates their page; every field is replaced.</summary>
    public async Task<Result<CreatorProfileResponse>> PutAsync(Guid userId, PutCreatorRequest request, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(user => user.Id == userId, cancellationToken))
        {
            return SocialErrors.UserNotFound;
        }

        var handle = request.Handle!.Trim().TrimStart('@').ToLowerInvariant();
        if (await db.CreatorProfiles.AnyAsync(creator => creator.Handle == handle && creator.UserId != userId, cancellationToken))
        {
            return SocialErrors.HandleTaken;
        }

        var creator = await db.CreatorProfiles.SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);
        if (creator is null)
        {
            creator = new CreatorProfile { UserId = userId };
            db.CreatorProfiles.Add(creator);
        }

        creator.Handle = handle;
        creator.Bio = string.IsNullOrWhiteSpace(request.Bio) ? null : request.Bio.Trim();
        creator.Link = string.IsNullOrWhiteSpace(request.Link) ? null : request.Link.Trim();
        creator.IsActive = request.IsActive ?? true;
        await db.SaveChangesAsync(cancellationToken);

        return ToResponse(creator);
    }

    /// <summary>
    /// Stops attribution (<c>IsActive = false</c>): the handle stays reserved and the creator's past clicks and posts stay as they were. Revoking
    /// a creator who is already inactive is not an error.
    /// </summary>
    public async Task<Result<CreatorProfileResponse>> RevokeAsync(Guid userId, CancellationToken cancellationToken)
    {
        var creator = await db.CreatorProfiles.SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);
        if (creator is null)
        {
            return SocialErrors.CreatorNotFound;
        }

        if (creator.IsActive)
        {
            creator.IsActive = false;
            await db.SaveChangesAsync(cancellationToken);
        }

        return ToResponse(creator);
    }

    private static CreatorProfileResponse ToResponse(CreatorProfile creator) =>
        new(creator.UserId, creator.Handle, creator.Bio, creator.Link, creator.IsActive, creator.UpdatedAt);
}
