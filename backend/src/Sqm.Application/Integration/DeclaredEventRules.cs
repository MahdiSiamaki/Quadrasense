using System.Globalization;
using Sqm.Domain.Identifiers;

namespace Sqm.Application.Integration;

/// <summary>What a declared event must look like before PI stores it (ADR-024).</summary>
/// <remarks>
/// The landscape names three commercial facts CRM declares to PI through Lodestone: a SIM swap, an
/// activation and a deactivation. Each is keyed by PI's own identifiers - the number, and the SIM by IMSI
/// or ICCID - and nothing of CRM's own model is asked for: an order reference is carried as an opaque
/// string. The rules are strict, because a malformed event stored is a wrong fact in a lookup.
/// </remarks>
public static class DeclaredEventRules
{
    /// <summary>The longest event id, source or reference accepted.</summary>
    public const int MaxTextLength = 128;

    /// <summary>The longest a source name may be.</summary>
    public const int MaxSourceLength = 32;

    /// <summary>The type of an event from its name, or null.</summary>
    public static DeclaredEventType? TypeOf(string? name) => name?.Trim() switch
    {
        "sim-swap" => DeclaredEventType.SimSwap,
        "activation" => DeclaredEventType.Activation,
        "deactivation" => DeclaredEventType.Deactivation,
        _ => null,
    };

    /// <summary>The name of a type, as the API and the database write it.</summary>
    public static string Name(DeclaredEventType type) => type switch
    {
        DeclaredEventType.SimSwap => "sim-swap",
        DeclaredEventType.Activation => "activation",
        _ => "deactivation",
    };

    /// <summary>
    /// Checks one event as delivered; returns the event normalised, or the problems keyed by field, as a
    /// validation problem lists them.
    /// </summary>
    public static (DeclaredEvent? Event, IReadOnlyDictionary<string, string> Problems) Check(
        string? eventId, string? source, string? type, DateTimeOffset? occurredAt, string? msisdn,
        string? imsi, string? iccid, string? previousImsi, string? previousIccid, string? reference, DateTimeOffset now)
    {
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);

        var id = eventId?.Trim();
        if (string.IsNullOrEmpty(id) || id.Length > MaxTextLength || id.Any(char.IsControl))
        {
            problems["eventId"] = $"Required: the sender's id for the event, at most {MaxTextLength} characters.";
        }

        var from = source?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(from) || from.Length > MaxSourceLength || !from.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            problems["source"] = "Required: the system that declared it, such as \"crm\" - letters, digits and hyphens.";
        }

        var kind = TypeOf(type);
        if (kind is null)
        {
            problems["type"] = "One of \"sim-swap\", \"activation\" or \"deactivation\".";
        }

        if (occurredAt is not { } at)
        {
            problems["occurredAt"] = "Required: when it happened, with its offset (ISO 8601).";
        }
        else if (at > now.AddMinutes(5))
        {
            problems["occurredAt"] = "In the future.";
        }

        string? number = null;
        if (Msisdn.TryParse(msisdn, out var parsed) && parsed.Value.IsWellFormed)
        {
            number = parsed.Value.ToString();
        }
        else
        {
            problems["msisdn"] = "Required: the number, ten digits.";
        }

        var sim = Sim(imsi, iccid, "imsi", "iccid", problems);
        var previous = Sim(previousImsi, previousIccid, "previousImsi", "previousIccid", problems);

        if (sim.Imsi is null && sim.Iccid is null && !problems.ContainsKey("imsi") && !problems.ContainsKey("iccid"))
        {
            problems["imsi"] = "The SIM, by IMSI or ICCID.";
        }

        if (kind == DeclaredEventType.SimSwap && previous.Imsi is null && previous.Iccid is null
            && !problems.ContainsKey("previousImsi") && !problems.ContainsKey("previousIccid"))
        {
            problems["previousImsi"] = "A swap names the SIM the number left, by IMSI or ICCID.";
        }

        if (kind is not DeclaredEventType.SimSwap && (previous.Imsi is not null || previous.Iccid is not null))
        {
            problems["previousImsi"] = "Only a swap names a previous SIM.";
        }

        var note = reference?.Trim();
        if (note is { Length: > MaxTextLength } || (note is not null && note.Any(char.IsControl)))
        {
            problems["reference"] = $"At most {MaxTextLength} characters.";
        }

        if (problems.Count > 0)
        {
            return (null, problems);
        }

        return (new DeclaredEvent(
            id!, from!, kind!.Value, occurredAt!.Value.ToUniversalTime(), number!, sim.Imsi, sim.Iccid,
            previous.Imsi, previous.Iccid, string.IsNullOrEmpty(note) ? null : note), problems);
    }

    /// <summary>The canonical text of an event, whose hash tells a retry from a conflicting reuse of an id.</summary>
    public static string Canonical(DeclaredEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return string.Join('\n',
            e.EventId, e.Source, Name(e.Type), e.OccurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), e.Msisdn,
            e.Imsi ?? string.Empty, e.Iccid ?? string.Empty, e.PreviousImsi ?? string.Empty, e.PreviousIccid ?? string.Empty,
            e.Reference ?? string.Empty);
    }

    private static (string? Imsi, string? Iccid) Sim(
        string? imsi, string? iccid, string imsiField, string iccidField, Dictionary<string, string> problems)
    {
        var i = string.IsNullOrWhiteSpace(imsi) ? null : imsi.Trim();
        var c = string.IsNullOrWhiteSpace(iccid) ? null : iccid.Trim();

        if (i is not null && !(i.Length == Imsi.ExpectedLength && i.All(char.IsAsciiDigit)))
        {
            problems[imsiField] = "An IMSI is fifteen digits.";
            i = null;
        }

        // Any ICCID of 18 to 20 digits is a SIM's: what CRM holds is the card's number, which this
        // operator's numbering may or may not have produced. Stored as declared.
        if (c is not null && !(c.Length is >= 18 and <= 20 && c.All(char.IsAsciiDigit)))
        {
            problems[iccidField] = "An ICCID is 18 to 20 digits.";
            c = null;
        }

        return (i, c);
    }
}
