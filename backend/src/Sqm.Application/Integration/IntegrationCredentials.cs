using System.Security.Cryptography;
using System.Text;

namespace Sqm.Application.Integration;

/// <summary>Client secrets and bearer tokens: how they are made and how they are kept (ADR-024).</summary>
/// <remarks>
/// <para>
/// Both are 32 bytes from the operating system's random generator, written base64url without padding -
/// 43 characters, 256 bits. Only their SHA-256 is stored, and compared in constant time.
/// </para>
/// <para>
/// A plain hash and not Argon2id, as a session token is: a password needs a slow hash because a person
/// chooses it from a small space, and a guess per hash is cheap. A 256-bit random value cannot be guessed
/// whatever the hash costs, and the token is checked on every request.
/// </para>
/// </remarks>
public static class IntegrationCredentials
{
    /// <summary>Bytes of randomness in a secret or a token.</summary>
    public const int Bytes = 32;

    /// <summary>A new secret or token, base64url without padding.</summary>
    public static string NewValue() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(Bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>What is stored for a secret or a token.</summary>
    public static byte[] Hash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SHA256.HashData(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>Whether a presented value matches a stored hash, in constant time.</summary>
    public static bool Matches(string presented, byte[] storedHash)
    {
        ArgumentNullException.ThrowIfNull(presented);
        ArgumentNullException.ThrowIfNull(storedHash);
        return CryptographicOperations.FixedTimeEquals(Hash(presented), storedHash);
    }

    /// <summary>Whether a string could be one of these values at all: the shape, before any lookup.</summary>
    public static bool IsWellFormed(string? value) =>
        value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
