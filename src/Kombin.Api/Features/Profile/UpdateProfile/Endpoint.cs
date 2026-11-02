using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Profile.GetProfile;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Profile.UpdateProfile;

/// <summary>
/// Replaces the measurements: a field that is left out is cleared. <c>Audience</c> is the exception, a null keeps the current one
/// (a profile always has an audience). <c>City</c> (at most 80 characters; blank clears it) is where the outfit engine reads the weather.
/// </summary>
internal sealed record UpdateProfileRequest(
    string? Audience,
    int? BirthYear,
    int? HeightCm,
    decimal? WeightKg,
    int? ChestCm,
    int? WaistCm,
    int? HipCm,
    decimal? ShoeSizeEu,
    string? City = null);

internal static class UpdateProfileEndpoint
{
    public static void Map(RouteGroupBuilder group) =>
        group.MapPut("/", Handle)
            .WithName("UpdateProfile")
            .WithValidation<UpdateProfileRequest>()
            .Produces<ProfileResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    private static async Task<IResult> Handle(UpdateProfileRequest request, ICurrentUser currentUser, KombinDbContext db, CancellationToken cancellationToken)
    {
        var profile = await UserProfiles.GetOrCreateAsync(db, currentUser.UserId, cancellationToken);

        if (request.Audience is not null)
        {
            profile.Audience = Enum.Parse<Audience>(request.Audience, ignoreCase: true);
        }

        profile.BirthYear = request.BirthYear;
        profile.HeightCm = request.HeightCm;
        profile.WeightKg = request.WeightKg;
        profile.ChestCm = request.ChestCm;
        profile.WaistCm = request.WaistCm;
        profile.HipCm = request.HipCm;
        profile.ShoeSizeEu = request.ShoeSizeEu;
        profile.City = string.IsNullOrWhiteSpace(request.City) ? null : request.City.Trim();

        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(await ProfileResponse.BuildAsync(db, profile, cancellationToken));
    }
}
