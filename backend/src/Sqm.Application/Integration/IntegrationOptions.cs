namespace Sqm.Application.Integration;

/// <summary>The integration API's limits (ADR-024), from configuration section <c>Integration</c>.</summary>
public sealed class IntegrationOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Integration";

    /// <summary>How long a bearer token lives. Short, because a leaked token is good until it expires.</summary>
    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Requests one client may make a minute, across every integration endpoint.</summary>
    public int RequestsPerMinute { get; init; } = 600;

    /// <summary>Token requests one network address may make a minute.</summary>
    public int TokenRequestsPerMinute { get; init; } = 30;

    /// <summary>Identifiers in one batch lookup.</summary>
    public int MaxBatch { get; init; } = 50;

    /// <summary>Bindings a lookup or a history returns at most, as the timeline does.</summary>
    public int MaxBindings { get; init; } = 200;

    /// <summary>Changes one page of the sync feed returns by default.</summary>
    public int DefaultChangesPage { get; init; } = 1000;

    /// <summary>Changes one page of the sync feed may return.</summary>
    public int MaxChangesPage { get; init; } = 5000;

    /// <summary>Events one page of the event feed may return.</summary>
    public int MaxEventsPage { get; init; } = 500;

    /// <summary>Declared events one delivery may carry.</summary>
    public int MaxDeclaredEventsPerRequest { get; init; } = 100;

    /// <summary>Declared events a history returns at most.</summary>
    public int MaxDeclaredEventsShown { get; init; } = 100;

    /// <summary>Days the event feed keeps an event before it is pruned.</summary>
    public int OutboxRetentionDays { get; init; } = 30;
}
