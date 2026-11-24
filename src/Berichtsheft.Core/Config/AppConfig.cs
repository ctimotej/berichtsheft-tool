namespace Berichtsheft.Core.Config;

/// <summary>Technical configuration. Everything has a default; <c>BH_*</c> environment variables (or the
/// <c>overrides</c> passed to <see cref="Load"/>, keys without the prefix) can change it. There is no config file.</summary>
public sealed class AppConfig
{
    public string DataDir { get; set; } = "data";
    public string LogLevel { get; set; } = "Information";
    /// <summary>Folder with seed files that replace the embedded ones (development).</summary>
    public string SeedDir { get; set; } = "";
    public string LibreOfficePath { get; set; } = "";
    public string GenerationProvider { get; set; } = "local";
    public string SignatureProvider { get; set; } = "image";
    public string StorageProvider { get; set; } = "local";
    /// <summary>Upper limit for uploaded templates and restored backups.</summary>
    public long MaxUploadBytes { get; set; } = 20 * 1024 * 1024;
    public long MaxBackupBytes { get; set; } = 1024L * 1024 * 1024;

    public string DbPath => Path.Combine(DataDir, "berichtsheft.db");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string ReportsDir => Path.Combine(DataDir, "reports");
    public string TemplatesDir => Path.Combine(DataDir, "templates");

    /// <param name="baseDir">Folder the data folder is placed in (default: next to the program).</param>
    /// <param name="overrides">Values that win over the environment (keys like "DATA_DIR").</param>
    public static AppConfig Load(string? baseDir = null, IDictionary<string, string?>? overrides = null)
    {
        string? Get(string key)
        {
            if (overrides != null && overrides.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) return v;
            var env = Environment.GetEnvironmentVariable("BH_" + key);
            return string.IsNullOrEmpty(env) ? null : env;
        }

        var cfg = new AppConfig();
        var dataDir = Get("DATA_DIR");
        if (dataDir == null)
        {
            var root = baseDir ?? AppContext.BaseDirectory;
            dataDir = Path.Combine(root, "data");
            // next to the program is the documented place; if that folder is not writable (e.g. "Program Files")
            // fall back to the user's local app data instead of failing on the first start
            if (baseDir == null && !CanWrite(root) && !Directory.Exists(dataDir))
                dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Berichtsheft", "data");
        }
        cfg.DataDir = Path.GetFullPath(dataDir);
        cfg.LogLevel = Get("LOG_LEVEL") ?? cfg.LogLevel;
        cfg.SeedDir = Get("SEED_DIR") ?? "";
        cfg.LibreOfficePath = Get("LIBREOFFICE_PATH") ?? "";
        cfg.GenerationProvider = Get("GENERATION_PROVIDER") ?? cfg.GenerationProvider;
        cfg.SignatureProvider = Get("SIGNATURE_PROVIDER") ?? cfg.SignatureProvider;
        cfg.StorageProvider = Get("STORAGE_PROVIDER") ?? cfg.StorageProvider;
        if (long.TryParse(Get("MAX_UPLOAD_BYTES"), out var max) && max > 0) cfg.MaxUploadBytes = max;
        return cfg;
    }

    public void EnsureDirs()
    {
        foreach (var d in new[] { DataDir, LogsDir, ReportsDir, TemplatesDir }) Directory.CreateDirectory(d);
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".bh-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException) { return false; }
    }
}
