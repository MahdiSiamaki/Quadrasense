using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Sqm.Api.Auth;
using Sqm.Api.Endpoints;
using Sqm.Application.Abstractions;
using Sqm.Application.Devices;
using Sqm.Application.Identity;
using Sqm.Application.Integration;
using Sqm.Application.Quality;
using Sqm.Application.Risk;
using Sqm.Application.Timeline;
using Sqm.Contracts.Integration;
using Sqm.Contracts.Lookup;
using Sqm.Domain.Identifiers;
using Sqm.Domain.Risk;

namespace Sqm.Api.Integration;

/// <summary>
/// The inventory a registered system may read (ADR-024): what PI knows of a number, a SIM or a handset,
/// what it was bound to and when, the facts a policy is written against, a device model, and each day's
/// changes for a copy kept in step.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identifiers travel in bodies.</b> Every read that names a number, a SIM or a handset is a POST, as
/// the UI's lookups are (MCI red lines, §2-3 row 7): a GET would put the identifier in access logs.
/// </para>
/// <para>
/// <b>The same reads as the UI.</b> Lookups and history are the binding history the Explorer's timeline
/// draws; relationships are the relationship explorer's; facts add the device catalogue and the risk
/// assessment a person sees on the risk page. Nothing here is a second copy of a rule.
/// </para>
/// <para>
/// <b>PI answers what it observed.</b> No answer here is a decision - eligibility, blocking and grey lists
/// are Tessera's - and none holds a key value: PI has none.
/// </para>
/// </remarks>
public static class IntegrationInventoryEndpoints
{
    private static readonly TimelineVisibility Full = new(true, true, true, (_, value) => value, false);

    /// <summary>Maps the routes.</summary>
    public static IEndpointRouteBuilder MapIntegrationInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var read = IntegrationPolicies.For(IntegrationScopes.InventoryRead);
        var api = IntegrationApi.Group(app, "Integration: inventory");

        api.MapPost("/inventory/lookup", LookupAsync).RequireAuthorization(read)
            .WithName("IntegrationLookup")
            .WithSummary("A number, SIM (IMSI or ICCID) or handset: its bindings, active and ended, with derived ICCIDs and models.")
            .Produces<InventoryLookupResponse>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapPost("/inventory/lookup/batch", BatchAsync).RequireAuthorization(read)
            .WithName("IntegrationBatchLookup")
            .WithSummary("Several identifiers at once, each answered as a lookup or refused on its own.")
            .Produces<BatchLookupResponse>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapPost("/inventory/relationships", RelationshipsAsync).RequireAuthorization(read)
            .WithName("IntegrationRelationships")
            .WithSummary("Every number, SIM and handset connected to one identifier, with counts.")
            .Produces<RelationshipGraph>().ProducesValidationProblem();

        api.MapPost("/inventory/history", HistoryAsync).RequireAuthorization(read)
            .WithName("IntegrationHistory")
            .WithSummary("Every binding of one identifier over time, and what CRM declared about it.")
            .Produces<InventoryHistoryResponse>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapGet("/devices/models/{tac:regex(^[0-9]{{8}}$)}", ModelAsync).RequireAuthorization(read)
            .WithName("IntegrationDeviceModel")
            .WithSummary("What a TAC is: make, model, device type and capabilities, from the active GSMA catalogue.")
            .Produces<DeviceModelInfo>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/inventory/facts", FactsAsync).RequireAuthorization(IntegrationPolicies.For(IntegrationScopes.FactsRead))
            .WithName("IntegrationFacts")
            .WithSummary("The device and SIM facts a policy decision is written against: observations, never a decision.")
            .Produces<InventoryFactsResponse>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapPost("/inventory/changes", ChangesAsync).RequireAuthorization(IntegrationPolicies.For(IntegrationScopes.InventorySync))
            .WithName("IntegrationBindingChanges")
            .WithSummary("One day's binding adds and removes, a page at a time, for a copy kept in step.")
            .Produces<BindingChangesResponse>().ProducesValidationProblem();

        return app;
    }

    // ------------------------------------------------------------------ lookup

    private static async Task<IResult> LookupAsync(
        IdentifierRequest request, HttpContext http, ITimelineStore timelines, IAuditLog audit, IOptions<IntegrationOptions> options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var client = CurrentClient.Require(http);

        if (!TryIdentify(request, out var id, out var refusal))
        {
            return refusal;
        }

        var masking = new IdentifierMasking(client.Has(IntegrationScopes.IdentifiersReveal), id);
        var (answer, unavailable) = await LookupCoreAsync(id, timelines, masking, options.Value.MaxBindings, ct).ConfigureAwait(false);
        if (answer is null)
        {
            return unavailable!;
        }

        await IntegrationApi.AuditAsync(audit, http, client, "integration.inventory.lookup", AuditOutcome.Success,
            InventoryIdentifier.Name(id.Kind),
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["found"] = answer.Found, ["bindings"] = answer.Bindings.Count, ["masked"] = !masking.Reveal,
            }, ct).ConfigureAwait(false);

        return Results.Ok(answer);
    }

    private static async Task<IResult> BatchAsync(
        BatchLookupRequest request, HttpContext http, ITimelineStore timelines, IAuditLog audit, IOptions<IntegrationOptions> options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var client = CurrentClient.Require(http);
        var max = options.Value.MaxBatch;

        if (request.Identifiers is not { Count: > 0 } identifiers || identifiers.Count > max)
        {
            return IntegrationApi.Invalid("identifiers", $"Between 1 and {max} identifiers.");
        }

        // One after another, never in parallel: the laptop's ClickHouse runs one heavy statement at a time,
        // and a batch is a client's convenience, not a reason to take the store from everybody else.
        var reveal = client.Has(IntegrationScopes.IdentifiersReveal);
        var items = new List<BatchLookupItem>(identifiers.Count);
        for (var i = 0; i < identifiers.Count; i++)
        {
            var one = identifiers[i];
            if (!InventoryIdentifier.TryParse(one?.Identifier, InventoryIdentifier.KindOf(one?.Kind), out var id, out var problem)
                || (one?.Kind is { } named && InventoryIdentifier.KindOf(named) is null))
            {
                items.Add(new BatchLookupItem(i, "invalid", problem ?? "Unknown kind: msisdn, imsi, iccid or imei.", null));
                continue;
            }

            var (answer, unavailable) = await LookupCoreAsync(id, timelines, new IdentifierMasking(reveal, id), options.Value.MaxBindings, ct)
                .ConfigureAwait(false);
            if (answer is null)
            {
                return unavailable!;
            }

            items.Add(new BatchLookupItem(i, answer.Found ? "found" : "notFound", null, answer));
        }

        await IntegrationApi.AuditAsync(audit, http, client, "integration.inventory.lookup.batch", AuditOutcome.Success, "batch",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["identifiers"] = items.Count,
                ["found"] = items.Count(x => x.Outcome == "found"),
                ["invalid"] = items.Count(x => x.Outcome == "invalid"),
                ["masked"] = !reveal,
            }, ct).ConfigureAwait(false);

        return Results.Ok(new BatchLookupResponse(items));
    }

    /// <summary>One identifier's bindings, from the binding history; or the answer that says why not.</summary>
    private static async Task<(InventoryLookupResponse? Answer, IResult? Unavailable)> LookupCoreAsync(
        InventoryIdentifier id, ITimelineStore timelines, IdentifierMasking masking, int max, CancellationToken ct)
    {
        if (await HistoryRefusalAsync(timelines, ct).ConfigureAwait(false) is { } refused)
        {
            return (null, refused);
        }

        var (centre, digits) = Centre(id);
        var stored = await timelines.GetAsync(centre, digits, max, ct).ConfigureAwait(false);
        var through = await timelines.DataThroughAsync(ct).ConfigureAwait(false);

        // Built unmasked so that each ICCID is derived from its real IMSI; masked below, field by field.
        var timeline = TimelineBuilder.Build(centre, digits, stored, Full, max, through);

        var bindings = timeline.Bindings.Select(b =>
        {
            var imsi = b.Imsi ?? string.Empty;
            return new InventoryBindingInfo(
                masking.Msisdn(b.Msisdn ?? string.Empty), masking.Imsi(imsi), masking.Iccid(Iccid.FromImsi(imsi)), "derived",
                masking.Imei(b.Imei ?? string.Empty), b.Tac, b.Brand, b.Model, b.Active, b.FirstSeen, b.FirstSeenIsDumpWindow,
                b.LastChange);
        }).ToList();

        return (new InventoryLookupResponse(
            InventoryIdentifier.Name(id.Kind), id.Digits, id.Kind == InventoryKind.Iccid ? id.Imsi : null,
            timeline.Summary.Bindings > 0, Summary(timeline.Summary), bindings, timeline.Truncated, masking.Visibility,
            IntegrationApi.Iso(through)), null);
    }

    // ------------------------------------------------------------------ relationships

    private static async Task<IResult> RelationshipsAsync(
        IdentifierRequest request, HttpContext http, IDeviceAnalyticsStore devices, IAuditLog audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var client = CurrentClient.Require(http);

        if (!TryIdentify(request, out var id, out var refusal))
        {
            return refusal;
        }

        var (kind, digits) = id.Kind switch
        {
            InventoryKind.Msisdn => (RelatedKind.Msisdn, id.Digits),
            InventoryKind.Imei => (RelatedKind.Imei, id.Digits),
            _ => (RelatedKind.Imsi, id.Imsi!),
        };

        var graph = await devices.GetRelationshipsAsync(kind, digits, new RelationshipSections(true, true, true), ct).ConfigureAwait(false);
        var reveal = client.Has(IntegrationScopes.IdentifiersReveal);
        var masking = new IdentifierMasking(reveal, id);
        var answer = reveal ? graph : graph with
        {
            Subscribers = [.. graph.Subscribers.Select(n => n with { Value = masking.Msisdn(n.Value) })],
            Sims = [.. graph.Sims.Select(n => n with { Value = masking.Imsi(n.Value) })],
            Handsets = [.. graph.Handsets.Select(n => n with { Value = masking.Imei(n.Value) })],
            Paired = [.. graph.Paired.Select(p => p with { Imei = masking.Imei(p.Imei) })],
            Identifiers = IdentifierVisibility.Masked,
        };

        await IntegrationApi.AuditAsync(audit, http, client, "integration.inventory.relationships", AuditOutcome.Success,
            InventoryIdentifier.Name(id.Kind),
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["found"] = graph.Found,
                ["subscribers"] = graph.Subscribers.Count, ["sims"] = graph.Sims.Count, ["handsets"] = graph.Handsets.Count,
                ["masked"] = !reveal,
            }, ct).ConfigureAwait(false);

        return Results.Ok(answer);
    }

    // ------------------------------------------------------------------ history

    private static async Task<IResult> HistoryAsync(
        IdentifierRequest request, HttpContext http, ITimelineStore timelines, IDeclaredEventStore declared, IAuditLog audit,
        IOptions<IntegrationOptions> options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var client = CurrentClient.Require(http);

        if (!TryIdentify(request, out var id, out var refusal))
        {
            return refusal;
        }

        if (await HistoryRefusalAsync(timelines, ct).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var reveal = client.Has(IntegrationScopes.IdentifiersReveal);
        var masking = new IdentifierMasking(reveal, id);
        var max = options.Value.MaxBindings;
        var (centre, digits) = Centre(id);

        var stored = await timelines.GetAsync(centre, digits, max, ct).ConfigureAwait(false);
        var through = await timelines.DataThroughAsync(ct).ConfigureAwait(false);
        var visibility = new TimelineVisibility(true, true, true, reveal ? (_, value) => value : Mask, !reveal);
        var timeline = TimelineBuilder.Build(centre, digits, stored, visibility, max, through);
        var events = await DeclaredForAsync(id, declared, masking, options.Value.MaxDeclaredEventsShown, ct).ConfigureAwait(false);

        await IntegrationApi.AuditAsync(audit, http, client, "integration.inventory.history", AuditOutcome.Success,
            InventoryIdentifier.Name(id.Kind),
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["bindings"] = timeline.Summary.Bindings, ["declared"] = events.Count, ["truncated"] = timeline.Truncated,
                ["masked"] = !reveal,
            }, ct).ConfigureAwait(false);

        return Results.Ok(new InventoryHistoryResponse(
            InventoryIdentifier.Name(id.Kind), id.Digits, id.Kind == InventoryKind.Iccid ? id.Imsi : null, timeline, events));
    }

    // ------------------------------------------------------------------ devices

    private static async Task<IResult> ModelAsync(
        string tac, HttpContext http, IDeviceAnalyticsStore devices, IAuditLog audit, CancellationToken ct)
    {
        var client = CurrentClient.Require(http);
        var detail = await devices.GetDeviceAsync(tac, ct).ConfigureAwait(false);

        await IntegrationApi.AuditAsync(audit, http, client, "integration.devices.model", AuditOutcome.Success, "tac",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["found"] = detail is not null }, ct).ConfigureAwait(false);

        return detail is null
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown TAC",
                detail: "Neither the active GSMA catalogue nor the network knows this TAC.")
            : Results.Ok(Model(detail));
    }

    // ------------------------------------------------------------------ facts

    private static async Task<IResult> FactsAsync(
        IdentifierRequest request, HttpContext http, ITimelineStore timelines, IDeviceAnalyticsStore devices,
        IDeclaredEventStore declared, IRiskReader risk, IFeedQualityReader quality, IOptions<FeedQualityOptions> qualityOptions,
        IOptionsMonitor<RiskOptions> riskOptions, IAuditLog audit, IOptions<IntegrationOptions> options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var client = CurrentClient.Require(http);

        if (!TryIdentify(request, out var id, out var refusal))
        {
            return refusal;
        }

        if (await HistoryRefusalAsync(timelines, ct).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var reveal = client.Has(IntegrationScopes.IdentifiersReveal);
        var masking = new IdentifierMasking(reveal, id);
        var (centre, digits) = Centre(id);

        var stored = await timelines.GetAsync(centre, digits, options.Value.MaxBindings, ct).ConfigureAwait(false);
        var through = await timelines.DataThroughAsync(ct).ConfigureAwait(false);
        var timeline = TimelineBuilder.Build(centre, digits, stored, Full, options.Value.MaxBindings, through);

        // The handsets that matter to a policy are those it is in now; a handset asked about is itself.
        List<string> handsets = id.Kind == InventoryKind.Imei
            ? [id.Digits]
            : [.. timeline.Bindings.Where(b => b.Active && b.Imei is { Length: 14 }).Select(b => b.Imei!).Distinct(StringComparer.Ordinal).Take(10)];

        var models = new Dictionary<string, DeviceModelInfo?>(StringComparer.Ordinal);
        foreach (var tac in handsets.Select(h => h[..8]).Distinct(StringComparer.Ordinal))
        {
            var detail = await devices.GetDeviceAsync(tac, ct).ConfigureAwait(false);
            models[tac] = detail is null ? null : Model(detail);
        }

        var family = id.Kind switch
        {
            InventoryKind.Msisdn => RiskFamily.Number,
            InventoryKind.Imei => RiskFamily.Imei,
            _ => RiskFamily.Sim,
        };
        var (assessment, notReady, _, _) = await RiskEndpoints.AssessAsync(
            family, digits, risk, quality, qualityOptions, riskOptions, ct).ConfigureAwait(false);

        var events = await DeclaredForAsync(id, declared, masking, options.Value.MaxDeclaredEventsShown, ct).ConfigureAwait(false);
        var summary = timeline.Summary;
        var firstSeen = DateOnly.TryParseExact(summary.FirstSeen, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)
            ? first
            : (DateOnly?)null;

        var answer = new InventoryFactsResponse(
            InventoryIdentifier.Name(id.Kind), id.Digits, id.Kind == InventoryKind.Iccid ? id.Imsi : null, summary.Bindings > 0,
            Summary(summary),
            summary.Bindings > 0 ? NetworkAge.Days(summary.FirstSeenIsDumpWindow ? null : firstSeen, summary.FirstSeenIsDumpWindow, through) : null,
            [.. handsets.Select(h => new DeviceFactsInfo(masking.Imei(h), models.GetValueOrDefault(h[..8])))],
            assessment, assessment is null ? notReady ?? "The risk snapshot is not ready." : null,
            events, masking.Visibility, IntegrationApi.Iso(through));

        await IntegrationApi.AuditAsync(audit, http, client, "integration.inventory.facts", AuditOutcome.Success,
            InventoryIdentifier.Name(id.Kind),
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["found"] = answer.Found, ["devices"] = answer.Devices.Count, ["risk"] = assessment?.Level, ["masked"] = !reveal,
            }, ct).ConfigureAwait(false);

        return Results.Ok(answer);
    }

    // ------------------------------------------------------------------ sync

    /// <summary>A page of one day's changes. A POST, because the cursor holds the last row's identifiers.</summary>
    private static async Task<IResult> ChangesAsync(
        BindingChangesRequest request, HttpContext http, IBindingChangeReader changes, IAuditLog audit,
        IOptions<IntegrationOptions> options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var client = CurrentClient.Require(http);
        var settings = options.Value;

        if (!DateOnly.TryParseExact(request.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return IntegrationApi.Invalid("date", "The business date, yyyy-MM-dd: the subject of an inventory.day-imported event.");
        }

        var limit = request.Limit ?? settings.DefaultChangesPage;
        if (limit < 1 || limit > settings.MaxChangesPage)
        {
            return IntegrationApi.Invalid("limit", $"Between 1 and {settings.MaxChangesPage}.");
        }

        BindingChangeCursor? after = null;
        if (request.Cursor is { Length: > 0 } cursor)
        {
            after = ReadCursor(cursor);
            if (after is null)
            {
                return IntegrationApi.Invalid("cursor", "Not a cursor this API issued. Send back the \"next\" of the previous page.");
            }
        }

        var page = await changes.ReadAsync(date, after, limit, ct).ConfigureAwait(false);
        var masking = new IdentifierMasking(client.Has(IntegrationScopes.IdentifiersReveal), null);

        await IntegrationApi.AuditAsync(audit, http, client, "integration.inventory.changes", AuditOutcome.Success, "day",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["date"] = IntegrationApi.Iso(date), ["changes"] = page.Changes.Count, ["more"] = page.Next is not null,
                ["masked"] = !masking.Reveal,
            }, ct).ConfigureAwait(false);

        return Results.Ok(new BindingChangesResponse(
            IntegrationApi.Iso(date)!,
            [.. page.Changes.Select(c =>
            {
                var imsi = c.Imsi.ToString(CultureInfo.InvariantCulture);
                return new BindingChangeInfo(
                    masking.Msisdn(c.Msisdn.ToString(CultureInfo.InvariantCulture)), masking.Imsi(imsi), masking.Iccid(Iccid.FromImsi(imsi)),
                    masking.Imei(c.Imei), c.Tac, c.Added ? "add" : "remove", c.Sequence);
            })],
            page.Next is { } next ? WriteCursor(next) : null,
            masking.Visibility));
    }

    /// <summary>A cursor is the last row's key, base64url: opaque to a client, and in a body, never a URL.</summary>
    public static string WriteCursor(BindingChangeCursor cursor) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(
            ':', cursor.Msisdn.ToString(CultureInfo.InvariantCulture), cursor.Imsi.ToString(CultureInfo.InvariantCulture),
            cursor.Imei, cursor.Sequence.ToString(CultureInfo.InvariantCulture))))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A cursor read back, or null when it is not one this API wrote.</summary>
    public static BindingChangeCursor? ReadCursor(string cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return Decode(cursor);
    }

    private static BindingChangeCursor? Decode(string cursor)
    {
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - (padded.Length % 4)) % 4);
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split(':');
            return parts.Length == 4
                   && ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var msisdn)
                   && ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var imsi)
                   && parts[2].Length <= 32 && parts[2].All(char.IsAsciiLetterOrDigit)
                   && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) && sequence <= ushort.MaxValue
                ? new BindingChangeCursor(msisdn, imsi, parts[2], sequence)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ shared

    private static bool TryIdentify(IdentifierRequest request, out InventoryIdentifier id, out IResult refusal)
    {
        var kind = InventoryIdentifier.KindOf(request.Kind);
        if (request.Kind is { Length: > 0 } && kind is null)
        {
            id = default;
            refusal = IntegrationApi.Invalid("kind", "msisdn, imsi, iccid or imei; or leave it out to read it from the length.");
            return false;
        }

        if (!InventoryIdentifier.TryParse(request.Identifier, kind, out id, out var problem))
        {
            refusal = IntegrationApi.Invalid("identifier", problem);
            return false;
        }

        refusal = Results.Empty;
        return true;
    }

    private static (TimelineCentre Centre, string Digits) Centre(InventoryIdentifier id) => id.Kind switch
    {
        InventoryKind.Msisdn => (TimelineCentre.Msisdn, id.Digits),
        InventoryKind.Imei => (TimelineCentre.Imei, id.Digits),
        _ => (TimelineCentre.Imsi, id.Imsi!),
    };

    /// <summary>The 503 a lookup gets while the binding history cannot answer completely.</summary>
    private static async Task<IResult?> HistoryRefusalAsync(ITimelineStore timelines, CancellationToken ct) =>
        await timelines.GetReadinessAsync(ct).ConfigureAwait(false) switch
        {
            HistoryReadiness.NotDeployed => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The binding history is not deployed", detail: "Analytics migration 022 is not applied on this server."),
            HistoryReadiness.Building => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The binding history is still being built",
                detail: "An answer now would silently miss the days not yet written. Retry later."),
            _ => null,
        };

    private static async Task<IReadOnlyList<DeclaredEventInfo>> DeclaredForAsync(
        InventoryIdentifier id, IDeclaredEventStore declared, IdentifierMasking masking, int limit, CancellationToken ct)
    {
        if (id.Kind == InventoryKind.Imei)
        {
            return [];
        }

        var iccid = id.Kind == InventoryKind.Iccid ? id.Digits : id.Imsi is { } imsi ? Iccid.FromImsi(imsi) : null;
        var rows = await declared.ListForAsync(
            id.Kind == InventoryKind.Msisdn ? id.Digits : null, id.Imsi, iccid, limit, ct).ConfigureAwait(false);

        return [.. rows.Select(r => new DeclaredEventInfo(
            r.Event.EventId, r.Event.Source, DeclaredEventRules.Name(r.Event.Type), IntegrationApi.Iso(r.Event.OccurredAt),
            IntegrationApi.Iso(r.ReceivedAt), masking.Msisdn(r.Event.Msisdn),
            r.Event.Imsi is null ? null : masking.Imsi(r.Event.Imsi), masking.Iccid(r.Event.Iccid),
            r.Event.PreviousImsi is null ? null : masking.Imsi(r.Event.PreviousImsi), masking.Iccid(r.Event.PreviousIccid),
            r.Event.Reference, r.ClientId))];
    }

    private static InventorySummary Summary(Contracts.Timeline.TimelineSummaryInfo s) => new(
        s.Bindings, s.ActiveBindings, s.Numbers, s.Sims, s.Handsets, s.FirstSeen, s.FirstSeenIsDumpWindow, s.LastChange);

    private static DeviceModelInfo Model(DeviceDetailRow d) => new(
        d.Summary.Tac, d.KnownToGsma, d.Summary.Manufacturer, d.Summary.Brand, d.Summary.Model, d.Summary.MarketingName,
        d.Summary.DeviceType, d.Summary.OperatingSystem,
        new DeviceCapabilitiesInfo(d.HasLte, d.Has5g, d.HasEsim, d.ImsEmergency, d.Bands, d.SimSlots, d.ImeiQuantity, d.Nfc, d.Wlan, d.Bluetooth),
        d.AllocationDate, d.TacVersionId);

    private static string Mask(TimelineCentre kind, string value) => kind switch
    {
        TimelineCentre.Msisdn => IdentifierMask.Msisdn(value),
        TimelineCentre.Imsi => IdentifierMask.Imsi(value),
        _ => IdentifierMask.Imei(value),
    };
}
