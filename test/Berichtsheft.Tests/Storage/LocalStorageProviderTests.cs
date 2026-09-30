using Berichtsheft.Core;
using Berichtsheft.Core.Storage;
using Xunit;

namespace Berichtsheft.Tests.Storage;

// Ported from tests/test_features_v12.py (storage section)
public class LocalStorageProviderTests
{
    [Fact]
    public void RoundtripAndTraversal()
    {
        var tmp = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var s = new LocalStorageProvider(Path.Combine(tmp, "reports"));
            var doc = s.UploadDocument("Bericht KW40.docx", "abc"u8.ToArray(), "2026");
            Assert.Equal("abc"u8.ToArray(), s.DownloadDocument(doc.Key));
            Assert.Equal(new[] { doc.Key }, s.ListDocuments("2026").Select(d => d.Key));
            Assert.Throws<StorageError>(() => s.DownloadDocument("../../etc/passwd"));
            s.DeleteDocument(doc.Key);
            Assert.Empty(s.ListDocuments("2026"));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void SafeFilename()
    {
        var result = Filenames.SafeFilename("Müller/../x.docx");
        Assert.True(result == "Mueller_.._x.docx" || !result.Contains('/'));
        Assert.Equal("dokument", Filenames.SafeFilename(""));
    }
}
