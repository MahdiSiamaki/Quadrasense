using System.Text;
using Microsoft.Extensions.Options;
using Sqm.Application.Identity;
using Sqm.Application.Integration;
using Sqm.Contracts.Integration;

namespace Sqm.Api.Integration;

/// <summary>
/// The OAuth 2.0 token endpoint (RFC 6749 §4.4, the client credentials grant): a registered system trades
/// its id and secret for a short-lived bearer token carrying its scopes (ADR-024).
/// </summary>
/// <remarks>
/// <para>
/// The client authenticates with HTTP Basic, which RFC 6749 §2.3.1 requires a server to support, or with
/// <c>client_id</c> and <c>client_secret</c> in the form body, which it allows. Never in the query string.
/// </para>
/// <para>
/// Anonymous by necessity - it is where a client becomes known - and so limited per network address
/// before anything else runs, and every refusal is audited by client id, never by secret.
/// </para>
/// </remarks>
public static class IntegrationTokenEndpoints
{
    /// <summary>Maps the token endpoint.</summary>
    public static IEndpointRouteBuilder MapIntegrationTokenEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost($"{IntegrationApi.Prefix}/oauth/token", TokenAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .RequireRateLimiting(IntegrationApi.TokenRateLimiter)
            .WithTags("Integration: authentication")
            .WithGroupName(IntegrationApi.DocumentName)
            .WithName("IssueIntegrationToken")
            .WithSummary("OAuth 2.0 client credentials: a registered system's id and secret for a bearer token.")
            .Produces<TokenResponse>()
            .Produces<TokenError>(StatusCodes.Status400BadRequest)
            .Produces<TokenError>(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static async Task<IResult> TokenAsync(
        HttpContext http, IIntegrationClientStore clients, IAuditLog audit, IOptions<IntegrationOptions> options, CancellationToken ct)
    {
        // RFC 6749 §5.1: a token response must not be cached anywhere.
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";

        if (!http.Request.HasFormContentType)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "Send application/x-www-form-urlencoded, as RFC 6749 §4.4.2 says.");
        }

        var form = await http.Request.ReadFormAsync(ct).ConfigureAwait(false);
        if (form["grant_type"].ToString() != "client_credentials")
        {
            return Error(StatusCodes.Status400BadRequest, "unsupported_grant_type", "Only client_credentials is supported.");
        }

        var (clientId, secret, basic) = Credentials(http, form);
        if (clientId is null || secret is null)
        {
            return Error(StatusCodes.Status401Unauthorized, "invalid_client",
                "Authenticate with HTTP Basic, or client_id and client_secret in the body.", basic);
        }

        var requested = form.ContainsKey("scope") ? IntegrationScopes.Parse(form["scope"].ToString()) : null;
        if (form.ContainsKey("scope") && requested is null)
        {
            await AuditAsync(audit, http, clientId, AuditOutcome.Failure, "unknown scope", ct).ConfigureAwait(false);
            return Error(StatusCodes.Status400BadRequest, "invalid_scope", "A scope named is not one this API knows.");
        }

        var grant = await clients.GrantAsync(clientId, secret, requested, ct).ConfigureAwait(false);
        switch (grant.Outcome)
        {
            case TokenGrantOutcome.InvalidClient:
                await AuditAsync(audit, http, clientId, AuditOutcome.Failure, "invalid client", ct).ConfigureAwait(false);
                return Error(StatusCodes.Status401Unauthorized, "invalid_client", "Unknown client, wrong secret or an inactive client.", basic);

            case TokenGrantOutcome.InvalidScope:
                await AuditAsync(audit, http, clientId, AuditOutcome.Failure, "scope not held", ct).ConfigureAwait(false);
                return Error(StatusCodes.Status400BadRequest, "invalid_scope", "A scope named is not one this client holds.");
        }

        await AuditAsync(audit, http, clientId, AuditOutcome.Success, null, ct, grant.Scopes).ConfigureAwait(false);

        return Results.Ok(new TokenResponse(
            grant.AccessToken!, "Bearer", (int)options.Value.TokenLifetime.TotalSeconds, IntegrationScopes.Format(grant.Scopes)));
    }

    /// <summary>The client's id and secret, from HTTP Basic or the form; and whether Basic was used.</summary>
    private static (string? ClientId, string? Secret, bool Basic) Credentials(HttpContext http, IFormCollection form)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
                var colon = decoded.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    // RFC 6749 §2.3.1: both are form-urlencoded before they are joined.
                    return (Uri.UnescapeDataString(decoded[..colon]), Uri.UnescapeDataString(decoded[(colon + 1)..]), true);
                }
            }
            catch (FormatException)
            {
                // A malformed header is a client that failed to authenticate, answered below.
            }

            return (null, null, true);
        }

        var id = form["client_id"].ToString();
        var secret = form["client_secret"].ToString();
        return (id.Length == 0 ? null : id, secret.Length == 0 ? null : secret, false);
    }

    private static TokenErrorResult Error(int status, string error, string description, bool basic = false) =>
        new(status, new TokenError(error, description), basic);

    private static Task AuditAsync(
        IAuditLog audit, HttpContext http, string clientId, AuditOutcome outcome, string? reason, CancellationToken ct,
        IReadOnlySet<string>? scopes = null)
    {
        var detail = new Dictionary<string, object?>(StringComparer.Ordinal) { ["client"] = clientId };
        if (reason is not null)
        {
            detail["reason"] = reason;
        }

        if (scopes is not null)
        {
            detail["scopes"] = IntegrationScopes.Format(scopes);
        }

        return audit.WriteAsync(new AuditEntry(
            ActorName: $"client:{(clientId.Length > 64 ? clientId[..64] : clientId)}",
            Action: outcome == AuditOutcome.Success ? "integration.token.issued" : "integration.token.refused",
            Category: AuditCategory.Authentication,
            Outcome: outcome,
            TargetType: "integration-client",
            Ip: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            CorrelationId: http.TraceIdentifier,
            Detail: detail), ct);
    }

    /// <summary>An RFC 6749 §5.2 error: its own JSON, not a problem document, because OAuth clients read this one.</summary>
    private sealed class TokenErrorResult(int status, TokenError error, bool basic) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.StatusCode = status;
            if (status == StatusCodes.Status401Unauthorized && basic)
            {
                httpContext.Response.Headers.WWWAuthenticate = "Basic realm=\"quadrasense\"";
            }

            return httpContext.Response.WriteAsJsonAsync(error, httpContext.RequestAborted);
        }
    }
}
