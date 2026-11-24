using Berichtsheft.Core.Services;
using Microsoft.Data.Sqlite;

namespace Berichtsheft.Core.Data;

/// <summary>Protects against databases this version cannot upgrade (created by a build whose migrations are not
/// part of this one, or not a database of this program at all). Such a file is never changed: it is moved aside
/// and a fresh database is created, and the user is told where the old file is.</summary>
public static class DatabaseGuard
{
    /// <summary>Returns a message for the user when the database was moved aside, otherwise null.</summary>
    public static string? SetAsideIfForeign(string dbPath)
    {
        if (!File.Exists(dbPath) || new FileInfo(dbPath).Length == 0) return null;
        bool foreign;
        try
        {
            using var c = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            c.Open();
            var tables = new HashSet<string>();
            using (var tc = c.CreateCommand())
            {
                tc.CommandText = "select name from sqlite_master where type='table'";
                using var r = tc.ExecuteReader();
                while (r.Read()) tables.Add(r.GetString(0));
            }
            if (tables.Count == 0) return null; // empty file: Migrate() fills it
            if (!tables.Contains("__EFMigrationsHistory")) foreign = true;
            else
            {
                var known = BackupService.KnownMigrations().ToHashSet();
                var applied = new List<string>();
                using var mc = c.CreateCommand();
                mc.CommandText = "select migration_id from \"__EFMigrationsHistory\"";
                using var mr = mc.ExecuteReader();
                while (mr.Read()) applied.Add(mr.GetString(0));
                foreign = applied.Count > 0 && !applied.All(known.Contains);
            }
        }
        catch (SqliteException) { foreign = true; }
        catch (InvalidOperationException) { foreign = true; }
        if (!foreign) return null;

        SqliteConnection.ClearAllPools();
        var kept = Path.Combine(Path.GetDirectoryName(dbPath)!, $"berichtsheft.alt-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.db");
        File.Move(dbPath, kept);
        foreach (var ext in new[] { "-wal", "-shm" })
            if (File.Exists(dbPath + ext)) File.Move(dbPath + ext, kept + ext);
        return "Die vorhandene Datenbank stammt aus einer anderen Programmversion und kann von dieser Version nicht übernommen werden. " +
               $"Sie wurde unverändert hier abgelegt:\n{kept}\n\nDas Programm startet mit einer neuen Datenbank. Bereits erzeugte Word-Berichte " +
               "(Ordner data\\reports) lassen sich über „Datei > Eigene Berichte importieren“ wieder übernehmen.";
    }
}
