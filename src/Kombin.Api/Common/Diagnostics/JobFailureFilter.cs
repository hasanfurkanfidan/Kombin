using System.Reflection;
using Hangfire;
using Hangfire.Server;

namespace Kombin.Api.Common.Diagnostics;

/// <summary>
/// Reports a background job that failed for the last time (<see cref="ErrorLog"/>: Sentry and the system page). An attempt that will be retried is not
/// reported: <c>WardrobeAnalyzeJob</c> and <c>PostProcessJob</c> retry three times and only the fourth failure counts. Registered once, globally, in
/// the Hangfire configuration. The source is the job's name (<c>WardrobeAnalyzeJob.RunAsync</c>), never its arguments.
/// </summary>
internal sealed class JobFailureFilter(ErrorLog errors) : IServerFilter
{
    /// <summary>Hangfire's own default when a job has no <see cref="AutomaticRetryAttribute"/>.</summary>
    internal const int DefaultAttempts = 10;

    public void OnPerforming(PerformingContext context)
    {
    }

    public void OnPerformed(PerformedContext context)
    {
        if (context.Exception is null || context.Canceled || context.ExceptionHandled)
        {
            return;
        }

        var exception = context.Exception is JobPerformanceException { InnerException: { } inner } ? inner : context.Exception;
        if (exception is OperationCanceledException)
        {
            return;
        }

        var retryCount = context.GetJobParameter<int>("RetryCount");
        if (!IsLastAttempt(context.BackgroundJob.Job.Method, retryCount))
        {
            return;
        }

        errors.ReportAsync(exception, ErrorLog.JobKind, JobName(context.BackgroundJob.Job.Type, context.BackgroundJob.Job.Method), correlationId: null)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>True when Hangfire will not try again: the retries the job's <see cref="AutomaticRetryAttribute"/> allows are used up.</summary>
    internal static bool IsLastAttempt(MethodInfo method, int retryCount)
    {
        var attempts = (method.GetCustomAttribute<AutomaticRetryAttribute>() ?? method.DeclaringType?.GetCustomAttribute<AutomaticRetryAttribute>())?.Attempts
            ?? DefaultAttempts;

        return retryCount >= attempts;
    }

    internal static string JobName(Type type, MethodInfo method) => type.Name + "." + method.Name;
}
