using Microsoft.Extensions.Options;

namespace Kombin.Api.Common.Storage;

internal static class StorageExtensions
{
    /// <summary>
    /// Registers <see cref="IObjectStorage"/>. The settings are validated when the store is first used rather than at start-up, so a
    /// process (or test host) that never touches storage does not need them.
    /// </summary>
    public static IServiceCollection AddObjectStorage(this IServiceCollection services)
    {
        services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.SectionName);
        services.AddSingleton<IObjectStorage>(provider => new S3ObjectStorage(provider.GetRequiredService<IOptions<StorageOptions>>().Value));

        return services;
    }
}
