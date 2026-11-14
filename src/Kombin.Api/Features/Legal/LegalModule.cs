using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Legal;

/// <summary>What the app needs to link to the legal texts and to know when to ask users to read them again.</summary>
internal sealed record LegalResponse(string Version, string PrivacyUrl, string TermsUrl, string DeleteAccountUrl);

/// <summary>
/// The public legal pages served by the API (<c>/legal/privacy</c>, <c>/legal/terms</c>, <c>/legal/delete-account</c>: Razor Pages in
/// <c>Features/Legal/Pages</c>, Turkish, no authentication) and <c>GET /api/v1/legal</c>. Google Play wants a public URL for the account
/// deletion page; the stores want one for the privacy policy. The texts are drafts for a lawyer: every sentence that needs one carries an
/// HTML comment <c>LAWYER</c> and the reasons are listed in <c>docs/legal-review.md</c>.
/// </summary>
internal static class LegalModule
{
    public const string PrivacyPath = "/legal/privacy";

    public const string TermsPath = "/legal/terms";

    public const string DeleteAccountPath = "/legal/delete-account";

    public static IServiceCollection AddLegalFeature(this IServiceCollection services)
    {
        services.AddOptions<LegalOptions>().BindConfiguration(LegalOptions.SectionName);

        return services;
    }

    public static IEndpointRouteBuilder MapLegalEndpoints(this IEndpointRouteBuilder app)
    {
        // The pages themselves are mapped with the other Razor Pages (MapAdminEndpoints calls MapRazorPages).
        app.MapGet("/api/v1/legal", Handle)
            .WithTags("Legal")
            .WithName("GetLegal")
            .Produces<LegalResponse>();

        return app;
    }

    /// <summary>
    /// <c>GET /api/v1/legal</c> (public): <c>{ version, privacyUrl, termsUrl, deleteAccountUrl }</c>. The app shows the version and links to the pages;
    /// a new version is a reason to show the texts again. The URLs are absolute: <c>Legal:PublicBaseUrl</c>, else the address of this request.
    /// </summary>
    private static Ok<LegalResponse> Handle(HttpContext context, IOptions<LegalOptions> options)
    {
        var legal = options.Value;
        var baseUrl = string.IsNullOrWhiteSpace(legal.PublicBaseUrl)
            ? $"{context.Request.Scheme}://{context.Request.Host}"
            : legal.PublicBaseUrl.Trim().TrimEnd('/');

        return TypedResults.Ok(new LegalResponse(legal.Version, baseUrl + PrivacyPath, baseUrl + TermsPath, baseUrl + DeleteAccountPath));
    }
}

/// <summary>Gives the legal pages the controller's details and the version.</summary>
internal abstract class LegalPageModel(IOptions<LegalOptions> options) : Microsoft.AspNetCore.Mvc.RazorPages.PageModel
{
    public LegalOptions Legal => options.Value;
}

internal sealed class PrivacyModel(IOptions<LegalOptions> options) : LegalPageModel(options);

internal sealed class TermsModel(IOptions<LegalOptions> options) : LegalPageModel(options);

internal sealed class DeleteAccountPageModel(IOptions<LegalOptions> options) : LegalPageModel(options);
