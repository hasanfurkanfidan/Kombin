namespace Kombin.Api.Common.Results;

/// <summary>
/// An expected failure. <paramref name="Code"/> is a stable, machine-readable identifier such as
/// <c>Identity.EmailTaken</c>; <paramref name="Description"/> is safe to show to API clients.
/// </summary>
internal sealed record Error(string Code, string Description, ErrorType ErrorType)
{
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.Failure);

    public static Error Validation(string code, string description) => new(code, description, ErrorType.Validation);

    public static Error Unauthorized(string code, string description) => new(code, description, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string description) => new(code, description, ErrorType.Forbidden);

    public static Error NotFound(string code, string description) => new(code, description, ErrorType.NotFound);

    public static Error Conflict(string code, string description) => new(code, description, ErrorType.Conflict);

    public static Error Unavailable(string code, string description) => new(code, description, ErrorType.Unavailable);

    public static Error Failure(string code, string description) => new(code, description, ErrorType.Failure);
}
