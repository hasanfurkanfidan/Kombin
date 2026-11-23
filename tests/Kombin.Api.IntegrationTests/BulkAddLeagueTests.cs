using Kombin.Api.Features.League;
using Kombin.Api.Persistence.Entities.League;
using Microsoft.EntityFrameworkCore;
using static Kombin.Api.IntegrationTests.LeagueClientExtensions;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// League usage points (<c>docs/design/bulk-add.md</c>, decision 5): the day close reads "a piece was added" as one flag per player and day, so five
/// pieces from one confirm are one 5-point day, and the week's cap (<c>League:Points:PieceDaysPerWeek</c>, 3) counts that day once. Every request runs
/// on the test's own league days (<see cref="LeagueClientExtensions.NewMonday"/>); the class closes days, so it joins <see cref="LeagueCloses"/>.
/// </summary>
[Collection(LeagueCloses.Name)]
public sealed class BulkAddLeagueTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A bulk photo of five pieces, all confirmed, at 13:00 Istanbul on <paramref name="day"/>.</summary>
    private async Task ConfirmFivePiecesAsync(TestUser player, DateOnly day)
    {
        player.Client.At(Time(day, 13));
        var batch = await factory.RunBulkAsync(player.Client, "bulk-many-5", Ct);
        var pieces = await factory.BulkPieceRowsAsync(batch.Id, Ct);
        Assert.Equal(5, pieces.Count);

        await player.Client.BulkConfirmAsync(batch.Id, pieces.Select(piece => piece.Id), Ct);
    }

    private async Task<LeagueDailyScore?> CloseAndScoreAsync(TestUser player, DateOnly day)
    {
        Assert.Equal(DayCloseOutcome.Closed, (await factory.CloseDayAsync(day, Time(day, 21, 1), Ct)).Outcome);

        return await factory.WithDbAsync(db => db.LeagueDailyScores.AsNoTracking().SingleOrDefaultAsync(row => row.Day == day && row.UserId == player.Id, Ct));
    }

    [Fact]
    public async Task FivePiecesFromOneConfirmAreOneFivePointDay()
    {
        var day = NewMonday();
        using var player = await factory.CreatePlayerAsync(day, Ct);

        await ConfirmFivePiecesAsync(player, day);
        var score = await CloseAndScoreAsync(player, day);

        Assert.NotNull(score);
        Assert.Equal(5, score.PiecePoints);
        Assert.Equal(5, score.Total);
    }

    /// <summary>The photo at 20:30 Istanbul on Monday, the confirm after Monday's close: 23:30 the same evening or 00:30 on Tuesday.</summary>
    [Theory]
    [InlineData(0, 23, 30)]
    [InlineData(1, 0, 30)]
    public async Task APieceCountsOnTheLeagueDayOfTheConfirmNotOfThePhoto(int confirmDayOffset, int hour, int minute)
    {
        var monday = NewMonday();
        using var player = await factory.CreatePlayerAsync(monday, Ct);
        player.Client.At(Time(monday, 20, 30));
        var batch = await factory.RunBulkAsync(player.Client, "bulk-many-5", Ct);
        var pieces = await factory.BulkPieceRowsAsync(batch.Id, Ct);

        player.Client.At(Time(monday.AddDays(confirmDayOffset), hour, minute));
        await player.Client.BulkConfirmAsync(batch.Id, pieces.Select(piece => piece.Id), Ct);
        var photoDay = (await CloseAndScoreAsync(player, monday))?.PiecePoints ?? 0;
        var confirmDay = (await CloseAndScoreAsync(player, monday.AddDays(1)))?.PiecePoints ?? 0;

        Assert.Equal((0, 5), (photoDay, confirmDay));
    }

    [Fact]
    public async Task TheWeeklyCapCountsABulkConfirmAsOnePieceDay()
    {
        var monday = NewMonday();
        using var player = await factory.CreatePlayerAsync(monday, Ct);
        List<int> points = [];

        // Monday: one bulk confirm of five pieces. Tuesday and Wednesday: one single piece each. Thursday: another bulk confirm.
        await ConfirmFivePiecesAsync(player, monday);
        points.Add((await CloseAndScoreAsync(player, monday))?.PiecePoints ?? 0);
        foreach (var day in new[] { monday.AddDays(1), monday.AddDays(2) })
        {
            player.Client.At(Time(day, 13));
            await factory.AddPhotoItemAsync(player.Client, cancellationToken: Ct);
            points.Add((await CloseAndScoreAsync(player, day))?.PiecePoints ?? 0);
        }

        await ConfirmFivePiecesAsync(player, monday.AddDays(3));
        points.Add((await CloseAndScoreAsync(player, monday.AddDays(3)))?.PiecePoints ?? 0);

        Assert.Equal([5, 5, 5, 0], points);
    }
}
