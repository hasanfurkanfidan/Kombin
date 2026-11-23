using System.Text.RegularExpressions;
using Sentry.Protocol;

namespace Kombin.Api.Common.Diagnostics;

/// <summary>
/// The single place that decides what leaves the server for Sentry. It does not remove what looks personal: it builds a new event and copies over
/// only what is on the allow-list, so a field the SDK adds in a later version stays at home until someone puts it here.
/// <list type="bullet">
/// <item>Kept: level, release, environment, timestamp; for each exception its type, module, mechanism type and handled flag, and the stack frames'
/// code locations (function, module, file name without its folder, line, addresses); the tags <c>kind</c>, <c>source</c> (route template or job name), <c>error_code</c>,
/// <c>correlation_id</c>; the runtime and OS contexts (the server's, not a person's).</item>
/// <item>Dropped: exception messages (they can carry what a user typed or a database value), the mechanism's data (it holds <c>Exception.Data</c>),
/// frame variables and source lines, the user, the request (URL, query string, headers, cookies, body), the server name, breadcrumbs, extra data,
/// threads, modules, debug images (their paths are the build machine's), folder paths, every other tag and context, the SDK's attachments, and any
/// message that is not one of our start-up codes.</item>
/// </list>
/// </summary>
internal static partial class SentryScrubber
{
    private static readonly string[] AllowedContexts = ["runtime", "os"];

    public static SentryEvent Scrub(SentryEvent original)
    {
        var clean = new SentryEvent
        {
            Level = original.Level,
            Release = original.Release,
            Environment = original.Environment,
            Platform = original.Platform,
            Logger = null,
            ServerName = null,
            Message = SafeMessage(original.Message),
            SentryExceptions = original.SentryExceptions?.Select(ScrubException).ToList(),
        };

        foreach (var (key, value) in original.Tags)
        {
            if (ErrorReporting.Tags.Allowed.Contains(key) && SafeTagValue().IsMatch(value))
            {
                clean.SetTag(key, value);
            }
        }

        // An exception the SDK caught by itself has no error code yet: the outermost exception's type is one.
        if (!clean.Tags.ContainsKey(ErrorReporting.Tags.ErrorCode) && clean.SentryExceptions?.LastOrDefault()?.Type is { } type && SafeTagValue().IsMatch(type))
        {
            clean.SetTag(ErrorReporting.Tags.ErrorCode, type);
        }

        foreach (var key in AllowedContexts)
        {
            if (original.Contexts.TryGetValue(key, out var context))
            {
                clean.Contexts[key] = context;
            }
        }

        if (original.Tags.TryGetValue(ErrorReporting.Tags.Source, out var source) && SafeTagValue().IsMatch(source))
        {
            clean.TransactionName = source;
        }

        return clean;
    }

    private static SentryException ScrubException(SentryException exception) => new()
    {
        Type = exception.Type,
        Module = exception.Module,
        ThreadId = exception.ThreadId,
        Value = null,
        Mechanism = exception.Mechanism is { } mechanism
            ? new Mechanism
            {
                Type = mechanism.Type,
                Handled = mechanism.Handled,
                Synthetic = mechanism.Synthetic,
                IsExceptionGroup = mechanism.IsExceptionGroup,
                ExceptionId = mechanism.ExceptionId,
                ParentId = mechanism.ParentId,
                Source = mechanism.Source,
            }
            : null,
        Stacktrace = exception.Stacktrace is { } stack
            ? new SentryStackTrace { Frames = stack.Frames.Select(ScrubFrame).ToList(), AddressAdjustment = stack.AddressAdjustment }
            : null,
    };

    private static SentryStackFrame ScrubFrame(SentryStackFrame frame) => new()
    {
        Function = frame.Function,
        Module = frame.Module,
        Package = frame.Package,
        // The file's name only: a build outside the container would carry the builder's home folder (an OS user name) in the path.
        FileName = frame.FileName is { } file ? Path.GetFileName(file.Replace('\\', '/')) : null,
        LineNumber = frame.LineNumber,
        ColumnNumber = frame.ColumnNumber,
        InApp = frame.InApp,
        Platform = frame.Platform,
        FunctionId = frame.FunctionId,
        InstructionAddress = frame.InstructionAddress,
        AddressMode = frame.AddressMode,
        ImageAddress = frame.ImageAddress,
        SymbolAddress = frame.SymbolAddress,
    };

    /// <summary>A message survives only when it is one of our own start-up codes (<see cref="ErrorReporting.StartupCodes"/>), never other text.</summary>
    private static SentryMessage? SafeMessage(SentryMessage? message)
    {
        var text = message?.Formatted ?? message?.Message;
        return text is not null && ErrorReporting.StartupCodes.Contains(text) ? new SentryMessage { Message = text } : null;
    }

    /// <summary>Tag values are route templates, job names, type names and correlation ids: no spaces, no '@', no '?', bounded length.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\-./{}:()`\[\],+]{1,200}$")]
    private static partial Regex SafeTagValue();
}
