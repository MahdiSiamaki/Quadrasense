using System.Collections.Frozen;

namespace Sqm.Application.Integration;

/// <summary>
/// What a client of the integration API may do (ADR-024). A scope is to a system what a permission is
/// to a person: an administrator grants a client the scopes it needs, and each endpoint names the one it
/// requires.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="Identity.Permissions"/> on purpose. A person's permission and a system's
/// scope are granted by different decisions and read by different people in an audit, and a policy for
/// one must never be satisfied by the other: a session cannot reach the integration API and a token
/// cannot reach the UI's.
/// </remarks>
public static class IntegrationScopes
{
    /// <summary>Look up numbers, SIMs (IMSI or ICCID) and handsets; their relationships, history and device models.</summary>
    public const string InventoryRead = "inventory.read";

    /// <summary>Read each day's binding changes, for a consumer that keeps its own copy in step.</summary>
    public const string InventorySync = "inventory.sync";

    /// <summary>Read the device and SIM facts a policy decision is written against (Tessera).</summary>
    public const string FactsRead = "facts.read";

    /// <summary>Read PI's event feed.</summary>
    public const string EventsRead = "events.read";

    /// <summary>Deliver declared events - CRM's SIM swaps, activations and deactivations - to PI.</summary>
    public const string EventsWrite = "events.write";

    /// <summary>Read data-quality figures and the state of the feeds.</summary>
    public const string QualityRead = "quality.read";

    /// <summary>
    /// Receive identifiers in full. Without it, every IMSI, ICCID and IMEI in an answer is masked, but
    /// for the identifier the client sent, as the UI does for a person without identifier.reveal.
    /// </summary>
    public const string IdentifiersReveal = "identifiers.reveal";

    /// <summary>Every scope this build knows, with what it allows in a sentence.</summary>
    public static readonly FrozenDictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [InventoryRead] = "Look up numbers, SIMs (IMSI or ICCID) and handsets: their bindings, relationships, history and device models.",
        [InventorySync] = "Read each day's binding changes, to keep a copy in step.",
        [FactsRead] = "Read the device and SIM facts a policy decision is written against.",
        [EventsRead] = "Read PI's event feed.",
        [EventsWrite] = "Deliver declared events - SIM swaps, activations, deactivations - to PI.",
        [QualityRead] = "Read data-quality figures and the state of the feeds.",
        [IdentifiersReveal] = "Receive IMSIs, ICCIDs and IMEIs in full; without it they are masked.",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Every scope code known to this build.</summary>
    public static readonly FrozenSet<string> All = Descriptions.Keys.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// A scope string as OAuth 2.0 writes it - space-separated (RFC 6749 §3.3) - as a set, or null when it
    /// names a scope this build does not know.
    /// </summary>
    public static IReadOnlySet<string>? Parse(string? scope)
    {
        var parts = (scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var set = new HashSet<string>(parts, StringComparer.Ordinal);
        return set.All(All.Contains) ? set : null;
    }

    /// <summary>A set of scopes as OAuth 2.0 writes it: space-separated, in a stable order.</summary>
    public static string Format(IEnumerable<string> scopes) =>
        string.Join(' ', scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
}
