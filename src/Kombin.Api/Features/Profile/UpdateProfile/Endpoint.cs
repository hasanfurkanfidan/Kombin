using System.Text.Json.Serialization;
using Kombin.Api.Common.Auth;
using Kombin.Api.Common.Validation;
using Kombin.Api.Features.Profile.GetProfile;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Profile.UpdateProfile;

/// <summary>
/// The measurements and the city, the same way as the seller PUT: left out = unchanged, an explicit null = cleared. The <c>*Set</c> flags
/// record which of them the body contained; the JSON reader only calls an init accessor for a property that is present. <c>Audience</c> is
/// the exception, a null keeps the current one too (a profile always has an audience). <c>City</c> (at most 80 characters; blank clears it)
/// is where the outfit engine reads the weather.
/// </summary>
internal sealed record UpdateProfileRequest
{
    public string? Audience { get; init; }

    public int? BirthYear
    {
        get;
        init
        {
            field = value;
            BirthYearSet = true;
        }
    }

    public int? HeightCm
    {
        get;
        init
        {
            field = value;
            HeightCmSet = true;
        }
    }

    public decimal? WeightKg
    {
        get;
        init
        {
            field = value;
            WeightKgSet = true;
        }
    }

    public int? ChestCm
    {
        get;
        init
        {
            field = value;
            ChestCmSet = true;
        }
    }

    public int? WaistCm
    {
        get;
        init
        {
            field = value;
            WaistCmSet = true;
        }
    }

    public int? HipCm
    {
        get;
        init
        {
            field = value;
            HipCmSet = true;
        }
    }

    public decimal? ShoeSizeEu
    {
        get;
        init
        {
            field = value;
            ShoeSizeEuSet = true;
        }
    }

    public string? City
    {
        get;
        init
        {
            field = value;
            CitySet = true;
        }
    }

    [JsonIgnore]
    public bool BirthYearSet { get; private init; }

    [JsonIgnore]
    public bool HeightCmSet { get; private init; }

    [JsonIgnore]
    public bool WeightKgSet { get; private init; }

    [JsonIgnore]
    public bool ChestCmSet { get; private init; }

    [JsonIgnore]
    public bool WaistCmSet { get; private init; }

    [JsonIgnore]
    public bool HipCmSet { get; private init; }

    [JsonIgnore]
    public bool ShoeSizeEuSet { get; private init; }

    [JsonIgnore]
    public bool CitySet { get; private init; }
}

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

        if (request.BirthYearSet)
        {
            profile.BirthYear = request.BirthYear;
        }

        if (request.HeightCmSet)
        {
            profile.HeightCm = request.HeightCm;
        }

        if (request.WeightKgSet)
        {
            profile.WeightKg = request.WeightKg;
        }

        if (request.ChestCmSet)
        {
            profile.ChestCm = request.ChestCm;
        }

        if (request.WaistCmSet)
        {
            profile.WaistCm = request.WaistCm;
        }

        if (request.HipCmSet)
        {
            profile.HipCm = request.HipCm;
        }

        if (request.ShoeSizeEuSet)
        {
            profile.ShoeSizeEu = request.ShoeSizeEu;
        }

        if (request.CitySet)
        {
            profile.City = string.IsNullOrWhiteSpace(request.City) ? null : request.City.Trim();
        }

        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(await ProfileResponse.BuildAsync(db, profile, cancellationToken));
    }
}
