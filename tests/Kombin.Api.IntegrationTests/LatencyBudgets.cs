namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The latency-budget classes (<see cref="OutfitPerformanceTests"/>, <see cref="FeedPerformanceTests"/>, <see cref="LensPerformanceTests"/>,
/// <see cref="BudgetAndTripPerformanceTests"/>) share this collection, which runs on its own: each class seeds 50,000 rows into its own container,
/// and a budget measured while another class seeds or measures is noise.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LatencyBudgets
{
    public const string Name = "Latency budgets";
}
