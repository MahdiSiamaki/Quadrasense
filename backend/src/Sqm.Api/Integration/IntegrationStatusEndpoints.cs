using Microsoft.Extensions.Options;
using Sqm.Api.Auth;
using Sqm.Application.DataImport;
using Sqm.Application.Identity;
using Sqm.Application.Integration;
using Sqm.Application.Quality;
using Sqm.Application.Timeline;
using Sqm.Contracts.Integration;
using Sqm.Contracts.Quality;

namespace Sqm.Api.Integration;

/// <summary>
/// The state of PI's data for a client deciding whether to read now, and the data-quality figures BI and
/// SNQM read (ADR-024). Aggregates only: nothing here names a subscriber.
/// </summary>
public static class IntegrationStatusEndpoints
{
    /// <summary>Maps the routes.</summary>
    public static IEndpointRouteBuilder MapIntegrationStatusEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var api = IntegrationApi.Group(app, "Integration: status and quality");

        api.MapGet("/status", StatusAsync).RequireAuthorization(IntegrationPolicies.AnyClient)
            .WithName("IntegrationStatus")
            .WithSummary("The calling client, its scopes, and how fresh PI's data is.")
            .Produces<IntegrationStatusResponse>();

        var quality = IntegrationPolicies.For(IntegrationScopes.QualityRead);

        api.MapGet("/quality/feed", FeedAsync).RequireAuthorization(quality)
            .WithName("IntegrationFeedQuality")
            .WithSummary("Each daily file judged against the ordinary days: shifted IMEIs, SIMs with several numbers, malformed IMEIs.")
            .Produces<FeedQualityResponse>().ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapGet("/quality/signals", SignalsAsync).RequireAuthorization(quality)
            .WithName("IntegrationQualitySignals")
            .WithSummary("Data-quality signals over the current state: invalid identifiers, contradictions, binding lifetimes.")
            .Produces<QualitySignalsResponse>();

        return app;
    }

    private static async Task<IResult> StatusAsync(
        HttpContext http, ITimelineStore timelines, IImportJobRepository imports, TimeProvider clock, CancellationToken ct)
    {
        var client = CurrentClient.Require(http);
        var readiness = await timelines.GetReadinessAsync(ct).ConfigureAwait(false);
        var through = await timelines.DataThroughAsync(ct).ConfigureAwait(false);
        var freshness = await imports.GetFreshnessAsync(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), ct).ConfigureAwait(false);

        return Results.Ok(new IntegrationStatusResponse(
            IntegrationApi.Version, client.ClientId, [.. client.Scopes.Order(StringComparer.Ordinal)], IntegrationApi.Iso(through),
            readiness.ToString(),
            [.. freshness.Select(f => new SourceStatusInfo(
                f.SourceCode, IntegrationApi.Iso(f.LatestBusinessDate), f.DaysBehind, f.MissingBusinessDates.Distinct().Count()))]));
    }

    private static async Task<IResult> FeedAsync(
        DateOnly? from, DateOnly? to, HttpContext http, IFeedQualityReader reader, IOptions<FeedQualityOptions> options,
        IAuditLog audit, CancellationToken ct)
    {
        var client = CurrentClient.Require(http);
        if (from is { } f && to is { } t && f > t)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The range is backwards.",
                detail: $"from ({f:yyyy-MM-dd}) is after to ({t:yyyy-MM-dd}).");
        }

        var answer = await QualityQueries.FeedQualityAsync(reader, options.Value, from, to, ct).ConfigureAwait(false);
        await IntegrationApi.AuditAsync(audit, http, client, "integration.quality.feed", AuditOutcome.Success, "feed-quality",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["days"] = answer.Days.Count }, ct).ConfigureAwait(false);
        return Results.Ok(answer);
    }

    private static async Task<IResult> SignalsAsync(HttpContext http, IQualityReader reader, IAuditLog audit, CancellationToken ct)
    {
        var client = CurrentClient.Require(http);
        var answer = await QualityQueries.SignalsAsync(reader, ct).ConfigureAwait(false);
        await IntegrationApi.AuditAsync(audit, http, client, "integration.quality.signals", AuditOutcome.Success, "quality-signals",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["available"] = answer.Available }, ct).ConfigureAwait(false);
        return Results.Ok(answer);
    }
}
