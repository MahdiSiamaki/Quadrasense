using System.Diagnostics.CodeAnalysis;
using Sqm.Domain.Identifiers;

namespace Sqm.Application.Integration;

/// <summary>Which of PI's identifiers a request names.</summary>
public enum InventoryKind
{
    /// <summary>A phone number, ten digits.</summary>
    Msisdn,

    /// <summary>A SIM, by its IMSI: fifteen digits.</summary>
    Imsi,

    /// <summary>A SIM, by its ICCID: nineteen digits, resolved to an IMSI by the operator's numbering.</summary>
    Iccid,

    /// <summary>
    /// A handset: fourteen digits, as the feed carries it. Sent with its check digit (15) or as an IMEISV (16),
    /// it is reduced to them (<see cref="ImeiForm"/>).
    /// </summary>
    Imei,
}

/// <summary>An identifier a client sent, checked and classified.</summary>
/// <remarks>
/// <para>
/// The kind may be given or left to the length, as the Explorer classifies what a person types. An ICCID
/// is nineteen digits and no other identifier is, so it can be told apart too.
/// </para>
/// <para>
/// <b>An ICCID is resolved, never looked up.</b> No feed PI receives carries one: the ICCID PI shows is
/// derived from the IMSI by the operator's numbering rule (<see cref="Iccid"/>), and labelled derived. An
/// ICCID is therefore turned back into its IMSI by the same rule, and one the rule does not produce
/// cannot be answered - it is said so, rather than reported as not found.
/// </para>
/// <para>
/// <b>An IMEI is answered in the feed's form.</b> Other systems hold an IMEI with its check digit, or as an
/// IMEISV; the feed drops both. Either is reduced to the fourteen digits PI holds, and a check digit that
/// does not match is refused rather than looked up as some other handset. Fifteen digits with no kind given
/// are an IMSI, as they are everywhere in this product: nothing else in the feed has that length.
/// </para>
/// <para>
/// <b>No EID.</b> PI receives no eUICC data (docs/architecture/20-estate-landscape.md §4), so an EID is
/// not an identifier this API accepts.
/// </para>
/// </remarks>
/// <param name="Kind">What it is.</param>
/// <param name="Digits">The identifier as PI stores it: a number as its ten digits, an IMEI as its fourteen.</param>
/// <param name="Imsi">For an IMSI, itself; for an ICCID, the IMSI it resolves to; otherwise null.</param>
public readonly record struct InventoryIdentifier(InventoryKind Kind, string Digits, string? Imsi)
{
    /// <summary>The name of a kind in a request and an answer.</summary>
    public static string Name(InventoryKind kind) => kind switch
    {
        InventoryKind.Msisdn => "msisdn",
        InventoryKind.Imsi => "imsi",
        InventoryKind.Iccid => "iccid",
        _ => "imei",
    };

    /// <summary>A kind from its name, or null.</summary>
    public static InventoryKind? KindOf(string? name) => name?.Trim().ToUpperInvariant() switch
    {
        "MSISDN" => InventoryKind.Msisdn,
        "IMSI" => InventoryKind.Imsi,
        "ICCID" => InventoryKind.Iccid,
        "IMEI" => InventoryKind.Imei,
        _ => null,
    };

    /// <summary>Checks an identifier; on failure, says why in a sentence a client's developer can act on.</summary>
    public static bool TryParse(
        string? raw, InventoryKind? declared, out InventoryIdentifier identifier, [NotNullWhen(false)] out string? problem)
    {
        identifier = default;
        var text = raw?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            problem = "An identifier is required.";
            return false;
        }

        var kind = declared ?? Classify(text);
        switch (kind)
        {
            case InventoryKind.Msisdn:
                if (Msisdn.TryParse(text, out var number) && number.Value.IsWellFormed)
                {
                    identifier = new InventoryIdentifier(InventoryKind.Msisdn, number.Value.ToString(), null);
                    problem = null;
                    return true;
                }

                problem = "A phone number is ten digits, written with or without a country code.";
                return false;

            case InventoryKind.Imsi when IsDigits(text, Domain.Identifiers.Imsi.ExpectedLength):
                identifier = new InventoryIdentifier(InventoryKind.Imsi, text, text);
                problem = null;
                return true;

            case InventoryKind.Imsi:
                problem = "An IMSI is fifteen digits.";
                return false;

            case InventoryKind.Iccid when IsDigits(text, Iccid.Length):
                if (Iccid.ToImsi(text) is { } imsi)
                {
                    identifier = new InventoryIdentifier(InventoryKind.Iccid, text, imsi);
                    problem = null;
                    return true;
                }

                problem = "This ICCID is not one the operator's numbering derives from an IMSI, so PI cannot resolve it: "
                          + "no feed PI receives carries ICCIDs.";
                return false;

            case InventoryKind.Iccid:
                problem = $"An ICCID is {Iccid.Length} digits.";
                return false;

            case InventoryKind.Imei:
                if (!ImeiForm.TryReduce(text, out var fourteen, out problem))
                {
                    return false;
                }

                identifier = new InventoryIdentifier(InventoryKind.Imei, fourteen, null);
                return true;

            default:
                problem = "A phone number (10 digits), an IMSI (15), an ICCID (19) or an IMEI (14, or 16 as an IMEISV; "
                          + "15 with its check digit when kind is imei). PI holds no EID: no feed it receives carries eUICC data.";
                return false;
        }
    }

    private static InventoryKind? Classify(string text)
    {
        if (DeviceSearchTerm.Classify(text) is { } term)
        {
            switch (term.Kind)
            {
                case DeviceSearchKind.Msisdn: return InventoryKind.Msisdn;
                case DeviceSearchKind.Imsi: return InventoryKind.Imsi;
                case DeviceSearchKind.Imei: return InventoryKind.Imei;
            }
        }

        // Sixteen digits are an IMEISV and nineteen an ICCID: no other identifier has either length.
        return IsDigits(text, Iccid.Length) ? InventoryKind.Iccid
            : IsDigits(text, ImeiForm.WithSoftwareVersion) ? InventoryKind.Imei
            : null;
    }

    private static bool IsDigits(string text, int length) => text.Length == length && text.All(char.IsAsciiDigit);
}
