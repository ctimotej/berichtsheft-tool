using System.IO.Compression;
using Berichtsheft.Core;
using Berichtsheft.Core.Services;
using Berichtsheft.Core.Storage;
using Berichtsheft.Tests.Hosting;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Berichtsheft.Tests.Services;

public class BackupRestoreTests
{
    [Fact]
    public void RealBackup_CanBeStaged()
    {
        using var t = new TestHost();
        var zip = Path.Combine(t.DataDir + "-bak.zip");
        try
        {
            BackupService.Create(t.Host.Config, zip, "test");
            BackupService.StageRestore(t.Host.Config, zip);
            Assert.True(File.Exists(Path.Combine(BackupService.StagingDir(t.Host.Config), "berichtsheft.db")));
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(zip); try { Directory.Delete(BackupService.StagingDir(t.Host.Config), true); } catch { } }
    }

    private static string MakeZip(string dir, Action<ZipArchive> fill)
    {
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".zip");
        using (var z = ZipFile.Open(path, ZipArchiveMode.Create)) fill(z);
        return path;
    }

    private static void Put(ZipArchive z, string name, byte[] data) { using var s = z.CreateEntry(name).Open(); s.Write(data); }

    [Fact]
    public void BackupWithForeignOrBrokenDatabase_IsRejected_AndNothingIsStaged()
    {
        using var t = new TestHost();
        var zip = MakeZip(Path.GetTempPath(), z =>
        {
            Put(z, "berichtsheft.db", System.Text.Encoding.ASCII.GetBytes("this is not a database"));
            Put(z, BackupService.InfoFile, new byte[] { 1 });
        });
        try
        {
            Assert.Throws<InvalidInputError>(() => BackupService.StageRestore(t.Host.Config, zip));
            Assert.False(Directory.Exists(BackupService.StagingDir(t.Host.Config)));
            Assert.False(Directory.Exists(BackupService.StagingDir(t.Host.Config) + ".partial"));
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public void BackupOfAnotherSqliteFile_IsRejected()
    {
        using var t = new TestHost();
        var other = Path.Combine(Path.GetTempPath(), "bh-other-" + Guid.NewGuid().ToString("N") + ".db");
        using (var c = new SqliteConnection($"Data Source={other};Pooling=False")) { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "create table foo(x)"; cmd.ExecuteNonQuery(); }
        var zip = MakeZip(Path.GetTempPath(), z => { Put(z, "berichtsheft.db", File.ReadAllBytes(other)); Put(z, BackupService.InfoFile, new byte[] { 1 }); });
        try { Assert.Throws<InvalidInputError>(() => BackupService.StageRestore(t.Host.Config, zip)); }
        finally { File.Delete(zip); File.Delete(other); }
    }

    [Fact]
    public void BackupFromNewerVersion_IsRejected()
    {
        using var t = new TestHost();
        var zip = Path.Combine(t.DataDir + "-new.zip");
        var tmp = Path.Combine(t.DataDir + "-new.db");
        try
        {
            BackupService.Create(t.Host.Config, zip, "test");
            using (var z = ZipFile.OpenRead(zip)) z.GetEntry("berichtsheft.db")!.ExtractToFile(tmp, true);
            using (var c = new SqliteConnection($"Data Source={tmp};Pooling=False"))
            {
                c.Open(); using var cmd = c.CreateCommand();
                cmd.CommandText = "insert into \"__EFMigrationsHistory\"(migration_id, product_version) values ('29990101000000_FromTheFuture','8.0.10')";
                cmd.ExecuteNonQuery();
            }
            var zip2 = MakeZip(Path.GetTempPath(), z => { Put(z, "berichtsheft.db", File.ReadAllBytes(tmp)); Put(z, BackupService.InfoFile, new byte[] { 1 }); });
            var ex = Assert.Throws<InvalidInputError>(() => BackupService.StageRestore(t.Host.Config, zip2));
            Assert.Contains("neueren", ex.Message);
            File.Delete(zip2);
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(zip); File.Delete(tmp); }
    }
}

public class FilenamesTests
{
    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.docx", "_nul.docx")]
    [InlineData("Com1.txt", "_Com1.txt")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData("Console", "Console")]
    [InlineData("Müller", "Mueller")]
    public void ReservedWindowsNames_AreDefused(string input, string expected) => Assert.Equal(expected, Filenames.SafeFilename(input));
}
