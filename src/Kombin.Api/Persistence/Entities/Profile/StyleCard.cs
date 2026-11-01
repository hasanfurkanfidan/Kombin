using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Persistence.Entities.Profile;

/// <summary>
/// Editorial content shown in the swipe onboarding. Seeded from <c>style-cards.json</c> in every environment; the IDs are fixed
/// in that file so a swipe stays valid across deployments. Not user data.
/// </summary>
internal sealed class StyleCard : BaseEntity
{
    public string Title { get; set; } = string.Empty;

    public string ImageUrl { get; set; } = string.Empty;

    public Audience Audience { get; set; }

    public StyleFamily Family { get; set; }

    /// <summary><see cref="ProductColor"/> names.</summary>
    public string[] Colors { get; set; } = [];

    /// <summary>1 = sportswear, 5 = formal.</summary>
    public short Formality { get; set; }

    /// <summary><see cref="ProductFit"/> names.</summary>
    public string[] Fits { get; set; } = [];

    public Season Seasons { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;
}
