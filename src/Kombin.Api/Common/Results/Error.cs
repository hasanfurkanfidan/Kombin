namespace Kombin.Api.Common.Results;

/// <summary>
/// An expected failure. <paramref name="Code"/> is a stable, machine-readable identifier such as
/// <c>Identity.EmailTaken</c>; <paramref name="Description"/> is safe to show to API clients.
/// </summary>
internal sealed record Error(string Code, string Description, ErrorType ErrorType)
{
    /// <summary>Extra members of the problem document (for example a date the client needs), next to <c>errorCode</c> and <c>correlationId</c>.</summary>
    public IReadOnlyDictionary<string, object?>? Extensions { get; init; }

    public Error WithExtension(string name, object? value)
    {
        var extensions = new Dictionary<string, object?>(Extensions ?? new Dictionary<string, object?>()) { [name] = value };

        return this with { Extensions = extensions };
    }

    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.Failure);

    public static Error Validation(string code, string description) => new(code, description, ErrorType.Validation);

    public static Error Unauthorized(string code, string description) => new(code, description, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string description) => new(code, description, ErrorType.Forbidden);

    public static Error NotFound(string code, string description) => new(code, description, ErrorType.NotFound);

    public static Error Conflict(string code, string description) => new(code, description, ErrorType.Conflict);

    public static Error Unavailable(string code, string description) => new(code, description, ErrorType.Unavailable);

    public static Error Failure(string code, string description) => new(code, description, ErrorType.Failure);
}
