using Kombin.Api.Common.Storage;

namespace Kombin.Api.Features.AskFriend;

/// <summary>
/// The ask's own copies of wardrobe thumbnails (design section 3): <c>ask/{random}/{random}/shared.{ext}</c>, no user ID in the key, no
/// <c>ImageMarker</c> (the copy exists so the URL can be revoked, not to hide the asker), private by name (served through presigned GETs).
/// </summary>
internal sealed partial class AskImageCopier(IObjectStorage storage, ILogger<AskImageCopier> logger)
{
    public const string Area = "ask";

    /// <summary>
    /// Copies every source key, in order. All or nothing: a missing source or a storage failure deletes the copies already written and answers
    /// null, so nothing of a refused ask is left behind.
    /// </summary>
    public async Task<IReadOnlyList<string>?> CopyAllAsync(IReadOnlyList<string> sourceKeys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceKeys);

        var written = new List<string>(sourceKeys.Count);
        try
        {
            foreach (var source in sourceKeys)
            {
                if (await storage.GetAsync(source, cancellationToken) is not { } stored)
                {
                    LogSourceMissing(logger);
                    await DeleteAsync(written, CancellationToken.None);
                    return null;
                }

                var key = StorageKeys.Build(Area, Guid.NewGuid(), Guid.NewGuid(), ImageVariant.Shared, StorageKeys.ExtensionFor(stored.ContentType) ?? "webp");
                await storage.PutAsync(key, stored.Content, stored.ContentType, cancellationToken);
                written.Add(key);
            }
        }
        catch (Exception exception)
        {
            await DeleteAsync(written, CancellationToken.None);
            if (exception is OperationCanceledException)
            {
                throw;
            }

            LogCopyFailed(logger, exception);
            return null;
        }

        return written;
    }

    /// <summary>Deletes copies; false (logged) when the store failed, so the caller leaves <c>copies_deleted_at</c> empty for the purge.</summary>
    public async Task<bool> DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count == 0)
        {
            return true;
        }

        try
        {
            await storage.DeleteAsync(keys, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCopiesLeft(logger, keys.Count, exception);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AskFriend: a wardrobe thumbnail to copy is missing from storage; the ask is refused.")]
    private static partial void LogSourceMissing(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AskFriend: a thumbnail could not be copied; the ask is refused.")]
    private static partial void LogCopyFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AskFriend: {Count} image copies could not be deleted and are left for the purge.")]
    private static partial void LogCopiesLeft(ILogger logger, int count, Exception exception);
}
