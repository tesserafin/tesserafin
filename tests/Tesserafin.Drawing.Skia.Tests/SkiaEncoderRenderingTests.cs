using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Drawing;
using Tesserafin.Model.Drawing;
using Xunit;

namespace Tesserafin.Drawing.Skia.Tests;

/// <summary>
/// Exercises the real SkiaSharp and HarfBuzzSharp native libraries through <see cref="SkiaEncoder"/>:
/// a managed/native version mismatch compiles and restores cleanly and only fails here, when
/// text is shaped, an SVG is rasterised, an image is resized or a BlurHash is computed.
/// </summary>
public sealed class SkiaEncoderRenderingTests : IDisposable
{
    private const string Accented = "Séries été à Noël";
    private const string Arabic = "مكتبة الأفلام";

    private static readonly SKColor _posterColor = new(40, 90, 160);

    private readonly string _root = Directory.CreateTempSubdirectory("tesserafin-skia-").FullName;
    private readonly SkiaEncoder _encoder;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkiaEncoderRenderingTests"/> class.
    /// </summary>
    public SkiaEncoderRenderingTests()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.TempDirectory).Returns(_root);
        paths.SetupGet(p => p.DataPath).Returns(_root);
        _encoder = new SkiaEncoder(NullLogger<SkiaEncoder>.Instance, paths.Object);
    }

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// Both native libraries load, and HarfBuzz shapes text into one positioned glyph per cluster.
    /// </summary>
    [Fact]
    public void NativeLibraries_LoadAndHarfBuzzShapesText()
    {
        Assert.True(SkiaEncoder.IsNativeLibAvailable());

        var typeface = SkiaEncoder.GetFontForCharacter("م");
        Assert.NotNull(typeface);

        using var shaper = new SKShaper(typeface!);
        using var font = new SKFont(typeface, 32);
        var result = shaper.Shape(Arabic, font);

        Assert.NotEmpty(result.Codepoints);
        Assert.Equal(result.Codepoints.Length, result.Points.Length);
        Assert.All(result.Codepoints, glyph => Assert.NotEqual(0u, glyph));
    }

    /// <summary>
    /// A library thumbnail collage draws an accented and an Arabic library name: the shaped text
    /// covers pixels a nameless collage leaves at the poster colour, and the two names differ.
    /// </summary>
    [Fact]
    public void ThumbCollage_DrawsAccentedAndArabicLibraryNames()
    {
        var posters = Enumerable.Range(0, 4).Select(i => WritePoster($"poster{i}.png")).ToArray();

        using var plain = Collage(posters, "plain.png", libraryName: null);
        using var accented = Collage(posters, "accented.png", Accented);
        using var arabic = Collage(posters, "arabic.png", Arabic);

        var accentedChanged = ChangedPixels(plain, accented);
        var arabicChanged = ChangedPixels(plain, arabic);

        Assert.True(accentedChanged > 500, $"the accented name changed only {accentedChanged} pixels");
        Assert.True(arabicChanged > 500, $"the Arabic name changed only {arabicChanged} pixels");
        Assert.True(ChangedPixels(accented, arabic) > 500, "the accented and Arabic names rendered identically");
    }

    /// <summary>
    /// An SVG is measured and rasterised at its declared size, with its fill intact.
    /// </summary>
    [Fact]
    public void Svg_IsMeasuredAndRasterised()
    {
        var svg = Path.Combine(_root, "logo.svg");
        File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"60\"><rect width=\"120\" height=\"60\" fill=\"#ff0000\"/></svg>");

        var size = _encoder.GetImageSize(svg);
        Assert.Equal(120, size.Width);
        Assert.Equal(60, size.Height);

        var output = Path.Combine(_root, "logo.png");
        var written = _encoder.EncodeImage(svg, DateTime.UtcNow, output, false, null, 90, Options(), ImageFormat.Png);

        using var bitmap = SKBitmap.Decode(written);
        Assert.NotNull(bitmap);
        Assert.Equal(120, bitmap.Width);
        Assert.Equal(60, bitmap.Height);
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(60, 30));
    }

    /// <summary>
    /// A thumbnail is resized to the requested width and keeps the source content.
    /// </summary>
    [Fact]
    public void Thumbnail_IsResized()
    {
        var poster = WritePoster("large.png", 400, 600);
        var options = Options();
        options.Width = 100;

        var written = _encoder.EncodeImage(poster, DateTime.UtcNow, Path.Combine(_root, "thumb.jpg"), false, null, 90, options, ImageFormat.Jpg);

        using var bitmap = SKBitmap.Decode(written);
        Assert.NotNull(bitmap);
        Assert.Equal(100, bitmap.Width);
        Assert.Equal(150, bitmap.Height);
        var centre = bitmap.GetPixel(50, 75);
        Assert.InRange(centre.Blue, 140, 180);
    }

    /// <summary>
    /// The BlurHash of a uniform image is a well-formed hash whose average colour is that image's.
    /// </summary>
    [Fact]
    public void BlurHash_IsComputed()
    {
        var poster = WritePoster("hash.png", 64, 64);

        var hash = _encoder.GetImageBlurHash(4, 3, poster);

        // 1 size flag + 1 max AC + 4 DC + 2 per AC component (4 x 3 - 1).
        Assert.Equal(28, hash.Length);
        Assert.Equal(EncodeDc(_posterColor), hash.Substring(2, 4));
    }

    private static int ChangedPixels(SKBitmap a, SKBitmap b)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        var count = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var p = a.GetPixel(x, y);
                var q = b.GetPixel(x, y);
                if (Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue) > 48)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// The BlurHash DC component of a uniform colour: its sRGB value as a 4-digit base-83 number.
    /// </summary>
    private static string EncodeDc(SKColor color)
    {
        const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~";
        var value = (color.Red << 16) + (color.Green << 8) + color.Blue;
        var result = new char[4];
        for (var i = 3; i >= 0; i--)
        {
            result[i] = Digits[value % 83];
            value /= 83;
        }

        return new string(result);
    }

    private static ImageProcessingOptions Options() => new()
    {
        SupportedOutputFormats = [ImageFormat.Png, ImageFormat.Jpg],
        Quality = 90
    };

    private SKBitmap Collage(string[] posters, string name, string? libraryName)
    {
        var output = Path.Combine(_root, name);
        _encoder.CreateImageCollage(
            new ImageCollageOptions { InputPaths = posters, OutputPath = output, Width = 600, Height = 338 },
            libraryName);
        var bitmap = SKBitmap.Decode(output);
        Assert.NotNull(bitmap);
        return bitmap;
    }

    private string WritePoster(string name, int width = 200, int height = 300)
    {
        var path = Path.Combine(_root, name);
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(_posterColor);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }
}
