using System.Text;
using System.Text.RegularExpressions;

namespace Berichtsheft.Core.Storage;

public sealed record StoredDocument(string Key, string Name, long Size, DateTime Modified);

/// <summary>Where generated documents are kept. Keys are relative paths like "2026/Berichtsheft_2026_KW40.docx".</summary>
public interface IDocumentStorageProvider
{
    string Name { get; }
    StoredDocument UploadDocument(string filename, byte[] data, string folder = "");
    byte[] DownloadDocument(string key);
    List<StoredDocument> ListDocuments(string folder = "");
    void DeleteDocument(string key);
}

public static class Filenames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A file name that is valid on Windows: umlauts transliterated, separators and other special
    /// characters replaced by "_", reserved device names (CON, NUL, COM1 …) prefixed with "_".</summary>
    public static string SafeFilename(string? name, string fallback = "dokument")
    {
        var s = (name ?? "").Trim();
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            switch (ch)
            {
                case 'ä': sb.Append("ae"); break;
                case 'ö': sb.Append("oe"); break;
                case 'ü': sb.Append("ue"); break;
                case 'Ä': sb.Append("Ae"); break;
                case 'Ö': sb.Append("Oe"); break;
                case 'Ü': sb.Append("Ue"); break;
                case 'ß': sb.Append("ss"); break;
                default:
                    sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_');
                    break;
            }
        }
        var result = Regex.Replace(sb.ToString(), "_{2,}", "_").Trim(' ', '.');
        if (result.Length > 150) result = result[..150];
        if (result.Length == 0 || result.All(c => c is '_' or '.')) return fallback;
        var stem = result.Split('.')[0];
        if (Reserved.Contains(stem)) result = "_" + result;
        return result;
    }
}

/// <summary>Documents below a local folder (data/reports). Writes are atomic; keys must stay inside the folder.</summary>
public sealed class LocalStorageProvider : IDocumentStorageProvider
{
    private readonly string _root;
    public string Name => "local";

    public LocalStorageProvider(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    private string Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || Path.IsPathRooted(key)) throw new StorageError("Ungültiger Dokumentpfad.");
        var full = Path.GetFullPath(Path.Combine(_root, key));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new StorageError("Ungültiger Dokumentpfad.");
        return full;
    }

    private static string CleanFolder(string folder) =>
        string.Join('/', (folder ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(p => Filenames.SafeFilename(p, "_")));

    public StoredDocument UploadDocument(string filename, byte[] data, string folder = "")
    {
        var f = CleanFolder(folder);
        var key = (f.Length > 0 ? f + "/" : "") + Filenames.SafeFilename(filename);
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw new StorageError($"Das Dokument konnte nicht gespeichert werden ({exc.Message}). Ist es vielleicht noch in Word geöffnet?", exc);
        }
        return Info(key, path);
    }

    public byte[] DownloadDocument(string key)
    {
        var path = Resolve(key);
        if (!File.Exists(path)) throw new NotFoundError("Das gespeicherte Dokument wurde nicht gefunden – bitte den Bericht neu erzeugen.");
        try { return File.ReadAllBytes(path); }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            throw new StorageError($"Das Dokument konnte nicht gelesen werden ({exc.Message}).", exc);
        }
    }

    public List<StoredDocument> ListDocuments(string folder = "")
    {
        var f = CleanFolder(folder);
        var dir = f.Length > 0 ? Resolve(f) : _root;
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains(".tmp-"))
            .Select(p => Info(Path.GetRelativePath(_root, p).Replace('\\', '/'), p))
            .OrderBy(d => d.Key, StringComparer.Ordinal).ToList();
    }

    public void DeleteDocument(string key)
    {
        var path = Resolve(key);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            throw new StorageError($"Das Dokument konnte nicht gelöscht werden ({exc.Message}).", exc);
        }
    }

    private static StoredDocument Info(string key, string path)
    {
        var fi = new FileInfo(path);
        return new StoredDocument(key, fi.Name, fi.Exists ? fi.Length : 0, fi.Exists ? fi.LastWriteTime : DateTime.MinValue);
    }
}
