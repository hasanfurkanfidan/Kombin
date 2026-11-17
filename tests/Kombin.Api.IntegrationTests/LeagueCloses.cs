namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The classes that close league days against the shared database. There is one close at a time per database (an advisory lock,
/// <c>DayCloser</c>), so two of these classes running in parallel would make each other's closes answer "already running". This collection
/// runs them one after another; no other class closes a day.
/// </summary>
[CollectionDefinition(Name)]
public sealed class LeagueCloses
{
    public const string Name = "League closes";
}
