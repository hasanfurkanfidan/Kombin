namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The classes that run the embedding job against the shared database. There is one embedding run at a time per database (an advisory lock,
/// <c>EmbeddingRunLock</c>), so two of these classes running in parallel would skip each other's runs. This collection runs them one after another;
/// every other class never starts a run.
/// </summary>
[CollectionDefinition(Name)]
public sealed class EmbeddingRuns
{
    public const string Name = "Embedding runs";
}
