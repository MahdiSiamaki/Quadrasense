using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Sqm.Api.Auth;
using Sqm.Application.Integration;

namespace Sqm.Api.Integration;

/// <summary>
/// The integration API's OpenAPI document (ADR-024): its own document, apart from the UI's, with the
/// OAuth 2.0 client credentials flow and the scope each operation requires.
/// </summary>
/// <remarks>
/// Served at <c>/openapi/integration-v1.json</c> in Development, as the UI's is; the reviewed copy a
/// consumer reads is <c>docs/integration/openapi/pi-integration-v1.json</c>, which
/// <c>IntegrationOpenApiTests</c> holds to what the API generates.
/// </remarks>
public static class IntegrationOpenApi
{
    /// <summary>The name of the security scheme in the document.</summary>
    public const string SchemeName = "oauth2";

    /// <summary>Configures the integration document.</summary>
    public static void Configure(OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Only the integration routes: a named document otherwise takes every route without a group, which
        // is every route of the UI's API.
        options.ShouldInclude = description => description.GroupName == IntegrationApi.DocumentName;

        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = "Quadrasense PI integration API",
                Version = IntegrationApi.Version,
                Description =
                    "Primary Inventory's machine interface for the systems of the SIM and eSIM estate: Lodestone (IEH) first, "
                    + "and Tessera (PDC) where it is registered directly. OAuth 2.0 client credentials; every identifier in a "
                    + "request body, never a URL. See docs/integration/PI-API-CATALOG.md.",
            };

            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
            document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.OAuth2,
                Description = "A registered system's id and secret for a 15-minute bearer token (RFC 6749 §4.4).",
                Flows = new OpenApiOAuthFlows
                {
                    ClientCredentials = new OpenApiOAuthFlow
                    {
                        TokenUrl = new Uri($"{IntegrationApi.Prefix}/oauth/token", UriKind.Relative),
                        Scopes = IntegrationScopes.Descriptions.OrderBy(d => d.Key, StringComparer.Ordinal)
                            .ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal),
                    },
                },
            };

            return Task.CompletedTask;
        });

        // Each operation names the scope its policy requires, so the document says what a client needs.
        options.AddOperationTransformer((operation, context, _) =>
        {
            var policies = context.Description.ActionDescriptor.EndpointMetadata
                .OfType<IAuthorizeData>()
                .Select(a => a.Policy)
                .Where(p => p is not null && (p.StartsWith(IntegrationPolicies.Prefix, StringComparison.Ordinal)
                                              || p == IntegrationPolicies.AnyClient))
                .ToList();

            // The token endpoint reads its form itself, so that a request that is not a form gets RFC 6749's
            // invalid_request rather than routing's 415; the body is described here instead.
            if (operation.OperationId == "IssueIntegrationToken")
            {
                operation.RequestBody = TokenRequestBody();
            }

            if (policies.Count > 0)
            {
                var scopes = policies
                    .Where(p => p!.StartsWith(IntegrationPolicies.Prefix, StringComparison.Ordinal))
                    .Select(p => p![IntegrationPolicies.Prefix.Length..])
                    .ToList();

                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(SchemeName, context.Document)] = scopes,
                });
            }

            return Task.CompletedTask;
        });
    }

    private static OpenApiRequestBody TokenRequestBody()
    {
        static OpenApiSchema Text(string description) => new() { Type = JsonSchemaType.String, Description = description };

        return new OpenApiRequestBody
        {
            Required = true,
            Description = "RFC 6749 §4.4.2. The client authenticates with HTTP Basic, or with client_id and client_secret here.",
            Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
            {
                ["application/x-www-form-urlencoded"] = new()
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Required = new HashSet<string>(StringComparer.Ordinal) { "grant_type" },
                        Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                        {
                            ["grant_type"] = Text("Always client_credentials."),
                            ["scope"] = Text("Space-separated scopes; all the client holds when left out."),
                            ["client_id"] = Text("The client's id, when not sent with HTTP Basic."),
                            ["client_secret"] = Text("The client's secret, when not sent with HTTP Basic."),
                        },
                    },
                },
            },
        };
    }
}
