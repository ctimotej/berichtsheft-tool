using SkiaSharp;

namespace Berichtsheft.Tests.TestHelpers;

// Ported from tests/conftest.py::make_signature_png
public static class SignatureFixtures
{
    public static byte[] MakeSignaturePng()
    {
        using var bitmap = new SKBitmap(400, 150, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint
        {
            Color = new SKColor(20, 30, 120, 255),
            StrokeWidth = 6,
            IsStroke = true,
            IsAntialias = true,
        };
        using var path = new SKPath();
#pragma warning disable CS0618 // SKPathBuilder is not yet available in the referenced SkiaSharp version
        path.MoveTo(20, 100);
        path.LineTo(120, 30);
        path.LineTo(220, 110);
        path.LineTo(380, 40);
#pragma warning restore CS0618
        canvas.DrawPath(path, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
