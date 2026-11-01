namespace Kombin.Api.Persistence.Entities.Profile;

/// <summary>The broad style families a style card (and, heuristically, a product) belongs to.</summary>
internal enum StyleFamily
{
    Minimal,
    Classic,
    Street,
    Sporty,
    Boho,
    Formal,
    Romantic,
    Edgy,
}

internal enum SwipeDecision
{
    Like,
    Dislike,
    Skip,
}

internal enum SizeGroup
{
    Tops,
    Bottoms,
    Outerwear,
    Shoes,
    Accessories,
}

internal enum SizeSource
{
    UserEntered,
    Inferred,
    Confirmed,
}
