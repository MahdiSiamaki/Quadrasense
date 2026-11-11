using System.Diagnostics.CodeAnalysis;

namespace Sqm.Domain.Identifiers;

/// <summary>
/// An IMEI as another system writes it, reduced to the fourteen digits the binding feed carries.
/// </summary>
/// <remarks>
/// <para>
/// The feed's IMEIs are fourteen digits - TAC and serial, the check digit dropped at the source
/// (<see cref="Imei"/>). The systems that ask PI about a handset hold it in the forms of 3GPP TS 23.003 §6.2:
/// </para>
/// <list type="bullet">
/// <item><b>15 digits</b>: the IMEI with its check digit, as it is printed on a handset and kept by a CRM.
/// The check digit is Luhn's over the first fourteen. A handset never transmits it: on the network the
/// fifteenth digit is a spare, sent as zero - so a zero there is accepted as well.</item>
/// <item><b>16 digits</b>: the IMEISV, whose last two digits are the software version. It has no check
/// digit.</item>
/// </list>
/// <para>
/// <b>A fifteenth digit that is neither is refused.</b> It is a mistyped number, and answering for its first
/// fourteen digits would answer for some other handset - or report "not found" for one PI does hold.
/// </para>
/// </remarks>
public static class ImeiForm
{
    /// <summary>An IMEI with its check digit, or with the spare zero a handset transmits in its place.</summary>
    public const int WithCheckDigit = 15;

    /// <summary>An IMEISV: the IMEI's fourteen digits and a two-digit software version.</summary>
    public const int WithSoftwareVersion = 16;

    /// <summary>Reduces an IMEI of 14, 15 or 16 digits to the fourteen PI holds; or says why it cannot.</summary>
    /// <param name="text">The IMEI as sent: digits only.</param>
    /// <param name="imei">The fourteen digits.</param>
    /// <param name="problem">Why not, in a sentence the sender's developer can act on.</param>
    public static bool TryReduce(string? text, [NotNullWhen(true)] out string? imei, [NotNullWhen(false)] out string? problem)
    {
        imei = null;
        var digits = text?.Trim() ?? string.Empty;

        if (digits.Length is not (Imei.WellFormedLength or WithCheckDigit or WithSoftwareVersion) || !digits.All(char.IsAsciiDigit))
        {
            problem = "An IMEI is 14 digits. With its check digit (15) or as an IMEISV (16) it is accepted too, "
                      + "and reduced to the 14 PI holds.";
            return false;
        }

        var fourteen = digits[..Imei.WellFormedLength];
        if (digits.Length == WithCheckDigit && digits[^1] != CheckDigit(fourteen) && digits[^1] != '0')
        {
            problem = "The fifteenth digit of this IMEI is neither its check digit nor the zero a handset "
                      + "transmits in its place, so the number is mistyped. PI holds the first fourteen digits.";
            return false;
        }

        imei = fourteen;
        problem = null;
        return true;
    }

    /// <summary>The Luhn check digit of an IMEI's fourteen digits: the fifteenth digit printed on a handset.</summary>
    /// <param name="fourteen">TAC and serial.</param>
    public static char CheckDigit(ReadOnlySpan<char> fourteen)
    {
        if (fourteen.Length != Imei.WellFormedLength)
        {
            throw new ArgumentException("Fourteen digits.", nameof(fourteen));
        }

        var sum = 0;
        for (var i = 0; i < fourteen.Length; i++)
        {
            var digit = fourteen[i] - '0';
            if (digit is < 0 or > 9)
            {
                throw new ArgumentException("Fourteen digits.", nameof(fourteen));
            }

            // Every second digit, counted from the one beside the check digit, is doubled and its digits added.
            if (i % 2 == 1)
            {
                digit *= 2;
                digit = digit > 9 ? digit - 9 : digit;
            }

            sum += digit;
        }

        return (char)('0' + ((10 - (sum % 10)) % 10));
    }
}
