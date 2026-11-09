using Sqm.Api.Auth;
using Sqm.Application.Identity;
using Sqm.Application.Integration;
using Sqm.Contracts.Integration;

namespace Sqm.Api.Integration;

/// <summary>
/// Registering the systems that call the integration API (ADR-024): a person's routes, behind a session and
/// integration.manage, under the UI's API - not the integration API, which no person reaches.
/// </summary>
/// <remarks>
/// A secret is shown once, in the answer that issued it, and never again: only its hash is kept. Every
/// change is audited, by client id and scopes.
/// </remarks>
public static class IntegrationClientEndpoints
{
    /// <summary>Maps the routes.</summary>
    public static IEndpointRouteBuilder MapIntegrationClientEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/integration/clients").WithTags("Integration clients")
            .RequireAuthorization(PermissionPolicyProvider.Prefix + Permissions.IntegrationManage);

        group.MapGet("/", ListAsync).WithName("ListIntegrationClients")
            .WithSummary("Every registered system and the scopes there are.");
        group.MapPost("/", CreateAsync).WithName("CreateIntegrationClient")
            .WithSummary("Registers a system and issues its secret, shown once.");
        group.MapPut("/{id:long}", UpdateAsync).WithName("UpdateIntegrationClient")
            .WithSummary("Changes a system's name, scopes or state; deactivating it ends its tokens.");
        group.MapPost("/{id:long}/secret", RotateAsync).WithName("RotateIntegrationClientSecret")
            .WithSummary("Issues a new secret, shown once; the old one and every token stop working.");

        return app;
    }

    private static async Task<IResult> ListAsync(IIntegrationClientStore clients, CancellationToken ct)
    {
        var all = await clients.ListAsync(ct).ConfigureAwait(false);
        return Results.Ok(new IntegrationClientList(
            [.. all.Select(Info)],
            [.. IntegrationScopes.Descriptions.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => new IntegrationScopeInfo(d.Key, d.Value))]));
    }

    private static async Task<IResult> CreateAsync(
        IntegrationClientCreateRequest request, HttpContext http, IIntegrationClientStore clients, IAuditLog audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var problems = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var clientId = request.ClientId?.Trim() ?? string.Empty;
        if (!IsClientId(clientId))
        {
            problems["clientId"] = ["Lower-case letters, digits and hyphens, 3 to 48, starting with a letter: \"lodestone\"."];
        }

        var (name, description, scopes) = Check(request.DisplayName, request.Description, request.Scopes, problems);
        if (problems.Count > 0)
        {
            return Results.ValidationProblem(problems);
        }

        var user = CurrentUser.Require(http);
        var issued = await clients.CreateAsync(new NewIntegrationClient(clientId, name, description, scopes), user.Username, ct)
            .ConfigureAwait(false);
        if (issued is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Client id taken",
                detail: $"A client named \"{clientId}\" is registered already.");
        }

        await AuditAsync(audit, http, user, "integration.client.create", issued.Client, ct).ConfigureAwait(false);
        return Results.Created($"/api/v1/integration/clients/{issued.Client.Id}",
            new IntegrationClientSecretResponse(Info(issued.Client), issued.Secret));
    }

    private static async Task<IResult> UpdateAsync(
        long id, IntegrationClientUpdateRequest request, HttpContext http, IIntegrationClientStore clients, IAuditLog audit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var problems = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var (name, description, scopes) = Check(request.DisplayName, request.Description, request.Scopes, problems);
        if (problems.Count > 0)
        {
            return Results.ValidationProblem(problems);
        }

        var user = CurrentUser.Require(http);
        var updated = await clients.UpdateAsync(id, new IntegrationClientChange(name, description, scopes, request.IsActive), user.Username, ct)
            .ConfigureAwait(false);
        if (updated is null)
        {
            return Results.NotFound();
        }

        await AuditAsync(audit, http, user, updated.IsActive ? "integration.client.update" : "integration.client.deactivate", updated, ct)
            .ConfigureAwait(false);
        return Results.Ok(Info(updated));
    }

    private static async Task<IResult> RotateAsync(
        long id, HttpContext http, IIntegrationClientStore clients, IAuditLog audit, CancellationToken ct)
    {
        var user = CurrentUser.Require(http);
        var issued = await clients.RotateSecretAsync(id, user.Username, ct).ConfigureAwait(false);
        if (issued is null)
        {
            return Results.NotFound();
        }

        await AuditAsync(audit, http, user, "integration.client.secret", issued.Client, ct).ConfigureAwait(false);
        return Results.Ok(new IntegrationClientSecretResponse(Info(issued.Client), issued.Secret));
    }

    private static (string Name, string Description, IReadOnlyList<string> Scopes) Check(
        string? displayName, string? description, IReadOnlyList<string>? scopes, Dictionary<string, string[]> problems)
    {
        var name = displayName?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100)
        {
            problems["displayName"] = ["Required, at most 100 characters."];
        }

        var about = description?.Trim() ?? string.Empty;
        if (about.Length > 500)
        {
            problems["description"] = ["At most 500 characters."];
        }

        var wanted = (scopes ?? []).Select(s => s.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var unknown = wanted.Where(s => !IntegrationScopes.All.Contains(s)).ToList();
        if (wanted.Count == 0)
        {
            problems["scopes"] = ["At least one scope."];
        }
        else if (unknown.Count > 0)
        {
            problems["scopes"] = [$"Unknown: {string.Join(", ", unknown)}."];
        }

        return (name, about, [.. wanted.Order(StringComparer.Ordinal)]);
    }

    private static bool IsClientId(string value) =>
        value.Length is >= 3 and <= 48 && char.IsAsciiLetterLower(value[0])
        && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    private static IntegrationClientInfo Info(IntegrationClient c) => new(
        c.Id, c.ClientId, c.DisplayName, c.Description, c.Scopes, c.IsActive, IntegrationApi.Iso(c.SecretSetAt),
        c.LastTokenAt is { } last ? IntegrationApi.Iso(last) : null, IntegrationApi.Iso(c.CreatedAt), c.CreatedBy,
        IntegrationApi.Iso(c.UpdatedAt), c.UpdatedBy);

    private static Task AuditAsync(
        IAuditLog audit, HttpContext http, AuthenticatedUser user, string action, IntegrationClient client, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(
            ActorName: user.Username,
            Action: action,
            Category: AuditCategory.System,
            Outcome: AuditOutcome.Success,
            ActorUserId: user.UserId,
            TargetType: "integration-client",
            TargetId: CurrentUser.Id(client.Id),
            TargetName: client.ClientId,
            Ip: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            CorrelationId: http.TraceIdentifier,
            Detail: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["scopes"] = IntegrationScopes.Format(client.Scopes),
                ["active"] = client.IsActive,
            }), ct);
}
