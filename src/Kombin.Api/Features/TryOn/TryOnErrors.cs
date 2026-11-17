using Kombin.Api.Common.Results;

namespace Kombin.Api.Features.TryOn;

internal static class TryOnErrors
{
    public static readonly Error Disabled = Error.NotFound("TryOn.Disabled", "Manken üstünde gör henüz açık değil.");

    public static readonly Error OutfitNotFound = Error.NotFound("TryOn.OutfitNotFound", "Kombin bulunamadı.");

    public static readonly Error ProductNotFound = Error.NotFound("TryOn.ProductNotFound", "Ürün bulunamadı.");

    public static readonly Error UnknownGarment = Error.Validation("TryOn.UnknownGarment", "Bildirilen parça bu kombinde yok.");

    public static readonly Error RendererUnavailable = Error.Unavailable("TryOn.RendererUnavailable", "Manken görünümü şu an hazırlanamıyor. Biraz sonra yeniden dene.");
}
