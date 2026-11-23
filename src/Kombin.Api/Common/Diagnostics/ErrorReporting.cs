using System.Reflection;
using Microsoft.AspNetCore.Diagnostics;

namespace Kombin.Api.Common.Diagnostics;

/// <summary>
/// Error reporting to Sentry (docs/privacy-notes.md, "Error reporting"). Off unless <c>Sentry:Dsn</c> (env <c>Sentry__Dsn</c>) is set: with no DSN the SDK is
/// never initialised and every <see cref="Capture"/> is a no-op. Only the core SDK is used, so nothing about a request, a user or a log line is collected
/// on its own; the three callers (<see cref="Http.GlobalExceptionHandler"/>, the Hangfire job filter, start-up) hand over an exception and the few
/// tags below, and <see cref="SentryScrubber"/> rebuilds every event from an allow-list before it leaves.
/// </summary>
internal static class ErrorReporting
{
    public const string DsnKey = "Sentry:Dsn";

    /// <summary>The git SHA of the build (compose passes <c>GIT_SHA</c>); the assembly's informational version when not set.</summary>
    public const string ReleaseKey = "Sentry:Release";

    /// <summary>The web address of the Sentry project, only for the link on <c>/admin/system</c>.</summary>
    public const string ProjectUrlKey = "Sentry:ProjectUrl";

    /// <summary>The tags an event may carry; <see cref="SentryScrubber"/> drops every other one.</summary>
    internal static class Tags
    {
        /// <summary><c>request</c>, <c>job</c> or <c>startup</c>.</summary>
        public const string Kind = "kind";

        /// <summary>The route template (<c>/api/v1/wardrobe/items/{id}</c>), never the raw path; or the job's name.</summary>
        public const string Source = "source";

        public const string ErrorCode = "error_code";

        public const string CorrelationId = "correlation_id";

        public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal) { Kind, Source, ErrorCode, CorrelationId };
    }

    public static bool IsEnabled(IConfiguration configuration) => !string.IsNullOrWhiteSpace(configuration[DsnKey]);

    /// <summary>The recorder the exception handler and the Hangfire filter share; the SDK itself is started by <see cref="Start"/>.</summary>
    public static IServiceCollection AddErrorReporting(this IServiceCollection services)
    {
        services.AddSingleton<ErrorLog>();
        services.AddSingleton<JobFailureFilter>();
        return services;
    }

    /// <summary>Starts the SDK when a DSN is set; dispose the result at shutdown to flush. Returns null (and does nothing) without a DSN.</summary>
    public static IDisposable? Start(IConfiguration configuration, string environment)
    {
        if (!IsEnabled(configuration))
        {
            return null;
        }

        return SentrySdk.Init(options => Configure(options, configuration, environment));
    }

    /// <summary>Every setting that matters for privacy and noise, in one place (pinned by <c>ErrorReportingTests</c>).</summary>
    internal static void Configure(SentryOptions options, IConfiguration configuration, string environment)
    {
        options.Dsn = configuration[DsnKey];
        options.Environment = environment;
        options.Release = Release(configuration);

        // Privacy: no user, no IP, no machine or OS user name, no breadcrumbs (they would collect log lines), no stack-local data.
        options.SendDefaultPii = false;
        options.IsEnvironmentUser = false;
        options.ServerName = "kombin-api";
        options.MaxBreadcrumbs = 0;
        options.AttachStacktrace = false;
        options.SendClientReports = false;
        options.AutoSessionTracking = false;
        options.DisableFileWrite = true;

        // Errors only, all of them; no performance tracing for now.
        options.SampleRate = 1.0f;
        options.TracesSampleRate = 0.0;

        // Noise: a cancelled request or shutdown is not a bug.
        options.AddExceptionFilterForType<OperationCanceledException>();

        // What the SDK catches on its own (a crash while starting, an unobserved task) is filed as "process"; Capture overrides both tags.
        options.DefaultTags[Tags.Kind] = "process";
        options.DefaultTags[Tags.Source] = "process";

        options.SetBeforeSend((sentryEvent, _) => SentryScrubber.Scrub(sentryEvent));
    }

    internal static string Release(IConfiguration configuration)
    {
        var configured = configuration[ReleaseKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return "kombin-api@" + configured.Trim();
        }

        var version = typeof(ErrorReporting).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return "kombin-api@" + version;
    }

    /// <summary>Reports an exception with the allowed tags only. A no-op when the SDK is not running; never throws.</summary>
    public static void Capture(Exception exception, string kind, string source, string errorCode, string? correlationId)
    {
        if (!SentrySdk.IsEnabled)
        {
            return;
        }

        try
        {
            SentrySdk.CaptureException(exception, scope =>
            {
                scope.SetTag(Tags.Kind, kind);
                scope.SetTag(Tags.Source, source);
                scope.SetTag(Tags.ErrorCode, errorCode);
                if (!string.IsNullOrEmpty(correlationId))
                {
                    scope.SetTag(Tags.CorrelationId, correlationId);
                }
            });
        }
        catch (Exception reportingFailure) when (reportingFailure is not OutOfMemoryException)
        {
            // Reporting must never turn one error into two.
        }
    }

    /// <summary>A start-up refusal that is not an exception (invalid production settings): only its code is sent.</summary>
    public static void CaptureStartupFailure(string errorCode)
    {
        if (!SentrySdk.IsEnabled)
        {
            return;
        }

        SentrySdk.CaptureMessage(errorCode, scope =>
        {
            scope.SetTag(Tags.Kind, "startup");
            scope.SetTag(Tags.Source, "startup");
            scope.SetTag(Tags.ErrorCode, errorCode);
        }, SentryLevel.Fatal);
        SentrySdk.Flush(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The route template of the endpoint that ran (<c>/api/v1/wardrobe/items/{id}</c>), never the path the client sent. Inside the exception handler the
    /// context's endpoint is already cleared; the original one is on the exception handler feature.
    /// </summary>
    public static string RouteTemplate(HttpContext context) =>
        (context.Features.Get<IExceptionHandlerFeature>()?.Endpoint ?? context.GetEndpoint()) is RouteEndpoint { RoutePattern.RawText: { } template }
            ? (template.StartsWith('/') ? template : "/" + template)
            : "(none)";

    /// <summary>The name an error is filed under: the exception's type, never its message (messages can carry what a user typed).</summary>
    public static string ErrorCode(Exception exception) => exception.GetType().FullName ?? exception.GetType().Name;
}
