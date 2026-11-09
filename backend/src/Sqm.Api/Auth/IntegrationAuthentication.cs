using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Sqm.Application.Integration;

namespace Sqm.Api.Auth;

/// <summary>
/// Bearer tokens for the integration API (ADR-024): the OAuth 2.0 access tokens PI issues to registered
/// systems. A scheme of its own, never the default: a UI route does not ask it, so a token opens no UI
/// route, and an integration route asks only it, so a session cookie opens no integration route.
/// </summary>
public sealed class IntegrationBearerHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name.</summary>
    public const string SchemeName = "IntegrationBearer";

    /// <summary>Key under which the authenticated client is stored in <c>HttpContext.Items</c>.</summary>
    public const string ClientItemKey = "sqm.integration.client";

    /// <summary>The claim carrying one scope.</summary>
    public const string ScopeClaim = "scope";

    private readonly IIntegrationClientStore _clients;

    /// <summary>Creates the handler.</summary>
    public IntegrationBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        IIntegrationClientStore clients)
        : base(options, logger, encoder) => _clients = clients;

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header))
        {
            return AuthenticateResult.NoResult();
        }

        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.Fail("Not a bearer token.");
        }

        var token = header["Bearer ".Length..].Trim();
        var client = await _clients.ResolveAsync(token, Context.RequestAborted).ConfigureAwait(false);
        if (client is null)
        {
            return AuthenticateResult.Fail("The token is unknown, expired or its client is inactive.");
        }

        Context.Items[ClientItemKey] = client;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, client.ClientId),
            new(ClaimTypes.Name, client.Actor),
        };
        claims.AddRange(client.Scopes.Select(s => new Claim(ScopeClaim, s)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    /// <inheritdoc />
    /// <remarks>RFC 6750 §3: the challenge says which scheme, and why a presented token failed.</remarks>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var failed = !string.IsNullOrEmpty(Request.Headers.Authorization.ToString());
        Response.Headers.WWWAuthenticate = failed
            ? "Bearer realm=\"quadrasense\", error=\"invalid_token\""
            : "Bearer realm=\"quadrasense\"";
        return base.HandleChallengeAsync(properties);
    }

    /// <inheritdoc />
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer realm=\"quadrasense\", error=\"insufficient_scope\"";
        return base.HandleForbiddenAsync(properties);
    }
}

/// <summary>That the authenticated client's token carries a scope.</summary>
/// <param name="scope">The scope, or null for any authenticated client.</param>
public sealed class ScopeRequirement(string? scope) : IAuthorizationRequirement
{
    /// <summary>The scope required, or null for any authenticated client.</summary>
    public string? Scope { get; } = scope;
}

/// <summary>Checks a <see cref="ScopeRequirement"/> against the client the bearer handler authenticated.</summary>
public sealed class ScopeAuthorizationHandler : AuthorizationHandler<ScopeRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        // Unauthenticated: left unhandled, so the pipeline answers 401 with a challenge rather than 403.
        if (context.Resource is HttpContext http && http.Items[IntegrationBearerHandler.ClientItemKey] is AuthenticatedClient client
            && (requirement.Scope is null || client.Has(requirement.Scope)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>The policies of the integration API.</summary>
public static class IntegrationPolicies
{
    /// <summary>The prefix of a policy naming a scope: <c>scope:inventory.read</c>.</summary>
    public const string Prefix = "scope:";

    /// <summary>Any authenticated client, whatever its scopes.</summary>
    public const string AnyClient = "integration:client";

    /// <summary>The policy name for a scope.</summary>
    public static string For(string scope) => Prefix + scope;

    /// <summary>Builds a policy: the bearer scheme only, and the scope if one is named.</summary>
    public static AuthorizationPolicy Build(string? scope) =>
        new AuthorizationPolicyBuilder(IntegrationBearerHandler.SchemeName)
            .AddRequirements(new ScopeRequirement(scope))
            .Build();
}

/// <summary>The client making an integration request.</summary>
public static class CurrentClient
{
    /// <summary>The authenticated client; throws when the endpoint forgot to require one.</summary>
    public static AuthenticatedClient Require(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Items[IntegrationBearerHandler.ClientItemKey] as AuthenticatedClient
               ?? throw new InvalidOperationException("No integration client on this request. The endpoint should require a scope.");
    }

    /// <summary>The authenticated client, or null.</summary>
    public static AuthenticatedClient? Find(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Items[IntegrationBearerHandler.ClientItemKey] as AuthenticatedClient;
    }
}
