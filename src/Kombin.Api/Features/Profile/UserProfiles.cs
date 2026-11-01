using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Profile;
using Npgsql;

namespace Kombin.Api.Features.Profile;

internal static class UserProfiles
{
    /// <summary>The user's non-archived personas.</summary>
    public static IQueryable<Persona> OwnedBy(this IQueryable<Persona> personas, Guid userId) =>
        personas.Where(persona => persona.UserId == userId && !persona.IsArchived);

    /// <summary>
    /// The tracked profile of the user, created on first use. Call it before making other changes: creating the row saves the context.
    /// </summary>
    public static async Task<UserProfile> GetOrCreateAsync(KombinDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var profile = await db.UserProfiles.SingleOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);

        if (profile is not null)
        {
            return profile;
        }

        profile = new UserProfile { UserId = userId };
        db.UserProfiles.Add(profile);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return profile;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A parallel request created it first; use theirs.
            db.Entry(profile).State = EntityState.Detached;
            return await db.UserProfiles.SingleAsync(candidate => candidate.UserId == userId, cancellationToken);
        }
    }
}
