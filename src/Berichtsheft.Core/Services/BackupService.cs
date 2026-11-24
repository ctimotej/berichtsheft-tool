using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Berichtsheft.Core.Config;
using Berichtsheft.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Berichtsheft.Core.Services;

/// <summary>Backup of the data folder (database, reports, templates) as a ZIP, and restore: the ZIP is checked and
/// unpacked next to the data folder ("staged"); at the next program start <see cref="ApplyPendingRestore"/> swaps the
/// folders, keeping the old data as "data.vor-wiederherstellung-…".</summary>
public static class BackupService
{
    public const string InfoFile = "berichtsheft-backup.json";
    private const string DbEntry = "berichtsheft.db";
    private static readonly string[] Folders = { "reports", "templates" };

    public static string StagingDir(AppConfig cfg) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(cfg.DataDir)) + ".restore";

    public static void Create(AppConfig cfg, string zipPath, string version)
    {
        var tmpDb = Path.Combine(Path.GetTempPath(), "bh-backup-" + Guid.NewGuid().ToString("N") + ".db");
        var tmpZip = zipPath + ".tmp";
        try
        {
            // consistent copy of the database even while the program uses it
            using (var src = new SqliteConnection($"Data Source={cfg.DbPath};Mode=ReadOnly;Pooling=False"))
            using (var dst = new SqliteConnection($"Data Source={tmpDb};Pooling=False"))
            {
                src.Open(); dst.Open();
                src.BackupDatabase(dst);
            }
            if (File.Exists(tmpZip)) File.Delete(tmpZip);
            using (var zip = ZipFile.Open(tmpZip, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(tmpDb, DbEntry, CompressionLevel.Optimal);
                foreach (var folder in Folders)
                {
                    var dir = Path.Combine(cfg.DataDir, folder);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        zip.CreateEntryFromFile(f, folder + "/" + Path.GetRelativePath(dir, f).Replace('\\', '/'), CompressionLevel.Optimal);
                }
                var info = zip.CreateEntry(InfoFile);
                using var s = info.Open();
                JsonSerializer.Serialize(s, new { format = 1, program_version = version, created = DateTime.Now, migrations = KnownMigrations().ToList() });
            }
            File.Move(tmpZip, zipPath, overwrite: true);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or SqliteException)
        {
            throw new StorageError("Die Sicherung konnte nicht erstellt werden: " + exc.Message, exc);
        }
        finally
        {
            try { File.Delete(tmpDb); } catch { /* best effort */ }
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { /* best effort */ }
        }
    }

    /// <summary>Checks the backup and unpacks it next to the data folder. Nothing is staged if anything is wrong.</summary>
    public static void StageRestore(AppConfig cfg, string zipPath)
    {
        var staging = StagingDir(cfg);
        var partial = staging + ".partial";
        try
        {
            if (!File.Exists(zipPath)) throw new InvalidInputError("Die Sicherungsdatei wurde nicht gefunden.");
            if (new FileInfo(zipPath).Length > cfg.MaxBackupBytes) throw new InvalidInputError("Die Sicherungsdatei ist zu groß.");
            if (Directory.Exists(partial)) Directory.Delete(partial, true);
            Directory.CreateDirectory(partial);
            var root = Path.GetFullPath(partial) + Path.DirectorySeparatorChar;
            using (var zip = OpenZip(zipPath))
            {
                if (zip.GetEntry(DbEntry) == null || zip.GetEntry(InfoFile) == null)
                    throw new InvalidInputError("Die Datei ist keine Sicherung dieses Programms.");
                long total = 0;
                foreach (var e in zip.Entries)
                {
                    var name = e.FullName.Replace('\\', '/');
                    var target = Path.GetFullPath(Path.Combine(partial, name));
                    if (name.StartsWith('/') || name.Contains(':') || !target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidInputError("Die Sicherung enthält ungültige Dateinamen und wird nicht verwendet.");
                    if (name.EndsWith('/')) continue;
                    var top = name.Split('/')[0];
                    if (name != DbEntry && name != InfoFile && !Folders.Contains(top)) continue; // ignore anything else
                    total += e.Length;
                    if (total > cfg.MaxBackupBytes * 4) throw new InvalidInputError("Die Sicherung ist zu groß.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    e.ExtractToFile(target, overwrite: true);
                }
            }
            CheckDatabase(Path.Combine(partial, DbEntry));
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.Move(partial, staging);
        }
        catch (Exception exc)
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(partial)) Directory.Delete(partial, true); } catch { /* best effort */ }
            if (exc is AppError) throw;
            if (exc is IOException or UnauthorizedAccessException or InvalidDataException)
                throw new InvalidInputError("Die Sicherung konnte nicht gelesen werden: " + exc.Message);
            throw;
        }
    }

    /// <summary>Swaps in a staged restore (call before the database is opened). Returns the folder with the old data, or null.</summary>
    public static string? ApplyPendingRestore(AppConfig cfg)
    {
        var staging = StagingDir(cfg);
        if (!Directory.Exists(staging)) return null;
        var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cfg.DataDir));
        var kept = $"{data}.vor-wiederherstellung-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        var hadData = Directory.Exists(data);
        if (hadData) Directory.Move(data, kept);
        try { Directory.Move(staging, data); }
        catch
        {
            if (hadData && !Directory.Exists(data)) Directory.Move(kept, data); // put the old data back
            throw;
        }
        if (hadData && Directory.Exists(Path.Combine(kept, "logs")))
        {
            try { CopyDir(Path.Combine(kept, "logs"), Path.Combine(data, "logs")); } catch (IOException) { }
        }
        return hadData ? kept : data;
    }

    private static ZipArchive OpenZip(string path)
    {
        try { return ZipFile.OpenRead(path); }
        catch (InvalidDataException) { throw new InvalidInputError("Die Datei ist kein gültiges ZIP-Archiv."); }
    }

    public static IEnumerable<string> KnownMigrations() =>
        typeof(BerichtsheftDbContext).Assembly.GetTypes()
            .Select(t => t.GetCustomAttribute<MigrationAttribute>()?.Id).Where(id => id != null).Select(id => id!).OrderBy(id => id);

    private static void CheckDatabase(string path)
    {
        const string foreign = "Die Sicherung enthält keine Datenbank dieses Programms oder sie ist beschädigt.";
        try
        {
            using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            c.Open();
            using (var ic = c.CreateCommand())
            {
                ic.CommandText = "pragma integrity_check";
                if (ic.ExecuteScalar() as string != "ok") throw new InvalidInputError(foreign);
            }
            var tables = new HashSet<string>();
            using (var tc = c.CreateCommand())
            {
                tc.CommandText = "select name from sqlite_master where type='table'";
                using var r = tc.ExecuteReader();
                while (r.Read()) tables.Add(r.GetString(0));
            }
            if (!new[] { "__EFMigrationsHistory", "reports", "settings", "departments" }.All(tables.Contains)) throw new InvalidInputError(foreign);
            var known = KnownMigrations().ToHashSet();
            using var mc = c.CreateCommand();
            mc.CommandText = "select migration_id from \"__EFMigrationsHistory\"";
            using var mr = mc.ExecuteReader();
            while (mr.Read())
                if (!known.Contains(mr.GetString(0)))
                    throw new InvalidInputError("Die Sicherung stammt aus einer neueren Version des Programms. Bitte zuerst das Programm aktualisieren.");
        }
        catch (SqliteException) { throw new InvalidInputError(foreign); }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src))
        {
            var target = Path.Combine(dst, Path.GetFileName(f));
            if (!File.Exists(target)) File.Copy(f, target);
        }
    }
}
