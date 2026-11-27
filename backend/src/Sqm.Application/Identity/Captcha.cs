using System.Security.Cryptography;
using System.Text;

namespace Sqm.Application.Identity;

/// <summary>
/// The Persian captcha's terms, from MCI's red line (FR-NSM-204/00 §2-3 row 3) and ADR-026: hidden at
/// first, asked for after three failed sign-ins and after five failures elsewhere.
/// </summary>
public sealed class CaptchaOptions
{
    /// <summary>Off only on a test copy: the red line asks for it wherever people sign in.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Failed sign-ins - from one address, or for one name - after which the next attempt needs a
    /// captcha. The red line's three.
    /// </summary>
    public int LoginFailuresBefore { get; init; } = 3;

    /// <summary>The same for account recovery: the red line's "five elsewhere".</summary>
    public int RecoveryFailuresBefore { get; init; } = 5;

    /// <summary>How long a failure counts towards asking.</summary>
    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a picture can be answered, once.</summary>
    public TimeSpan ChallengeLifetime { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>Where a captcha guards a form, each with its own count of failures.</summary>
public enum CaptchaScope
{
    /// <summary>Signing in with a password: asked after three failures.</summary>
    Login,

    /// <summary>Getting back into an account with a code: asked after five.</summary>
    Recovery,
}

/// <summary>The page the picture sits on, so it is drawn to be read there.</summary>
public enum CaptchaTheme
{
    /// <summary>Dark characters on a light ground.</summary>
    Light,

    /// <summary>Light characters on a dark ground.</summary>
    Dark,
}

/// <summary>A picture to answer, and the reference the answer comes back with.</summary>
/// <param name="Id">Names the challenge. Holds nothing of the answer.</param>
/// <param name="Png">The picture: the only form in which the characters leave the server.</param>
/// <param name="ExpiresAt">When it can no longer be answered.</param>
public sealed record CaptchaChallenge(string Id, byte[] Png, DateTimeOffset ExpiresAt);

/// <summary>Draws a captcha's characters as a picture.</summary>
public interface ICaptchaRenderer
{
    /// <summary>How many different ways of drawing there are; the red line asks for at least ten.</summary>
    int PatternCount { get; }

    /// <summary>The characters drawn right to left, as Persian is read, in one of the patterns.</summary>
    /// <param name="text">What to draw, in reading order.</param>
    /// <param name="theme">The page it will sit on.</param>
    /// <param name="pattern">Which pattern, from 0 to <see cref="PatternCount"/> - 1.</param>
    /// <param name="random">Where the drawing's own variety comes from - never the characters'.</param>
    /// <returns>A PNG.</returns>
    byte[] Render(string text, CaptchaTheme theme, int pattern, Random random);
}

/// <summary>Issues and checks captchas, and decides when one is needed.</summary>
public interface ICaptchaService
{
    /// <summary>
    /// Whether the next attempt in <paramref name="scope"/> needs a captcha: the address or the name has
    /// failed often enough, recently. The same answer whether or not an account by that name exists.
    /// </summary>
    Task<bool> IsRequiredAsync(CaptchaScope scope, string? address, string? username, CancellationToken ct);

    /// <summary>Counts a failure against the address and the name.</summary>
    Task RecordFailureAsync(CaptchaScope scope, string? address, string? username, CancellationToken ct);

    /// <summary>
    /// Forgets the name's failures, after it proved its password. Not the address's: one person
    /// succeeding from a shared address says nothing about the guesses of another.
    /// </summary>
    Task ForgetAsync(CaptchaScope scope, string? username, CancellationToken ct);

    /// <summary>A new picture, with its answer kept on the server.</summary>
    Task<CaptchaChallenge> IssueAsync(CaptchaTheme theme, CancellationToken ct);

    /// <summary>
    /// Whether <paramref name="answer"/> is the challenge's answer. Either way the challenge is used up:
    /// each picture is answered once.
    /// </summary>
    Task<bool> RedeemAsync(string? id, string? answer, CancellationToken ct);

    /// <summary>Removes pictures that can no longer be answered and failures that no longer count.</summary>
    /// <returns>How many rows went.</returns>
    Task<int> SweepAsync(CancellationToken ct);
}

/// <summary>The characters a captcha is made of, how they are chosen, and how an answer is read.</summary>
/// <remarks>
/// <para>
/// <b>Letters</b>, in their isolated forms, without those that a dot or a stroke lost under the noise would
/// turn into another letter of the set: ا (a bare stroke, like the noise lines, and like ۱), and the dotted
/// twins ث پ چ خ ح ذ ز ش ض ظ غ ق گ, each a mark away from a letter kept. A person must be able to read what
/// an OCR cannot; a pair that differs by one dot is a coin toss under a crossing curve.
/// </para>
/// <para>
/// <b>Digits</b>, Persian, without ۰ (a dot), ۱ (a stroke), ۵ (in several of the fonts the shape of ه) and
/// ۹ (the shape of و).
/// </para>
/// <para>
/// Never two digits side by side: inside right-to-left text a run of digits reads left to right, and the
/// picture lays every character right to left - "۲۳" would be drawn as a reader expects "۳۲".
/// </para>
/// </remarks>
public static class CaptchaText
{
    /// <summary>The letters a captcha may hold.</summary>
    public const string Letters = "بتجدرسصطعفکلمنوهی";

    /// <summary>The digits a captcha may hold.</summary>
    public const string Digits = "۲۳۴۶۷۸";

    /// <summary>Characters in a captcha: some 6 million choices, and still quick to type.</summary>
    public const int Length = 5;

    /// <summary>Every character a captcha may hold: what the on-screen keyboard offers.</summary>
    public static string Alphabet => Letters + Digits;

    /// <summary>A new captcha's characters, from the system's cryptographic generator.</summary>
    public static string Generate()
    {
        Span<char> text = stackalloc char[Length];

        while (true)
        {
            var digits = 0;
            var adjacent = false;

            for (var i = 0; i < Length; i++)
            {
                var pick = RandomNumberGenerator.GetInt32(Alphabet.Length);
                text[i] = Alphabet[pick];

                if (pick >= Letters.Length)
                {
                    digits++;
                    adjacent |= i > 0 && Digits.Contains(text[i - 1], StringComparison.Ordinal);
                }
            }

            // At least one of each, and no run of digits. Drawn again rather than patched, so every
            // allowed string stays equally likely.
            if (digits > 0 && digits < Length && !adjacent)
            {
                return new string(text);
            }
        }
    }

    /// <summary>
    /// An answer as it is compared: Arabic-Indic and Latin digits as Persian, the Arabic ي and ك as the
    /// Persian ی and ک, and nothing that does not show - spaces, joiners, tatweel, direction marks.
    /// </summary>
    public static string Normalize(string? answer)
    {
        if (string.IsNullOrEmpty(answer))
        {
            return string.Empty;
        }

        var normal = new StringBuilder(answer.Length);

        foreach (var c in answer)
        {
            switch (c)
            {
                case >= '0' and <= '9':
                    normal.Append((char)('\u06F0' + (c - '0')));
                    break;
                case >= '\u0660' and <= '\u0669':
                    normal.Append((char)('\u06F0' + (c - '\u0660')));
                    break;
                case '\u064A' or '\u0649':
                    normal.Append('\u06CC');
                    break;
                case '\u0643':
                    normal.Append('\u06A9');
                    break;
                case '\u200C' or '\u200D' or '\u0640' or '\u200E' or '\u200F'
                    or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069'):
                    break;
                default:
                    if (!char.IsWhiteSpace(c))
                    {
                        normal.Append(c);
                    }

                    break;
            }
        }

        return normal.ToString();
    }

    /// <summary>
    /// What is stored for an answer: a hash bound to its challenge, so a copy of the table shows neither the
    /// answers nor which answer goes with which picture.
    /// </summary>
    public static byte[] AnswerHash(string id, string? answer)
    {
        ArgumentNullException.ThrowIfNull(id);
        return SHA256.HashData(Encoding.UTF8.GetBytes(id + "\n" + Normalize(answer)));
    }
}
