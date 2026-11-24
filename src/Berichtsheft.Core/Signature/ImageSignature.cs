using SkiaSharp;

namespace Berichtsheft.Core.Signature;

public sealed class SignatureRequest
{
    public int ReportId { get; set; }
    public string SignerName { get; set; } = "";
    public byte[] ImageData { get; set; } = Array.Empty<byte>();
}

public sealed class SignatureResult
{
    public byte[] ImagePng { get; set; } = Array.Empty<byte>();
    public string Provider { get; set; } = "";
    public DateTime SignedAt { get; set; }
    /// <summary>"visual": an image of a handwritten signature, not a qualified e-signature.</summary>
    public string LegalLevel { get; set; } = "visual";
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public interface ISignatureProvider
{
    string Name { get; }
    string LegalLevel { get; }
    SignatureResult Sign(SignatureRequest request);
}

public static class SignatureProviderRegistry
{
    public static ISignatureProvider GetSignatureProvider(string? name) => name?.ToLowerInvariant() switch
    {
        _ => new ImageSignatureProvider(),
    };
}

/// <summary>Visual signature: the normalized image of the handwritten signature.</summary>
public sealed class ImageSignatureProvider : ISignatureProvider
{
    public string Name => "image";
    public string LegalLevel => "visual";

    public SignatureResult Sign(SignatureRequest request) => new()
    {
        ImagePng = ImageSignature.NormalizeSignatureImage(request.ImageData),
        Provider = Name,
        SignedAt = DateTime.Now,
        LegalLevel = LegalLevel,
        Metadata = new() { ["signer"] = request.SignerName ?? "" },
    };
}

public static class ImageSignature
{
    public const int MaxBytes = 5 * 1024 * 1024;
    private const int MaxWidth = 1200;

    /// <summary>Accepts "data:image/png|jpeg|webp;base64,…".</summary>
    public static byte[] DecodeDataUrl(string dataUrl)
    {
        var s = (dataUrl ?? "").Trim();
        if (!s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) throw new SignatureError("Die Unterschrift hat kein gültiges Bildformat.");
        var comma = s.IndexOf(',');
        if (comma < 0) throw new SignatureError("Die Unterschrift hat kein gültiges Bildformat.");
        var head = s[5..comma].ToLowerInvariant();
        if (!head.EndsWith(";base64") || !(head.StartsWith("image/png") || head.StartsWith("image/jpeg") || head.StartsWith("image/jpg") || head.StartsWith("image/webp")))
            throw new SignatureError("Als Unterschrift sind nur PNG-, JPEG- oder WebP-Bilder möglich.");
        byte[] data;
        try { data = Convert.FromBase64String(s[(comma + 1)..]); }
        catch (FormatException) { throw new SignatureError("Die Unterschrift konnte nicht gelesen werden."); }
        if (data.Length > MaxBytes) throw new SignatureError("Das Bild der Unterschrift ist zu groß (höchstens 5 MB).");
        return data;
    }

    /// <summary>Crops the image to the ink, makes the background transparent and returns a PNG.
    /// Throws <see cref="SignatureError"/> for unreadable or empty images.</summary>
    public static byte[] NormalizeSignatureImage(byte[] data)
    {
        if (data == null || data.Length == 0) throw new SignatureError("Es wurde keine Unterschrift übergeben.");
        if (data.Length > MaxBytes) throw new SignatureError("Das Bild der Unterschrift ist zu groß (höchstens 5 MB).");
        SKBitmap? bmp;
        try
        {
            using var codec = SKCodec.Create(new MemoryStream(data));
            bmp = codec == null ? null : SKBitmap.Decode(codec);
        }
        catch (Exception exc) when (exc is ArgumentException or InvalidOperationException) { bmp = null; }
        using var decoded = bmp ?? throw new SignatureError("Die Datei ist kein lesbares Bild.");
        using var src = decoded.Copy(SKColorType.Rgba8888) ?? throw new SignatureError("Die Datei ist kein lesbares Bild.");

        int w = src.Width, h = src.Height, minX = w, minY = h, maxX = -1, maxY = -1;
        var ink = new bool[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = src.GetPixel(x, y);
            var lum = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
            if (c.Alpha < 60 || lum > 190) continue; // transparent or (nearly) white paper
            ink[y * w + x] = true;
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        if (maxX < 0 || (maxX - minX + 1) * (maxY - minY + 1) < 9)
            throw new SignatureError("Die Unterschrift ist leer – bitte im Feld unterschreiben.");

        const int pad = 4;
        minX = Math.Max(0, minX - pad); minY = Math.Max(0, minY - pad);
        maxX = Math.Min(w - 1, maxX + pad); maxY = Math.Min(h - 1, maxY + pad);
        var cw = maxX - minX + 1; var ch = maxY - minY + 1;
        using var outBmp = new SKBitmap(new SKImageInfo(cw, ch, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < ch; y++)
        for (var x = 0; x < cw; x++)
        {
            var sx = x + minX; var sy = y + minY;
            outBmp.SetPixel(x, y, ink[sy * w + sx] ? src.GetPixel(sx, sy).WithAlpha(255) : SKColors.Transparent);
        }

        SKBitmap final = outBmp;
        SKBitmap? scaled = null;
        if (cw > MaxWidth)
        {
            var nh = Math.Max(1, (int)Math.Round(ch * (double)MaxWidth / cw));
            scaled = outBmp.Resize(new SKImageInfo(MaxWidth, nh, SKColorType.Rgba8888, SKAlphaType.Unpremul), SKFilterQuality.High);
            if (scaled != null) final = scaled;
        }
        try
        {
            using var img = SKImage.FromBitmap(final);
            using var png = img.Encode(SKEncodedImageFormat.Png, 100);
            return png.ToArray();
        }
        finally { scaled?.Dispose(); }
    }

    /// <summary>Pixel size of an image (0,0 if unreadable).</summary>
    public static (int Width, int Height) PixelSize(byte[] data)
    {
        try
        {
            using var codec = SKCodec.Create(new MemoryStream(data));
            return codec == null ? (0, 0) : (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception) { return (0, 0); }
    }
}
