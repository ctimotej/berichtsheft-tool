using Berichtsheft.Core;
using Berichtsheft.Core.Signature;
using Berichtsheft.Tests.TestHelpers;
using SkiaSharp;
using Xunit;

namespace Berichtsheft.Tests.Signature;

// Ported from tests/test_features_v12.py (signature section)
public class ImageSignatureTests
{
    [Fact]
    public void ValidImageIsNormalized()
    {
        var provider = new ImageSignatureProvider();
        var res = provider.Sign(new SignatureRequest { ReportId = 1, SignerName = "Max", ImageData = SignatureFixtures.MakeSignaturePng() });
        Assert.Equal(0x89, res.ImagePng[0]);
        Assert.Equal((byte)'P', res.ImagePng[1]);
        Assert.Equal((byte)'N', res.ImagePng[2]);
        Assert.Equal((byte)'G', res.ImagePng[3]);
        Assert.Equal("visual", res.LegalLevel);
    }

    [Fact]
    public void InvalidData()
    {
        Assert.Throws<SignatureError>(() => ImageSignature.NormalizeSignatureImage("no image"u8.ToArray()));
        Assert.Throws<SignatureError>(() => ImageSignature.DecodeDataUrl("data:text/plain;base64,aGVsbG8="));
    }

    [Fact]
    public void EmptyCanvasRejected()
    {
        using var bitmap = new SKBitmap(300, 100, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(255, 255, 255, 0));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        Assert.Throws<SignatureError>(() => ImageSignature.NormalizeSignatureImage(data.ToArray()));
    }
}
