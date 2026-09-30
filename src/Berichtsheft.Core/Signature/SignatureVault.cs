using System.Security.Cryptography;

namespace Berichtsheft.Core.Signature;

/// <summary>Protects stored signature images. On Windows they are encrypted with DPAPI for the current Windows user
/// (<see cref="DataProtectionScope.CurrentUser"/>): the database and the backup ZIPs then contain no readable signature,
/// and they cannot be decrypted on another account or PC. Elsewhere (Linux tests) the image is only wrapped, not encrypted.
/// Stored form: "BHS1" + scheme byte + payload.</summary>
public static class SignatureVault
{
    /// <summary>Failed attempts (program starts) after which an unreadable signature is deleted.</summary>
    public const int MaxAttempts = 3;

    private static readonly byte[] Magic = { (byte)'B', (byte)'H', (byte)'S', (byte)'1' };
    private static readonly byte[] Entropy = System.Text.Encoding.UTF8.GetBytes("Berichtsheft.Signature.v1");
    private const byte Plain = 0, Dpapi = 1;

    public static bool IsProtected(byte[] stored) => stored.Length > Magic.Length && stored.AsSpan(0, Magic.Length).SequenceEqual(Magic);

    public static byte[] Protect(byte[] image)
    {
        var scheme = OperatingSystem.IsWindows() ? Dpapi : Plain;
        var payload = scheme == Dpapi ? ProtectDpapi(image) : image;
        var result = new byte[Magic.Length + 1 + payload.Length];
        Magic.CopyTo(result, 0);
        result[Magic.Length] = scheme;
        payload.CopyTo(result, Magic.Length + 1);
        return result;
    }

    /// <summary>The image, or a <see cref="CryptographicException"/> if it cannot be decrypted (other Windows user/PC, damaged data).</summary>
    public static byte[] Unprotect(byte[] stored)
    {
        if (!IsProtected(stored)) throw new CryptographicException("Unbekanntes Format der gespeicherten Unterschrift.");
        var scheme = stored[Magic.Length];
        var payload = stored.AsSpan(Magic.Length + 1).ToArray();
        return scheme switch
        {
            Plain => payload,
            Dpapi => UnprotectDpapi(payload),
            _ => throw new CryptographicException("Unbekanntes Format der gespeicherten Unterschrift."),
        };
    }

    private static byte[] ProtectDpapi(byte[] data) =>
        System.Security.Cryptography.ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    private static byte[] UnprotectDpapi(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) throw new CryptographicException("Die Unterschrift wurde unter Windows verschlüsselt und kann hier nicht gelesen werden.");
        return System.Security.Cryptography.ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
    }
}
