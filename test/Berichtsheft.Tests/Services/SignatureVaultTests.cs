using System.Security.Cryptography;
using Berichtsheft.Core.Services;
using Berichtsheft.Core.Signature;
using Berichtsheft.Tests.Hosting;
using Berichtsheft.Tests.TestHelpers;
using Xunit;

namespace Berichtsheft.Tests.Services;

public class SignatureVaultTests
{
    private static readonly byte[] Png = SignatureFixtures.MakeSignaturePng();

    [Fact]
    public void ProtectedValueRoundTrips_AndIsNotThePlainImage()
    {
        var stored = SignatureVault.Protect(Png);
        Assert.True(SignatureVault.IsProtected(stored));
        Assert.False(SignatureVault.IsProtected(Png));
        Assert.Equal(Png, SignatureVault.Unprotect(stored));
        if (OperatingSystem.IsWindows()) Assert.DoesNotContain((byte)'P', stored.Skip(6).Take(3)); // DPAPI: the PNG header is not visible
    }

    [Fact]
    public void UnprefixedImage_IsRejected() => Assert.Throws<CryptographicException>(() => SignatureVault.Unprotect(Png));

    [Fact]
    public void UnreadableValue_ThrowsCryptographicException()
    {
        var foreign = new byte[] { (byte)'B', (byte)'H', (byte)'S', (byte)'1', 1, 9, 9, 9, 9 }; // DPAPI scheme, garbage payload
        Assert.ThrowsAny<CryptographicException>(() => SignatureVault.Unprotect(foreign));
        Assert.Throws<CryptographicException>(() => SignatureVault.Unprotect(new byte[] { (byte)'B', (byte)'H', (byte)'S', (byte)'1', 77, 1 }));
    }

    [Fact]
    public void DefaultSignature_IsStoredProtected_AndReadBack()
    {
        using var t = new TestHost();
        using var s = t.Host.Open();
        s.Settings.SetDefaultSignature(Png);
        var raw = Convert.FromBase64String(s.Settings.GetFlagString("default_signature_png")!);
        Assert.True(SignatureVault.IsProtected(raw));
        Assert.Equal(Png, s.Settings.GetDefaultSignature());
        Assert.Null(s.Settings.LastSignatureProblem);
    }

    [Fact]
    public void UnreadableSignature_IsDeletedAfterThreeProgramStarts()
    {
        using var t = new TestHost();
        var bad = Convert.ToBase64String(new byte[] { (byte)'B', (byte)'H', (byte)'S', (byte)'1', 1, 9, 9, 9, 9 });
        for (var start = 1; start <= SignatureVault.MaxAttempts; start++)
        {
            SettingsService.ResetAttemptCounting(); // = a new program start
            using var s = t.Host.Open();
            if (start == 1) s.Settings.SetFlag("default_signature_png", bad);
            Assert.Null(s.Settings.GetDefaultSignature());
            var problem = s.Settings.LastSignatureProblem!;
            Assert.Null(s.Settings.GetDefaultSignature()); // a second read in the same run does not count again
            Assert.Equal(start, problem.Attempts);
            Assert.Equal(start == SignatureVault.MaxAttempts, problem.Deleted);
        }
        using var after = t.Host.Open();
        Assert.Null(after.Settings.GetFlagString("default_signature_png")); // the blob is gone
        Assert.Null(after.Settings.GetFlagRaw("sigfail:default_signature_png"));
    }
}
