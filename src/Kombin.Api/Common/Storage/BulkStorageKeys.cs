namespace Kombin.Api.Common.Storage;

/// <summary>
/// The renditions of one staged bulk-add image. None is public: <see cref="StorageKeys.IsPublic"/> looks at the file name and these are neither
/// <c>cutout.*</c> nor <c>thumb.*</c>, so every one is stored <c>private, no-store</c> and served through a presigned GET. The room photo
/// (<c>Original</c>) stays only while its batch is <c>Failed</c>; <c>Staged</c> is the piece's cutout as vision sent it (the review image, the
/// larger of the two) and <c>StagedThumb</c> its thumbnail (the grid); both are copied to the wardrobe item's public names at confirm.
/// </summary>
internal enum BulkImageVariant
{
    Original,
    Staged,
    StagedThumb,
}

/// <summary>
/// Keys of bulk add: <c>bulk/{userId}/{batchId}/{ownerId}/{variant}.{ext}</c>. The batch is part of the path, so confirm, cancel, the cleanup and the
/// account purger delete a batch (or a user) by listing its prefix instead of trusting rows: a crashed delete or a lost row leaves nothing the next
/// run cannot find. The photo's owner is the batch itself.
/// TODO(task BA2): this belongs in <see cref="StorageKeys"/> (<c>BuildBulk</c>) and in <see cref="ImageVariant"/>; it is a file of its own only
/// because task B9 had that file open. Move it, keeping the method and variant names, once B9 is committed.
/// </summary>
internal static class BulkStorageKeys
{
    public const string Area = "bulk";

    public static string Build(Guid userId, Guid batchId, Guid ownerId, BulkImageVariant variant, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        return $"{BatchPrefix(userId, batchId)}{ownerId:D}/{variant.ToString().ToLowerInvariant()}.{extension.TrimStart('.').ToLowerInvariant()}";
    }

    /// <summary>The room photo: <c>bulk/{user}/{batch}/{batch}/original.{ext}</c>.</summary>
    public static string Original(Guid userId, Guid batchId, string extension) => Build(userId, batchId, batchId, BulkImageVariant.Original, extension);

    /// <summary>Everything of one batch, with the trailing slash: the prefix to list and delete.</summary>
    public static string BatchPrefix(Guid userId, Guid batchId) => $"{UserPrefix(userId)}{batchId:D}/";

    /// <summary>Everything of one user, with the trailing slash: what the account purger deletes.</summary>
    public static string UserPrefix(Guid userId) => $"{Area}/{userId:D}/";
}
