using System.Reflection;
using System.Text.RegularExpressions;
using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Tests;

/// <summary>
/// A bulk piece is the same record as a single one (the vision brief: <c>analysis</c> is exactly <c>/analyze</c>'s answer), so the columns the single
/// add's analysis sets must exist on <see cref="BulkPiece"/> with the same types and defaults, or confirm would drop something the single add keeps
/// (task BA2). The list is read from <c>WardrobeAnalyzeJob</c> itself: a column added there fails this test until the piece has it too.
/// </summary>
public sealed partial class BulkPieceMirrorsItemTests
{
    /// <summary>What the analysis job assigns that is not an attribute: the state machine and the keys (the staged ones are named differently on purpose).</summary>
    private static readonly string[] NotAnAttribute = ["Status", "FailureReason", "AnalysisAttempts", "ImageCutoutKey", "ImageThumbKey", "ImageOriginalKey"];

    [Fact]
    public void EveryColumnTheAnalysisSetsExistsOnThePieceWithTheSameTypeAndDefault()
    {
        var assigned = AssignedByTheAnalysisJob();
        Assert.NotEmpty(assigned);

        var item = new WardrobeItem();
        var piece = new BulkPiece();

        foreach (var name in assigned)
        {
            var onItem = typeof(WardrobeItem).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!;
            var onPiece = typeof(BulkPiece).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

            Assert.True(onPiece is not null, $"WardrobeAnalyzeJob sets item.{name}, but BulkPiece has no {name}: a bulk piece would lose it at confirm.");
            Assert.True(onItem.PropertyType == onPiece.PropertyType, $"{name}: item is {onItem.PropertyType.Name}, piece is {onPiece.PropertyType.Name}.");
            AssertSameDefault(name, onItem.GetValue(item), onPiece.GetValue(piece));
        }
    }

    [Fact]
    public void TheStagedImagesAreTheItemsImagesUnderPrivateNames()
    {
        Assert.NotNull(typeof(BulkPiece).GetProperty(nameof(BulkPiece.CutoutKey)));
        Assert.NotNull(typeof(BulkPiece).GetProperty(nameof(BulkPiece.ThumbKey)));
        Assert.Equal(typeof(string), typeof(BulkPiece).GetProperty(nameof(BulkPiece.CutoutKey))!.PropertyType);
        Assert.Equal(typeof(WardrobeItem).GetProperty(nameof(WardrobeItem.ImageCutoutKey))!.PropertyType, typeof(BulkPiece).GetProperty(nameof(BulkPiece.CutoutKey))!.PropertyType);
        Assert.Equal(typeof(WardrobeItem).GetProperty(nameof(WardrobeItem.ImageThumbKey))!.PropertyType, typeof(BulkPiece).GetProperty(nameof(BulkPiece.ThumbKey))!.PropertyType);
    }

    [Fact]
    public void ThePieceHoldsNothingTheItemWouldKeepForTheUserOnly()
    {
        // What the user edits afterwards with PATCH (name, brand, size, price...) is not on the piece: confirm edits only category and colours.
        var onPiece = typeof(BulkPiece).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(nameof(WardrobeItem.Name), onPiece);
        Assert.DoesNotContain(nameof(WardrobeItem.BrandId), onPiece);
        Assert.DoesNotContain(nameof(WardrobeItem.Size), onPiece);
        Assert.DoesNotContain(nameof(WardrobeItem.PurchasePrice), onPiece);
        Assert.DoesNotContain(nameof(WardrobeItem.ModerationVerdict), onPiece);
    }

    private static void AssertSameDefault(string name, object? onItem, object? onPiece)
    {
        if (onItem is Array itemArray && onPiece is Array pieceArray)
        {
            Assert.Equal(itemArray.Length, pieceArray.Length);

            return;
        }

        Assert.True(Equals(onItem, onPiece), $"{name}: the item starts as '{onItem}', the piece as '{onPiece}'.");
    }

    private static List<string> AssignedByTheAnalysisJob()
    {
        var source = File.ReadAllText(Path.Combine(SourceRoot(), "Features", "Wardrobe", "Jobs", "WardrobeAnalyzeJob.cs"));
        var body = source[source.IndexOf("public async Task<AnalyzeOutcome> AnalyzeAsync", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("private async Task<AnalyzeOutcome> FailAsync", StringComparison.Ordinal)];

        return [.. Assignment().Matches(body).Select(match => match.Groups["name"].Value).Distinct(StringComparer.Ordinal).Where(name => !NotAnAttribute.Contains(name, StringComparer.Ordinal))];
    }

    [GeneratedRegex(@"^\s*item\.(?<name>[A-Za-z]+)\s*=[^=]", RegexOptions.Multiline)]
    private static partial Regex Assignment();

    private static string SourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Kombin.slnx")))
            {
                return Path.Combine(directory.FullName, "src", "Kombin.Api");
            }
        }

        throw new InvalidOperationException("Kombin.slnx not found above the test binaries.");
    }
}
