using Kombin.Api.Common.Text;
using Kombin.Api.Features.League;
using Kombin.Api.Features.League.GetResults;
using Kombin.Api.Persistence.Entities.League;

namespace Kombin.Api.Tests;

/// <summary>
/// The league's Turkish lines are the API's, never the app's (task B8). Each line is pinned: a change is a deliberate edit here and a note to the
/// app team. <c>CopyGlossaryTests</c> scans <c>LeagueCopy.cs</c> for banned words.
/// </summary>
public class LeagueCopyTests
{
    public static TheoryData<string, string> Lines => new()
    {
        { "RecentlyCompeted", "Bu kombin yakın zamanda yarıştı, bugün yarışmıyor." },
        { "UnsafePiece", "Kombinindeki bir parçanın fotoğrafı yarışma için uygun bulunmadı, bu yüzden bugün yarışmıyor. Başka bir kombin giyip işaretleyebilirsin." },
        { "IncompleteOutfit", "Bu kombinde yeterince parça yok ya da bazı parçalar dolabında değil. Dolabındaki parçalarla bir kombin giyip işaretlersen yarışır." },
        { "PieceUnavailable", "Kombinindeki bir parça dolabından çıkarılmış ya da fotoğrafı yok, bu yüzden bugün yarışmıyor. Parçayı geri ekleyebilir ya da başka bir kombin giyebilirsin." },
        { "AgeRestricted", "Kombin Ligi 18 yaş ve üzeri için. Doğum yılına göre bugün yarışamıyorsun." },
        { "NotWorn", "Bu kombin artık giydim olarak işaretli değil, bu yüzden bugün yarışmıyor. Bugün giydiğin bir kombini işaretlersen yarışır." },
    };

    [Theory]
    [MemberData(nameof(Lines))]
    public void EveryReasonHasItsPinnedLine(string reasonName, string expected)
    {
        var reason = Enum.Parse<LeagueEntryReason>(reasonName);
        Assert.Equal(expected, LeagueCopy.For(reason));
        Assert.Equal(expected, LeagueCopy.ForEntry(LeagueEntryStatus.NotCompeting, reason));
    }

    [Fact]
    public void EveryReasonIsPinned()
    {
        Assert.Equal(Enum.GetNames<LeagueEntryReason>().Order(), Lines.Select(line => line.Data.Item1).Order());
    }

    [Fact]
    public void ANewReasonCannotBeAddedWithoutALine()
    {
        foreach (var reason in Enum.GetValues<LeagueEntryReason>())
        {
            Assert.False(string.IsNullOrWhiteSpace(LeagueCopy.For(reason)), $"{reason} has no line");
        }
    }

    [Fact]
    public void TheRemovedLineIsForTheEntryAndForTheResult()
    {
        const string Removed = "Kombinin incelendi ve yarıştan çıkarıldı, bu yüzden sıralaman ve oy puanın yok. Diğer puanların duruyor.";

        Assert.Equal(Removed, LeagueCopy.Removed);
        Assert.Equal(Removed, LeagueCopy.ForEntry(LeagueEntryStatus.Removed, null));
        Assert.Equal(Removed, LeagueCopy.ForResult(LeagueResultStatus.Removed));
        Assert.Equal("Kombinin inceleniyor. Sonuç inceleme bitince görünecek.", LeagueCopy.ForResult(LeagueResultStatus.UnderReview));
    }

    [Fact]
    public void StatesThatNeedNoLineGetNone()
    {
        Assert.Null(LeagueCopy.ForResult(LeagueResultStatus.Final));
        Assert.Null(LeagueCopy.ForEntry(LeagueEntryStatus.Competing, null));
        Assert.Null(LeagueCopy.ForEntry(LeagueEntryStatus.Pending, null));
        Assert.Null(LeagueCopy.ForEntry(LeagueEntryStatus.Withdrawn, null));
        Assert.Null(LeagueCopy.For(null));
    }

    [Fact]
    public void EveryLineFollowsTheGlossary()
    {
        var lines = Enum.GetValues<LeagueEntryReason>().Select(reason => LeagueCopy.For(reason)!)
            .Concat([LeagueCopy.Removed, LeagueCopy.UnderReview]);

        foreach (var line in lines)
        {
            Assert.Empty(CopyGlossary.Violations(line));
        }
    }
}
