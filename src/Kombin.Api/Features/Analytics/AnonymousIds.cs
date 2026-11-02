using FluentValidation;

namespace Kombin.Api.Features.Analytics;

/// <summary>
/// The anonymous ID is a stable random value the app generates before login (a UUID, typically). It is sent with every event and,
/// at login or registration, to <see cref="IAnalyticsIdentityLinker"/>.
/// </summary>
internal static class AnonymousIds
{
    public const int MaxLength = 64;

    /// <summary>The same alphabet as the correlation ID; null is fine (the field is optional), an empty string is not.</summary>
    public static IRuleBuilderOptions<T, string?> MustBeAnonymousId<T>(this IRuleBuilder<T, string?> rule) =>
        rule.MaximumLength(MaxLength).Matches("^[A-Za-z0-9\\-_.:]+$");
}
