using Kombin.Api.Common.Storage;

namespace Kombin.Api.Tests;

/// <summary>Keys of bulk add (task BA2): the staged images are private by their names, like every key that is not <c>cutout.*</c> or <c>thumb.*</c>, and a batch is found by its prefix.</summary>
public sealed class BulkStorageKeysTests
{
    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Batch = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Piece = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void KeysAreBulkUserBatchOwnerVariant()
    {
        Assert.Equal(
            "bulk/11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222/33333333-3333-3333-3333-333333333333/staged.png",
            BulkStorageKeys.Build(User, Batch, Piece, BulkImageVariant.Staged, "png"));
        Assert.EndsWith("/33333333-3333-3333-3333-333333333333/stagedthumb.webp", BulkStorageKeys.Build(User, Batch, Piece, BulkImageVariant.StagedThumb, ".WEBP"), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePhotoIsOwnedByTheBatch() =>
        Assert.Equal(
            "bulk/11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222/22222222-2222-2222-2222-222222222222/original.jpg",
            BulkStorageKeys.Original(User, Batch, "jpg"));

    [Fact]
    public void EveryKeyOfABatchIsUnderItsPrefixAndEveryKeyOfAUserUnderTheirs()
    {
        var keys = new[]
        {
            BulkStorageKeys.Original(User, Batch, "jpg"),
            BulkStorageKeys.Build(User, Batch, Piece, BulkImageVariant.Staged, "png"),
            BulkStorageKeys.Build(User, Batch, Piece, BulkImageVariant.StagedThumb, "webp"),
        };

        Assert.All(keys, key => Assert.StartsWith(BulkStorageKeys.BatchPrefix(User, Batch), key, StringComparison.Ordinal));
        Assert.All(keys, key => Assert.StartsWith(BulkStorageKeys.UserPrefix(User), key, StringComparison.Ordinal));
        Assert.EndsWith("/", BulkStorageKeys.BatchPrefix(User, Batch), StringComparison.Ordinal);
        Assert.DoesNotContain(BulkStorageKeys.BatchPrefix(User, Guid.NewGuid()), keys, StringComparer.Ordinal);
    }

    [Fact]
    public void NoBulkKeyIsPublicAndEveryOneIsStoredPrivateNoStore()
    {
        foreach (var variant in Enum.GetValues<BulkImageVariant>())
        {
            var key = BulkStorageKeys.Build(User, Batch, Piece, variant, "png");

            Assert.False(StorageKeys.IsPublic(key), key);
            Assert.Equal(StorageKeys.PrivateCacheControl, StorageKeys.CacheControlFor(key));
        }
    }

    [Fact]
    public void AStagedNameIsNeverMistakenForAPublicOne()
    {
        // The bucket policy matches names that start with "cutout." or "thumb.": "staged" and "stagedthumb" must never do.
        foreach (var name in new[] { "staged.png", "stagedthumb.webp", "original.jpg" })
        {
            Assert.False(StorageKeys.IsPublic($"bulk/{User:D}/{Batch:D}/{Piece:D}/{name}"), name);
        }

        Assert.True(StorageKeys.IsPublic($"wardrobe/{User:D}/{Piece:D}/cutout.png"));
        Assert.True(StorageKeys.IsPublic($"wardrobe/{User:D}/{Piece:D}/thumb.webp"));
    }

    [Fact]
    public void AnEmptyExtensionIsRefused() =>
        Assert.Throws<ArgumentException>(() => BulkStorageKeys.Build(User, Batch, Piece, BulkImageVariant.Staged, " "));
}
