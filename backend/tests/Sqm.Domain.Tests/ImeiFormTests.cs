using Sqm.Domain.Identifiers;

namespace Sqm.Domain.Tests;

/// <summary>
/// An IMEI as another system holds it, reduced to the fourteen digits of the feed. 49015420323751 with check
/// digit 8 is the worked example of the Luhn rule for IMEIs; no number here is a real handset's.
/// </summary>
public class ImeiFormTests
{
    private const string Fourteen = "49015420323751";

    [Theory]
    [InlineData("49015420323751", '8')]
    [InlineData("35085748000001", '6')]
    [InlineData("00000000000000", '0')]
    public void The_check_digit_is_Luhns_over_the_fourteen_digits(string fourteen, char expected) =>
        Assert.Equal(expected, ImeiForm.CheckDigit(fourteen));

    [Theory]
    [InlineData("49015420323751")] // as the feed carries it
    [InlineData("490154203237518")] // printed on the handset: with its check digit
    [InlineData("490154203237510")] // as a handset transmits it: the check digit's place is a spare zero
    [InlineData("4901542032375112")] // IMEISV: software version 12
    [InlineData(" 490154203237518 ")]
    public void Every_form_another_system_holds_is_reduced_to_the_feeds_fourteen_digits(string sent)
    {
        Assert.True(ImeiForm.TryReduce(sent, out var imei, out var problem));
        Assert.Equal(Fourteen, imei);
        Assert.Null(problem);
    }

    [Fact]
    public void A_fifteenth_digit_that_is_neither_the_check_digit_nor_zero_is_a_mistyped_number()
    {
        // Answering for the first fourteen digits would answer for another handset.
        Assert.False(ImeiForm.TryReduce("490154203237517", out var imei, out var problem));
        Assert.Null(imei);
        Assert.Contains("mistyped", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("4901542032375")] // thirteen
    [InlineData("49015420323751123")] // seventeen
    [InlineData("4901542032375A")]
    [InlineData("49-015420-323751")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_refused_with_the_lengths_that_are_accepted(string? sent)
    {
        Assert.False(ImeiForm.TryReduce(sent, out _, out var problem));
        Assert.Contains("14 digits", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_IMEISV_has_no_check_digit_so_any_software_version_is_accepted()
    {
        for (var version = 0; version < 100; version++)
        {
            Assert.True(ImeiForm.TryReduce($"{Fourteen}{version:00}", out var imei, out _));
            Assert.Equal(Fourteen, imei);
        }
    }
}
