using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Sqm.Api.Integration;

/// <summary>
/// The integration API's contract for the people who read it in the product: the API guide under Settings
/// (ADR-024, amended 2026-11-10).
/// </summary>
/// <remarks>
/// <para>
/// <b>The document the running server generates</b>, not a copy shipped beside it: the guide's reference can
/// then never describe another version than the one answering. It is the same document as
/// <c>docs/integration/openapi/pi-integration-v1.json</c>, which <c>IntegrationOpenApiTests</c> holds to the
/// code.
/// </para>
/// <para>
/// <b>Behind a session, and no permission</b> (owner, 2026-11-10): it holds no data and no secret - the
/// contract is in the public repository - and a developer of another system reads it from an account that can
/// do nothing else. It is a route of the UI's API: under <c>/api/integration</c> it would need a system's
/// token, which a person does not have. The anonymous <c>/openapi/…</c> routes stay Development's.
/// </para>
/// </remarks>
public static class IntegrationGuideEndpoints
{
    /// <summary>Where the guide reads the document.</summary>
    public const string Route = "/api/v1/integration/openapi";

    /// <summary>Maps the route.</summary>
    public static IEndpointRouteBuilder MapIntegrationGuideEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(Route, DocumentAsync)
            .RequireAuthorization()
            .WithTags("Integration guide")
            .WithName("GetIntegrationOpenApi")
            .WithSummary("The integration API's OpenAPI document, as this server generates it.")
            .Produces<string>(StatusCodes.Status200OK, "application/json");

        return app;
    }

    private static async Task<IResult> DocumentAsync(
        [FromKeyedServices(IntegrationApi.DocumentName)] IOpenApiDocumentProvider provider,
        IOptionsMonitor<OpenApiOptions> options, CancellationToken ct)
    {
        var document = await provider.GetOpenApiDocumentAsync(ct).ConfigureAwait(false);

        // The server address is whichever host asked: not part of the contract, and the guide shows its own.
        document.Servers = [];

        var json = await document.SerializeAsJsonAsync(options.Get(IntegrationApi.DocumentName).OpenApiVersion, ct).ConfigureAwait(false);
        return Results.Text(json, "application/json");
    }
}
