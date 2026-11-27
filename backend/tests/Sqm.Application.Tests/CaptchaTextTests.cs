using Sqm.Application.Identity;
using Xunit;

namespace Sqm.Application.Tests;

/// <summary>
/// The captcha's characters (ADR-026; MCI red line §2-3 row 3): Persian letters and digits, none that a
/// dot or a stroke under the noise would turn into another, never two digits side by side, and answers
/// read however a Persian, Arabic or Latin keyboard types them.
/// </summary>
public sealed class CaptchaTextTests
{
    [Fact]
    public void Every_captcha_holds_letters_and_digits_of_the_set_and_no_run_of_digits()
    {
        for (var i = 0; i < 5000; i++)
        {
            var text = CaptchaText.Generate();

            Assert.Equal(CaptchaText.Length, text.Length);
            Assert.All(text, c => Assert.Contains(c, CaptchaText.Alphabet));
            Assert.Contains(text, c => CaptchaText.Letters.Contains(c));
            Assert.Contains(text, c => CaptchaText.Digits.Contains(c));

            for (var j = 1; j < text.Length; j++)
            {
                Assert.False(
                    CaptchaText.Digits.Contains(text[j]) && CaptchaText.Digits.Contains(text[j - 1]),
                    $"two digits side by side in {text}");
            }
        }
    }

    [Fact]
    public void Every_character_of_the_set_turns_up()
    {
        var seen = new HashSet<char>();

        for (var i = 0; i < 5000; i++)
        {
            seen.UnionWith(CaptchaText.Generate());
        }

        Assert.Equal(CaptchaText.Alphabet.ToHashSet(), seen);
    }

    [Theory]
    [InlineData('ا')] // a bare stroke: like the noise, and like ۱
    [InlineData('ث')]
    [InlineData('پ')]
    [InlineData('چ')]
    [InlineData('خ')]
    [InlineData('ح')]
    [InlineData('ذ')]
    [InlineData('ز')]
    [InlineData('ش')]
    [InlineData('ض')]
    [InlineData('ظ')]
    [InlineData('غ')]
    [InlineData('ق')]
    [InlineData('گ')]
    [InlineData('۰')]
    [InlineData('۱')]
    [InlineData('۵')] // in several of the typefaces, the shape of ه
    [InlineData('۹')] // the shape of و
    public void A_character_a_mark_away_from_another_is_never_used(char twin) =>
        Assert.DoesNotContain(twin, CaptchaText.Alphabet);

    [Fact]
    public void The_set_is_persian_letters_and_persian_digits()
    {
        Assert.All(CaptchaText.Letters, c => Assert.InRange(c, '\u0600', '\u06FF'));
        Assert.All(CaptchaText.Digits, c => Assert.InRange(c, '\u06F0', '\u06F9'));
        Assert.Equal(CaptchaText.Alphabet.Length, CaptchaText.Alphabet.Distinct().Count());
    }

    [Theory]
    [InlineData("ب۳ک۷م", "ب۳ک۷م")]         // as typed on a Persian keyboard
    [InlineData("ب3ک7م", "ب۳ک۷م")]          // Latin digits
    [InlineData("ب٣ک٧م", "ب۳ک۷م")]          // Arabic-Indic digits
    [InlineData("بيك", "بیک")]              // the Arabic ye and kaf
    [InlineData("بى", "بی")]                // alef maksura
    [InlineData(" ب ۳\u200Cک\u0640۷\u200Fم ", "ب۳ک۷م")] // spaces, a joiner, tatweel, a direction mark
    [InlineData(null, "")]
    public void An_answer_is_read_however_it_was_typed(string? typed, string read) =>
        Assert.Equal(read, CaptchaText.Normalize(typed));

    [Fact]
    public void An_answer_hash_is_bound_to_its_challenge()
    {
        Assert.Equal(CaptchaText.AnswerHash("a", "ب۳ک۷م"), CaptchaText.AnswerHash("a", "ب3ک7م"));
        Assert.NotEqual(CaptchaText.AnswerHash("a", "ب۳ک۷م"), CaptchaText.AnswerHash("b", "ب۳ک۷م"));
        Assert.NotEqual(CaptchaText.AnswerHash("a", "ب۳ک۷م"), CaptchaText.AnswerHash("a", "ب۳ک۷ن"));
    }

    [Fact]
    public void The_defaults_are_the_red_lines()
    {
        var options = new CaptchaOptions();

        Assert.True(options.Enabled);
        Assert.Equal(3, options.LoginFailuresBefore);
        Assert.Equal(5, options.RecoveryFailuresBefore);
    }
}
