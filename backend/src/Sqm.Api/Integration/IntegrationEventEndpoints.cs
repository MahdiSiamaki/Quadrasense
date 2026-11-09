using Microsoft.Extensions.Options;
using Sqm.Api.Auth;
using Sqm.Application.Identity;
using Sqm.Application.Integration;
using Sqm.Contracts.Integration;

namespace Sqm.Api.Integration;

/// <summary>
/// Events both ways between PI and Lodestone (ADR-024): PI's feed, which Lodestone pulls with a cursor,
/// and the declared events Lodestone delivers from CRM.
/// </summary>
/// <remarks>
/// <para>
/// <b>Out: pulled, not pushed.</b> PI opens no connection to anyone (MCI red lines, §1-4 row 4: egress has
/// to be requested), and a feed read with a cursor survives a reader that was down: it resumes where it
/// stopped. Delivery is at least once - a reader may see an event again after a failure - so each carries
/// an id to de-duplicate on.
/// </para>
/// <para>
/// <b>In: idempotent.</b> The sender's event id is the key. A retry is stored once and answered as a
/// duplicate; the same id with different content is refused as a conflict, because one of the two is
/// wrong and PI cannot tell which.
/// </para>
/// </remarks>
public static class IntegrationEventEndpoints
{
    /// <summary>Maps the routes.</summary>
    public static IEndpointRouteBuilder MapIntegrationEventEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var api = IntegrationApi.Group(app, "Integration: events");

        api.MapGet("/events", FeedAsync).RequireAuthorization(IntegrationPolicies.For(IntegrationScopes.EventsRead))
            .WithName("IntegrationEventFeed")
            .WithSummary("PI's events after a cursor, oldest first: inventory.day-imported, catalogue.version-activated.")
            .Produces<IntegrationEventPage>().ProducesValidationProblem();

        api.MapPost("/events/declared", DeclaredAsync).RequireAuthorization(IntegrationPolicies.For(IntegrationScopes.EventsWrite))
            .WithName("IntegrationDeclaredEvents")
            .WithSummary("CRM's SIM swaps, activations and deactivations, delivered by Lodestone. Idempotent by event id.")
            .Produces<DeclaredEventsResponse>()
            .Produces<DeclaredEventsResponse>(StatusCodes.Status422UnprocessableEntity)
            .ProducesValidationProblem();

        return app;
    }

    private static async Task<IResult> FeedAsync(
        long? after, int? limit, HttpContext http, IIntegrationOutbox outbox, IAuditLog audit, IOptions<IntegrationOptions> options,
        CancellationToken ct)
    {
        var client = CurrentClient.Require(http);
        var max = options.Value.MaxEventsPage;
        var cursor = after ?? 0;
        var size = limit ?? 100;

        if (cursor < 0)
        {
            return IntegrationApi.Invalid("after", "The sequence of the last event read, or 0 to start from the oldest kept.");
        }

        if (size < 1 || size > max)
        {
            return IntegrationApi.Invalid("limit", $"Between 1 and {max}.");
        }

        var page = await outbox.ReadAsync(cursor, size, ct).ConfigureAwait(false);

        await IntegrationApi.AuditAsync(audit, http, client, "integration.events.read", AuditOutcome.Success, "event-feed",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["after"] = cursor, ["events"] = page.Events.Count }, ct)
            .ConfigureAwait(false);

        return Results.Ok(new IntegrationEventPage(
            [.. page.Events.Select(e => new IntegrationEventInfo(e.Sequence, e.EventId, e.Type, e.Subject, IntegrationApi.Iso(e.OccurredAt), e.Payload))],
            page.Events.Count > 0 ? page.Events[^1].Sequence : cursor,
            page.OldestAvailable,
            page.Latest));
    }

    private static async Task<IResult> DeclaredAsync(
        DeclaredEventsDelivery delivery, HttpContext http, IDeclaredEventStore store, IAuditLog audit, TimeProvider clock,
        IOptions<IntegrationOptions> options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var client = CurrentClient.Require(http);
        var max = options.Value.MaxDeclaredEventsPerRequest;

        if (delivery.Events is not { Count: > 0 } events || events.Count > max)
        {
            return IntegrationApi.Invalid("events", $"Between 1 and {max} events.");
        }

        var now = clock.GetUtcNow();
        var results = new List<DeclaredEventResult>(events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e is null)
            {
                results.Add(new DeclaredEventResult(i, null, "invalid", new Dictionary<string, string>(StringComparer.Ordinal) { ["event"] = "Missing." }));
                continue;
            }

            var (checkedEvent, problems) = DeclaredEventRules.Check(
                e.EventId, e.Source, e.Type, e.OccurredAt, e.Msisdn, e.Imsi, e.Iccid, e.PreviousImsi, e.PreviousIccid, e.Reference, now);
            if (checkedEvent is null)
            {
                results.Add(new DeclaredEventResult(i, e.EventId, "invalid", problems));
                continue;
            }

            var outcome = await store.RecordAsync(client.Id, checkedEvent, ct).ConfigureAwait(false);
            results.Add(new DeclaredEventResult(i, checkedEvent.EventId, outcome switch
            {
                DeclaredEventOutcome.Accepted => "accepted",
                DeclaredEventOutcome.Duplicate => "duplicate",
                _ => "conflict",
            }, null));
        }

        var accepted = results.Count(r => r.Outcome == "accepted");
        var duplicates = results.Count(r => r.Outcome == "duplicate");
        var rejected = results.Count - accepted - duplicates;

        await IntegrationApi.AuditAsync(audit, http, client, "integration.events.declared",
            rejected == 0 ? AuditOutcome.Success : AuditOutcome.Failure, "declared-event",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["events"] = results.Count, ["accepted"] = accepted, ["duplicates"] = duplicates, ["rejected"] = rejected,
            }, ct).ConfigureAwait(false);

        var answer = new DeclaredEventsResponse(accepted, duplicates, rejected, results);

        // Every event is answered on its own and the valid ones are stored whatever their neighbours did; a
        // 422 says that at least one needs the sender's attention, so that a client reading only the status
        // cannot take a partial delivery for a whole one.
        return rejected == 0 ? Results.Ok(answer) : Results.UnprocessableEntity(answer);
    }
}
