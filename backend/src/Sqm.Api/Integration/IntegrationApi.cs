using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Sqm.Api.Auth;
using Sqm.Application.Identity;
using Sqm.Application.Integration;
using Sqm.Domain.Identifiers;

namespace Sqm.Api.Integration;

/// <summary>What every integration route shares (ADR-024): its prefix, its OpenAPI document, audit, masking.</summary>
public static class IntegrationApi
{
    /// <summary>The prefix of every integration route. Versioned in the path: a breaking change is a v2 beside it.</summary>
    public const string Prefix = "/api/integration/v1";

    /// <summary>The OpenAPI document the integration routes are published in, apart from the UI's.</summary>
    public const string DocumentName = "integration-v1";

    /// <summary>The version a client reads in the status answer.</summary>
    public const string Version = "1.0";

    /// <summary>The rate-limiter policy of the token endpoint: per network address, before anyone is known.</summary>
    public const string TokenRateLimiter = "integration-token";

    /// <summary>The integration route group: tagged, published in its own document, limited per client.</summary>
    public static RouteGroupBuilder Group(IEndpointRouteBuilder app, string tag)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.MapGroup(Prefix)
            .WithTags(tag)
            .WithGroupName(DocumentName)
            .AddEndpointFilter<IntegrationRateLimitFilter>();
    }

    /// <summary>Writes an audit entry for a client: by kind and count, never an identifier's value.</summary>
    public static Task AuditAsync(
        IAuditLog audit, HttpContext http, AuthenticatedClient client, string action, AuditOutcome outcome, string? targetType,
        IReadOnlyDictionary<string, object?> detail, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(client);

        return audit.WriteAsync(new AuditEntry(
            ActorName: client.Actor,
            Action: action,
            Category: AuditCategory.Data,
            Outcome: outcome,
            TargetType: targetType,
            Ip: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            CorrelationId: http.TraceIdentifier,
            Detail: new Dictionary<string, object?>(detail, StringComparer.Ordinal) { ["client"] = client.ClientId }), ct);
    }

    /// <summary>A refused identifier, as a validation problem names its field.</summary>
    public static IResult Invalid(string field, string problem) =>
        Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [problem] });

    /// <summary>A date as the API writes it.</summary>
    public static string? Iso(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A moment as the API writes it: ISO 8601, UTC.</summary>
    public static string Iso(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>
/// How identifiers leave the integration API: in full with identifiers.reveal, masked without it - but for
/// the identifier the client sent, which it already holds, and the SIM it named by ICCID.
/// </summary>
/// <param name="Reveal">The token carries identifiers.reveal.</param>
/// <param name="Sent">The identifier the client sent, or null for a feed.</param>
internal sealed record IdentifierMasking(bool Reveal, InventoryIdentifier? Sent)
{
    /// <summary>full or masked, as an answer says it.</summary>
    public string Visibility => Reveal ? "full" : "masked";

    public string Msisdn(string value) =>
        Reveal || (Sent is { Kind: InventoryKind.Msisdn } s && s.Digits == value) ? value : IdentifierMask.Msisdn(value);

    public string Imsi(string value) =>
        Reveal || (Sent is { Imsi: { } imsi } && imsi == value) ? value : IdentifierMask.Imsi(value);

    public string? Iccid(string? value) =>
        value is null || Reveal || (Sent is { Imsi: { } imsi } && Domain.Identifiers.Iccid.FromImsi(imsi) == value)
            ? value
            : IdentifierMask.Iccid(value);

    public string Imei(string value) =>
        Reveal || (Sent is { Kind: InventoryKind.Imei } s && s.Digits == value) ? value : IdentifierMask.Imei(value);
}

/// <summary>Requests one client may make a minute, across every integration route (ADR-024).</summary>
/// <remarks>
/// An endpoint filter, not the rate-limiting middleware: the middleware runs before authentication, when
/// the client is not yet known, and a limit per network address would let one client starve another
/// behind the same gateway - Lodestone is one address for every partner it carries.
/// </remarks>
public sealed class IntegrationRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    /// <summary>Creates the limiter from the configured rate.</summary>
    public IntegrationRateLimiter(IOptions<IntegrationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var permits = Math.Max(1, options.Value.RequestsPerMinute);
        _limiter = PartitionedRateLimiter.Create<string, string>(client => RateLimitPartition.GetFixedWindowLimiter(
            client, _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    }

    /// <summary>One permit for a client, or a refused lease.</summary>
    public RateLimitLease Acquire(string clientId) => _limiter.AttemptAcquire(clientId);

    /// <inheritdoc />
    public void Dispose() => _limiter.Dispose();
}

/// <summary>Applies <see cref="IntegrationRateLimiter"/> to an integration route, after the client is known.</summary>
public sealed class IntegrationRateLimitFilter(IntegrationRateLimiter limiter) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (CurrentClient.Find(context.HttpContext) is { } client)
        {
            using var lease = limiter.Acquire(client.ClientId);
            if (!lease.IsAcquired)
            {
                var wait = lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? retry : TimeSpan.FromMinutes(1);
                context.HttpContext.Response.Headers.RetryAfter =
                    Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return Results.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests, title: "Too many requests",
                    detail: "This client has used its requests for this minute. Retry after the time in Retry-After.");
            }
        }

        return await next(context).ConfigureAwait(false);
    }
}
