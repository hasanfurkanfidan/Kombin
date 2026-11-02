namespace Kombin.Api.Persistence.Entities.Recommendation;

internal enum OutfitKind
{
    /// <summary>"Yarın için dolabından bir kombin": built from the wardrobe for a day.</summary>
    Daily,

    /// <summary>"Bunu nasıl giyerim?": built around one catalog product or wardrobe item.</summary>
    HowToWear,

    /// <summary>Catalog-only ideas for a user whose wardrobe is empty.</summary>
    Shopping,
}

internal enum OutfitStatus
{
    Suggested,
    Saved,
    Worn,
    Dismissed,
}

/// <summary>How much of the outfit the user already owns. Honesty about gaps is part of the contract with the app.</summary>
internal enum OutfitCompleteness
{
    /// <summary>Every piece is in the wardrobe (a how-to-wear anchor from the catalog does not count).</summary>
    Complete,

    /// <summary>Exactly one piece comes from the catalog.</summary>
    NeedsOne,

    /// <summary>The wardrobe could not contribute: the pieces are shopping ideas.</summary>
    CatalogOnly,
}

internal enum OutfitSlot
{
    Top,
    Bottom,
    Outerwear,
    Shoes,
    Accessory,
}

internal enum OutfitItemRole
{
    /// <summary>The piece a how-to-wear outfit is built around.</summary>
    Anchor,
    Wardrobe,

    /// <summary>A catalog piece the user does not own, with its cheapest offer.</summary>
    Gap,
}

internal enum OutfitFeedbackAction
{
    Save,
    Dismiss,
    Worn,
    Shuffle,
}

internal enum DismissReason
{
    DontLikeColors,
    TooFormal,
    TooCasual,
    WrongWeather,
    DontOwnAnymore,
    Other,
}

internal enum Occasion
{
    Office,
    Weekend,
    Evening,
    Sport,
    Event,
}

internal enum NarrationProvider
{
    Template,
    Anthropic,
}
