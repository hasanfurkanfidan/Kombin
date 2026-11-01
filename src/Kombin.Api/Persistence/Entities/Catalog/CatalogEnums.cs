namespace Kombin.Api.Persistence.Entities.Catalog;

internal enum SellerKind
{
    Marketplace,
    BrandStore,
}

internal enum Audience
{
    Women,
    Men,
    Unisex,
    Kids,
}

internal enum ProductColor
{
    Black,
    White,
    Grey,
    Navy,
    Blue,
    Green,
    Beige,
    Brown,
    Red,
    Pink,
    Purple,
    Yellow,
    Orange,
    Multi,
    Other,
}

internal enum ProductPattern
{
    Solid,
    Striped,
    Checked,
    Printed,
    Textured,
    Other,
}

internal enum ProductFit
{
    Slim,
    Regular,
    Relaxed,
    Oversized,
    Unknown,
}

[Flags]
internal enum Season
{
    None = 0,
    Spring = 1,
    Summer = 2,
    Autumn = 4,
    Winter = 8,
    AllYear = Spring | Summer | Autumn | Winter,
}

internal enum StockStatus
{
    InStock,
    LowStock,
    OutOfStock,
    Unknown,
}
