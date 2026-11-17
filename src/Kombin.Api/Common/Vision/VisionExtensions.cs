namespace Kombin.Api.Common.Vision;

/// <summary>The size limits of the <c>kombin-vision</c> service's photo endpoints (its README): it refuses more with <c>413</c> without looking.</summary>
internal static class VisionLimits
{
    /// <summary>One photo (<c>/analyze</c>, <c>/detect</c>, <c>/garment/prepare</c>, ...).</summary>
    public const long MaxPhotoBytes = 8 * 1024 * 1024;

    /// <summary>All photos of one <c>/v1/garment/prepare</c> request together.</summary>
    public const long MaxPrepareRequestBytes = 16 * 1024 * 1024;

    /// <summary>At most this many photos per <c>/v1/garment/prepare</c> request.</summary>
    public const int MaxPrepareImages = 6;

    /// <summary>The long side the service works at: a photo scaled to it loses nothing (the try-on brief).</summary>
    public const int WorkingSide = 1600;
}

internal static class VisionExtensions
{
    /// <summary>
    /// The typed client of the <c>kombin-vision</c> service (<c>Vision:BaseUrl</c>, <c>Vision:InternalKey</c>), shared by the wardrobe analysis, the
    /// social posts, visual search, the catalog embeddings, try-on and the admin panel's health strip.
    /// </summary>
    public static IServiceCollection AddVisionClient(this IServiceCollection services)
    {
        services.AddOptions<VisionOptions>().BindConfiguration(VisionOptions.SectionName);
        services.AddHttpClient<IVisionClient, VisionClient>(VisionClient.Configure);

        return services;
    }
}
