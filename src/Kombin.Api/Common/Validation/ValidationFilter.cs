using System.Globalization;
using System.Reflection;
using FluentValidation;
using Kombin.Api.Common.Http;

namespace Kombin.Api.Common.Validation;

/// <summary>Runs the <see cref="IValidator{T}"/> registered for <typeparamref name="TRequest"/> before the handler.</summary>
internal sealed class ValidationFilter<TRequest> : IEndpointFilter
    where TRequest : notnull
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();

        if (request is null)
        {
            return Problems.Create(context.HttpContext, StatusCodes.Status400BadRequest, detail: "A request body is required.");
        }

        var validator = context.HttpContext.RequestServices.GetRequiredService<IValidator<TRequest>>();
        var result = await validator.ValidateAsync(request, context.HttpContext.RequestAborted);

        if (result.IsValid)
        {
            return await next(context);
        }

        var errors = result.Errors
            .GroupBy(failure => ToCamelCase(failure.PropertyName))
            .ToDictionary(group => group.Key, group => group.Select(failure => failure.ErrorMessage).Distinct().ToArray());

        // A rule can name a feature error code ("Analytics.UnknownEvent"); FluentValidation's own codes ("NotEmptyValidator") have no dot.
        var errorCode = result.Errors.Select(failure => failure.ErrorCode).FirstOrDefault(code => code?.Contains('.', StringComparison.Ordinal) == true)
            ?? Problems.ValidationFailed;

        return Problems.Validation(context.HttpContext, errors, errorCode);
    }

    private static string ToCamelCase(string propertyPath) =>
        string.Join('.', propertyPath.Split('.').Select(segment =>
            segment.Length == 0 ? segment : char.ToLowerInvariant(segment[0]) + segment[1..]));
}

internal static class ValidationExtensions
{
    public static RouteHandlerBuilder WithValidation<TRequest>(this RouteHandlerBuilder builder)
        where TRequest : notnull =>
        builder.AddEndpointFilter<ValidationFilter<TRequest>>();

    /// <summary>
    /// Registers every concrete <see cref="IValidator{T}"/> found in the assembly and pins FluentValidation's built-in
    /// messages to English, so responses do not depend on the culture of the host machine.
    /// </summary>
    public static IServiceCollection AddValidatorsFromAssembly(this IServiceCollection services, Assembly assembly)
    {
        ValidatorOptions.Global.LanguageManager.Culture = CultureInfo.InvariantCulture;

        var validators = assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsGenericTypeDefinition: false })
            .SelectMany(type => type.GetInterfaces()
                .Where(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IValidator<>))
                .Select(contract => (Contract: contract, Implementation: type)));

        foreach (var (contract, implementation) in validators)
        {
            services.AddScoped(contract, implementation);
        }

        return services;
    }
}
