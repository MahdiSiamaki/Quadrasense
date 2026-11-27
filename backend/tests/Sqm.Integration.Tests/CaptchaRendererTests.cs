using SkiaSharp;
using Sqm.Application.Identity;
using Sqm.Infrastructure.Identity.Captcha;

namespace Sqm.Integration.Tests;

/// <summary>
/// The captcha's pictures, against the red line (§2-3 row 3): Persian characters in several Persian
/// typefaces, at least ten patterns, every character present in every typeface. No database: the
/// renderer is pure drawing, and runs wherever the tests run.
/// </summary>
public sealed class CaptchaRendererTests : IDisposable
{
    private readonly SkiaCaptchaRenderer _renderer = new();

    public void Dispose() => _renderer.Dispose();

    [Fact]
    public void There_are_at_least_ten_patterns_and_several_typefaces()
    {
        Assert.True(_renderer.PatternCount >= 10, $"{_renderer.PatternCount} patterns");
        Assert.True(_renderer.TypefaceCount >= 3, $"{_renderer.TypefaceCount} typefaces");
    }

    [Fact]
    public void Every_typeface_has_every_character()
    {
        var assembly = typeof(SkiaCaptchaRenderer).Assembly;

        foreach (var file in SkiaCaptchaRenderer.FontFiles)
        {
            using var stream = assembly.GetManifestResourceStream("Sqm.Captcha.Fonts." + file);
            Assert.NotNull(stream);
            using var data = SKData.Create(stream);
            using var face = SKTypeface.FromData(data);
            Assert.NotNull(face);
            using var font = new SKFont(face, 40);

            foreach (var c in CaptchaText.Alphabet)
            {
                Assert.True(font.ContainsGlyphs(c.ToString()), $"{file} has no U+{(int)c:X4}");
            }
        }
    }

    [Theory]
    [InlineData(CaptchaTheme.Light)]
    [InlineData(CaptchaTheme.Dark)]
    public void Every_pattern_draws_a_picture_of_the_right_size(CaptchaTheme theme)
    {
        for (var pattern = 0; pattern < _renderer.PatternCount; pattern++)
        {
            var png = _renderer.Render("ب۳ک۷م", theme, pattern, new Random(pattern));

            using var bitmap = SKBitmap.Decode(png);
            Assert.NotNull(bitmap);
            Assert.Equal(SkiaCaptchaRenderer.Width, bitmap.Width);
            Assert.Equal(SkiaCaptchaRenderer.Height, bitmap.Height);
        }
    }

    [Fact]
    public void The_patterns_draw_differently()
    {
        var pictures = Enumerable.Range(0, _renderer.PatternCount)
            .Select(pattern => Convert.ToHexString(_renderer.Render("ب۳ک۷م", CaptchaTheme.Light, pattern, new Random(7))))
            .ToList();

        Assert.Equal(pictures.Count, pictures.Distinct().Count());
    }

    [Fact]
    public void The_characters_change_the_picture_and_the_seed_alone_repeats_it()
    {
        var once = _renderer.Render("ب۳ک۷م", CaptchaTheme.Light, 0, new Random(1));
        var again = _renderer.Render("ب۳ک۷م", CaptchaTheme.Light, 0, new Random(1));
        var other = _renderer.Render("ن۲ع۶ل", CaptchaTheme.Light, 0, new Random(1));

        Assert.Equal(once, again);
        Assert.NotEqual(once, other);
    }

    [Fact]
    public void A_picture_mixes_typefaces()
    {
        var mixed = 0;
        var used = new HashSet<int>();
        const int pictures = 60;

        for (var i = 0; i < pictures; i++)
        {
            var faces = new int[CaptchaText.Length];
            _renderer.Render(CaptchaText.Generate(), CaptchaTheme.Light, i % _renderer.PatternCount, new Random(i), faces);

            used.UnionWith(faces);
            if (faces.Distinct().Count() > 1)
            {
                mixed++;
            }
        }

        // Five characters over five typefaces: all in one face happens 1 time in 625.
        Assert.True(mixed >= pictures - 3, $"{mixed} of {pictures} pictures mixed their typefaces");
        Assert.Equal(_renderer.TypefaceCount, used.Count);
    }
}
