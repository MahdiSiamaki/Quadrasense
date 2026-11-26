using System.Reflection;
using SkiaSharp;
using Sqm.Application.Identity;

namespace Sqm.Infrastructure.Identity.Captcha;

/// <summary>
/// Draws the Persian captcha (ADR-026) to the red line's terms: Persian digits and letters, each in one of
/// several Persian typefaces, in one of twelve patterns, made hard for OCR - characters turned, slanted,
/// crowded and of mixed sizes, crossed by strokes in the same inks, over a textured ground with faint
/// decoys, and the whole bent by a wave.
/// </summary>
/// <remarks>
/// <para>
/// Drawn at twice the size it is shown (260 x 84 CSS pixels), so it stays sharp on a high-density screen.
/// </para>
/// <para>
/// Characters are drawn one at a time in their isolated forms, so no shaping engine is needed: Skia maps
/// each code point to its glyph, which for a lone Persian letter is the isolated form.
/// </para>
/// <para>
/// The typefaces are embedded (OFL 1.1, Tanha public domain; licences beside them in the output) and
/// checked when the renderer starts: a face missing any character of the alphabet stops the API rather
/// than drawing an empty box in someone's sign-in.
/// </para>
/// </remarks>
public sealed class SkiaCaptchaRenderer : ICaptchaRenderer, IDisposable
{
    /// <summary>Drawn width, in pixels: twice the width shown.</summary>
    public const int Width = 520;

    /// <summary>Drawn height, in pixels.</summary>
    public const int Height = 168;

    /// <summary>The embedded typefaces, by file name.</summary>
    public static IReadOnlyList<string> FontFiles { get; } =
        ["Vazirmatn-Regular.ttf", "Sahel.ttf", "Samim.ttf", "Shabnam.ttf", "Tanha.ttf"];

    private enum Ground { Gradient, Dots, Grid, Hatch, Waves, Speckle }

    private enum Strokes { Curves, Arcs, Strikes, Mixed }

    private enum Bend { None, Wave, Ripple, Both }

    private sealed record Pattern(Ground Ground, Strokes Strokes, Bend Bend, bool Decoys, float Turn, float Crowd);

    /// <summary>
    /// The twelve patterns: no two share their ground, strokes, bend and decoys together, and each turns and
    /// crowds the characters by its own amount.
    /// </summary>
    private static readonly Pattern[] Patterns =
    [
        new(Ground.Gradient, Strokes.Curves, Bend.Wave, Decoys: false, Turn: 22, Crowd: 1.00f),
        new(Ground.Dots, Strokes.Arcs, Bend.Ripple, Decoys: false, Turn: 26, Crowd: 0.92f),
        new(Ground.Grid, Strokes.Strikes, Bend.Wave, Decoys: true, Turn: 18, Crowd: 0.96f),
        new(Ground.Hatch, Strokes.Curves, Bend.Both, Decoys: false, Turn: 24, Crowd: 0.90f),
        new(Ground.Waves, Strokes.Mixed, Bend.None, Decoys: true, Turn: 28, Crowd: 0.94f),
        new(Ground.Speckle, Strokes.Curves, Bend.Ripple, Decoys: false, Turn: 20, Crowd: 1.00f),
        new(Ground.Gradient, Strokes.Strikes, Bend.Both, Decoys: true, Turn: 25, Crowd: 0.93f),
        new(Ground.Dots, Strokes.Mixed, Bend.Wave, Decoys: false, Turn: 30, Crowd: 0.97f),
        new(Ground.Grid, Strokes.Curves, Bend.Ripple, Decoys: false, Turn: 21, Crowd: 0.91f),
        new(Ground.Hatch, Strokes.Arcs, Bend.Wave, Decoys: true, Turn: 27, Crowd: 0.95f),
        new(Ground.Waves, Strokes.Strikes, Bend.Both, Decoys: false, Turn: 23, Crowd: 0.98f),
        new(Ground.Speckle, Strokes.Mixed, Bend.Wave, Decoys: true, Turn: 29, Crowd: 0.92f),
    ];

    private sealed record Palette(SKColor Paper, SKColor PaperShade, SKColor[] Inks);

    // Inks of similar weight, so neither the characters nor the strokes can be told apart by colour.
    private static readonly Palette LightPalette = new(
        new SKColor(0xF3, 0xF5, 0xFA), new SKColor(0xDD, 0xE3, 0xEF),
        [
            new SKColor(0x1F, 0x2A, 0x5C), new SKColor(0x6B, 0x1E, 0x3A), new SKColor(0x1D, 0x5C, 0x45),
            new SKColor(0x4B, 0x2A, 0x6E), new SKColor(0x33, 0x3A, 0x45), new SKColor(0x7A, 0x3E, 0x12),
        ]);

    private static readonly Palette DarkPalette = new(
        new SKColor(0x1B, 0x1F, 0x2A), new SKColor(0x2A, 0x31, 0x40),
        [
            new SKColor(0xC9, 0xD6, 0xFF), new SKColor(0xF5, 0xB8, 0xC8), new SKColor(0xA8, 0xE6, 0xC8),
            new SKColor(0xDA, 0xC4, 0xF5), new SKColor(0xE2, 0xE6, 0xEE), new SKColor(0xF5, 0xCF, 0xA0),
        ]);

    private readonly SKTypeface[] _faces;

    /// <summary>Loads and checks the embedded typefaces.</summary>
    /// <exception cref="InvalidOperationException">A typeface is missing, or lacks a character.</exception>
    public SkiaCaptchaRenderer()
    {
        var assembly = typeof(SkiaCaptchaRenderer).Assembly;
        var faces = new List<SKTypeface>(FontFiles.Count);

        try
        {
            foreach (var file in FontFiles)
            {
                faces.Add(Load(assembly, file));
            }
        }
        catch
        {
            foreach (var face in faces)
            {
                face.Dispose();
            }

            throw;
        }

        _faces = [.. faces];
    }

    /// <inheritdoc />
    public int PatternCount => Patterns.Length;

    /// <summary>How many typefaces the characters are drawn in.</summary>
    public int TypefaceCount => _faces.Length;

    /// <inheritdoc />
    public byte[] Render(string text, CaptchaTheme theme, int pattern, Random random)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentOutOfRangeException.ThrowIfNegative(pattern);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pattern, Patterns.Length);
        ArgumentNullException.ThrowIfNull(random);

        return Render(text, theme, Patterns[pattern], random, faceUsed: null);
    }

    /// <summary>As <see cref="Render(string, CaptchaTheme, int, Random)"/>, saying which typeface each character got.</summary>
    internal byte[] Render(string text, CaptchaTheme theme, int pattern, Random random, int[] faceUsed) =>
        Render(text, theme, Patterns[pattern], random, faceUsed);

    private byte[] Render(string text, CaptchaTheme theme, Pattern pattern, Random random, int[]? faceUsed)
    {
        var palette = theme == CaptchaTheme.Dark ? DarkPalette : LightPalette;
        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);

        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException("Skia could not make a drawing surface.");
        var canvas = surface.Canvas;

        DrawGround(canvas, pattern.Ground, palette, random);

        if (pattern.Decoys)
        {
            DrawDecoys(canvas, palette, random);
        }

        DrawCharacters(canvas, text, pattern, palette, random, faceUsed);
        DrawStrokes(canvas, pattern.Strokes, palette, random);

        var image = surface.Snapshot();

        try
        {
            // The bend: rows shifted sideways (a wave), then columns up and down (a ripple).
            if (pattern.Bend is Bend.Wave or Bend.Both)
            {
                var waved = Shift(image, palette.Paper, rows: true, amplitude: Next(random, 5, 9), period: Next(random, 70, 120), random);
                image.Dispose();
                image = waved;
            }

            if (pattern.Bend is Bend.Ripple or Bend.Both)
            {
                var rippled = Shift(image, palette.Paper, rows: false, amplitude: Next(random, 4, 8), period: Next(random, 90, 170), random);
                image.Dispose();
                image = rippled;
            }

            using var png = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("Skia could not encode the picture.");

            return png.ToArray();
        }
        finally
        {
            image.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // The ground
    // -----------------------------------------------------------------------

    private static void DrawGround(SKCanvas canvas, Ground ground, Palette palette, Random random)
    {
        using (var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(Width, Height),
            [palette.Paper, palette.PaperShade, palette.Paper], [0f, Next(random, 0.3f, 0.7f), 1f],
            SKShaderTileMode.Clamp))
        using (var fill = new SKPaint { Shader = shader })
        {
            canvas.DrawRect(0, 0, Width, Height, fill);
        }

        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f };

        switch (ground)
        {
            case Ground.Dots:
                paint.Style = SKPaintStyle.Fill;
                for (var i = 0; i < 260; i++)
                {
                    paint.Color = Ink(palette, random, 0x30, 0x70);
                    canvas.DrawCircle(Next(random, 0, Width), Next(random, 0, Height), Next(random, 1.2f, 3.4f), paint);
                }

                break;

            case Ground.Grid:
                var step = Next(random, 18, 30);
                paint.Color = Ink(palette, random, 0x28, 0x48);
                for (var x = Next(random, 0, step); x < Width; x += step)
                {
                    canvas.DrawLine(x, 0, x + Next(random, -12, 12), Height, paint);
                }

                for (var y = Next(random, 0, step); y < Height; y += step)
                {
                    canvas.DrawLine(0, y, Width, y + Next(random, -8, 8), paint);
                }

                break;

            case Ground.Hatch:
                paint.Color = Ink(palette, random, 0x26, 0x46);
                var slope = Next(random, 0.5f, 1.6f) * (random.Next(2) == 0 ? 1 : -1);
                for (var x = -Height * 2f; x < Width + Height * 2f; x += Next(random, 9, 15))
                {
                    canvas.DrawLine(x, 0, x + Height * slope, Height, paint);
                }

                break;

            case Ground.Waves:
                for (var line = 0; line < 9; line++)
                {
                    paint.Color = Ink(palette, random, 0x2A, 0x50);
                    using var builder = new SKPathBuilder();
                    var y0 = Next(random, 0, Height);
                    var amplitude = Next(random, 6, 18);
                    var period = Next(random, 60, 140);
                    var phase = Next(random, 0, MathF.Tau);
                    builder.MoveTo(0, y0);
                    for (var x = 4f; x <= Width; x += 4f)
                    {
                        builder.LineTo(x, y0 + amplitude * MathF.Sin(x / period * MathF.Tau + phase));
                    }

                    using var path = builder.Detach();
                    canvas.DrawPath(path, paint);
                }

                break;

            case Ground.Speckle:
                paint.Style = SKPaintStyle.Fill;
                for (var i = 0; i < 900; i++)
                {
                    paint.Color = Ink(palette, random, 0x22, 0x55);
                    var x = Next(random, 0, Width);
                    var y = Next(random, 0, Height);
                    canvas.DrawRect(x, y, Next(random, 1, 3), Next(random, 1, 3), paint);
                }

                break;

            case Ground.Gradient:
            default:
                break;
        }
    }

    /// <summary>Small, faint characters of the same alphabet: a person looks past them; a segmenter does not.</summary>
    private void DrawDecoys(SKCanvas canvas, Palette palette, Random random)
    {
        using var paint = new SKPaint { IsAntialias = true };

        for (var i = 0; i < 14; i++)
        {
            paint.Color = Ink(palette, random, 0x1C, 0x34);
            using var font = new SKFont(_faces[random.Next(_faces.Length)], Next(random, 22, 38));
            var c = CaptchaText.Alphabet[random.Next(CaptchaText.Alphabet.Length)].ToString();

            canvas.Save();
            canvas.Translate(Next(random, 10, Width - 10), Next(random, 24, Height - 6));
            canvas.RotateDegrees(Next(random, -40, 40));
            canvas.DrawText(c, 0, 0, SKTextAlign.Center, font, paint);
            canvas.Restore();
        }
    }

    // -----------------------------------------------------------------------
    // The characters
    // -----------------------------------------------------------------------

    /// <summary>
    /// Right to left, as Persian is read: the first character at the right. Each in a typeface of its own,
    /// at its own size, turned and slanted, and close enough to its neighbours to touch them.
    /// </summary>
    private void DrawCharacters(
        SKCanvas canvas, string text, Pattern pattern, Palette palette, Random random, int[]? faceUsed)
    {
        const float margin = 30;
        var step = (Width - 2 * margin) / text.Length * pattern.Crowd;
        var start = Width - (Width - step * text.Length) / 2;

        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };

        for (var i = 0; i < text.Length; i++)
        {
            var face = random.Next(_faces.Length);
            if (faceUsed is not null && i < faceUsed.Length)
            {
                faceUsed[i] = face;
            }

            using var font = new SKFont(_faces[face], Next(random, 78, 100), scaleX: Next(random, 0.85f, 1.15f), skewX: Next(random, -0.28f, 0.28f));
            var c = text[i].ToString();

            // Centred on its own box, so turning it turns it about its middle.
            font.MeasureText(c, out var bounds);
            var x = start - step * (i + 0.5f) + Next(random, -step * 0.12f, step * 0.12f);
            var y = Height / 2f + Next(random, -14, 14);

            fill.Color = Ink(palette, random, 0xE6, 0xFF);

            canvas.Save();
            canvas.Translate(x, y);
            canvas.RotateDegrees(Next(random, -pattern.Turn, pattern.Turn));
            canvas.DrawText(c, -bounds.MidX, -bounds.MidY, SKTextAlign.Left, font, fill);

            // An outline in another ink on some characters: no single colour holds the whole glyph.
            if (random.Next(3) == 0)
            {
                edge.Color = Ink(palette, random, 0xB0, 0xE0);
                canvas.DrawText(c, -bounds.MidX + Next(random, -2, 2), -bounds.MidY + Next(random, -2, 2), SKTextAlign.Left, font, edge);
            }

            canvas.Restore();
        }
    }

    // -----------------------------------------------------------------------
    // Strokes across the characters, in the characters' inks
    // -----------------------------------------------------------------------

    private static void DrawStrokes(SKCanvas canvas, Strokes strokes, Palette palette, Random random)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round,
        };

        switch (strokes)
        {
            case Strokes.Curves:
                Curves(canvas, paint, palette, random, count: 3);
                break;

            case Strokes.Arcs:
                Arcs(canvas, paint, palette, random, count: 6);
                break;

            case Strokes.Strikes:
                Strikes(canvas, paint, palette, random, count: 3);
                break;

            case Strokes.Mixed:
            default:
                Curves(canvas, paint, palette, random, count: 1);
                Arcs(canvas, paint, palette, random, count: 3);
                Strikes(canvas, paint, palette, random, count: 1);
                break;
        }
    }

    private static void Curves(SKCanvas canvas, SKPaint paint, Palette palette, Random random, int count)
    {
        for (var i = 0; i < count; i++)
        {
            paint.Color = Ink(palette, random, 0xC0, 0xF0);
            paint.StrokeWidth = Next(random, 3.5f, 6.5f);
            using var builder = new SKPathBuilder();
            builder.MoveTo(Next(random, -20, 30), Next(random, 20, Height - 20));
            builder.CubicTo(
                Next(random, Width * 0.2f, Width * 0.45f), Next(random, -40, Height + 40),
                Next(random, Width * 0.55f, Width * 0.8f), Next(random, -40, Height + 40),
                Next(random, Width - 30, Width + 20), Next(random, 20, Height - 20));
            using var path = builder.Detach();
            canvas.DrawPath(path, paint);
        }
    }

    private static void Arcs(SKCanvas canvas, SKPaint paint, Palette palette, Random random, int count)
    {
        for (var i = 0; i < count; i++)
        {
            paint.Color = Ink(palette, random, 0xB0, 0xE8);
            paint.StrokeWidth = Next(random, 3f, 5.5f);
            var cx = Next(random, 0, Width);
            var cy = Next(random, 0, Height);
            var rx = Next(random, 40, 130);
            var ry = Next(random, 25, 80);
            canvas.DrawArc(new SKRect(cx - rx, cy - ry, cx + rx, cy + ry), Next(random, 0, 360), Next(random, 70, 200), useCenter: false, paint);
        }
    }

    private static void Strikes(SKCanvas canvas, SKPaint paint, Palette palette, Random random, int count)
    {
        for (var i = 0; i < count; i++)
        {
            paint.Color = Ink(palette, random, 0xC0, 0xF0);
            paint.StrokeWidth = Next(random, 3f, 5f);
            using var builder = new SKPathBuilder();
            builder.MoveTo(0, Next(random, Height * 0.3f, Height * 0.7f));
            builder.QuadTo(Width / 2f, Next(random, Height * 0.2f, Height * 0.8f), Width, Next(random, Height * 0.3f, Height * 0.7f));
            using var path = builder.Detach();
            canvas.DrawPath(path, paint);
        }
    }

    // -----------------------------------------------------------------------
    // The bend
    // -----------------------------------------------------------------------

    /// <summary>
    /// Shifts rows sideways, or columns up and down, along a sine curve, so no character sits on a straight
    /// baseline and no stroke stays straight. Edges uncovered by a shift show the paper.
    /// </summary>
    private static SKImage Shift(SKImage source, SKColor paper, bool rows, float amplitude, float period, Random random)
    {
        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException("Skia could not make a drawing surface.");
        var canvas = surface.Canvas;
        canvas.Clear(paper);

        var phase = Next(random, 0, MathF.Tau);
        const int band = 2;
        var length = rows ? Height : Width;

        for (var at = 0; at < length; at += band)
        {
            var offset = amplitude * MathF.Sin(at / period * MathF.Tau + phase);
            var from = rows ? new SKRect(0, at, Width, at + band) : new SKRect(at, 0, at + band, Height);
            var to = rows ? new SKRect(offset, at, Width + offset, at + band) : new SKRect(at, offset, at + band, Height + offset);
            canvas.DrawImage(source, from, to, SKSamplingOptions.Default);
        }

        return surface.Snapshot();
    }

    // -----------------------------------------------------------------------
    // Small parts
    // -----------------------------------------------------------------------

    private static SKColor Ink(Palette palette, Random random, byte alphaFrom, byte alphaTo) =>
        palette.Inks[random.Next(palette.Inks.Length)].WithAlpha((byte)random.Next(alphaFrom, alphaTo + 1));

    private static float Next(Random random, float from, float to) => from + (float)random.NextDouble() * (to - from);

    private static SKTypeface Load(Assembly assembly, string file)
    {
        var name = "Sqm.Captcha.Fonts." + file;
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The captcha typeface {file} is not embedded.");
        using var data = SKData.Create(stream);
        var face = SKTypeface.FromData(data)
            ?? throw new InvalidOperationException($"The captcha typeface {file} could not be read.");

        using var probe = new SKFont(face, 40);
        foreach (var c in CaptchaText.Alphabet)
        {
            if (!probe.ContainsGlyphs(c.ToString()))
            {
                face.Dispose();
                throw new InvalidOperationException($"The captcha typeface {file} has no glyph for U+{(int)c:X4}.");
            }
        }

        return face;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var face in _faces)
        {
            face.Dispose();
        }
    }
}
