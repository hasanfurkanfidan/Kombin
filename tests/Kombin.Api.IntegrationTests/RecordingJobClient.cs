using System.Collections.Concurrent;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// Stands in for Hangfire's job client: the tests never run a Hangfire server or create its schema, so an enqueued job is only recorded,
/// and the test runs the job itself (the way the other job tests do).
/// </summary>
internal sealed class RecordingJobClient : IBackgroundJobClient
{
    private readonly ConcurrentQueue<Job> _jobs = new();

    /// <summary>Every job created through the client, oldest first.</summary>
    public IReadOnlyList<Job> Jobs => [.. _jobs];

    /// <summary>The jobs whose first argument is <paramref name="id"/>, e.g. the analyses enqueued for one wardrobe item.</summary>
    public IReadOnlyList<Job> JobsFor(Guid id) => [.. _jobs.Where(job => job.Args.Count > 0 && job.Args[0] is Guid argument && argument == id)];

    public string Create(Job job, IState state)
    {
        _jobs.Enqueue(job);
        return Guid.NewGuid().ToString("N");
    }

    public bool ChangeState(string jobId, IState state, string expectedState) => true;
}
