using Berichtsheft.Core.Config;
using Berichtsheft.Core.Generation;
using Berichtsheft.Core.Signature;
using Berichtsheft.Core.Storage;
using Berichtsheft.Core.Templating;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Berichtsheft.Core.Services;

/// <summary>The components shared by all sessions of a process: configuration and the pluggable providers.</summary>
public sealed class BerichtsheftContext
{
    public AppConfig Settings { get; init; } = new();
    public IDocumentStorageProvider Storage { get; init; } = null!;
    public ISnippetGenerationProvider Generator { get; init; } = new LocalSnippetGenerator();
    public OfficeConverter Converter { get; init; } = new();
    public ISignatureProvider SignatureProvider { get; init; } = new ImageSignatureProvider();
    public ILoggerFactory LoggerFactory { get; init; } = NullLoggerFactory.Instance;
    /// <summary>Reads a seed file ("example_snippets.json", "templates/…") or returns null.</summary>
    public Func<string, byte[]?> ReadSeed { get; init; } = _ => null;

    public ILogger Log<T>() => LoggerFactory.CreateLogger<T>();

    /// <summary>Storage provider from the configuration. "sharepoint" is planned (see ARCHITECTURE.md) and not available yet.</summary>
    public static IDocumentStorageProvider CreateStorage(AppConfig config) => config.StorageProvider.ToLowerInvariant() switch
    {
        "local" or "" => new LocalStorageProvider(config.ReportsDir),
        "sharepoint" => throw new FeatureUnavailableError("Die Ablage in SharePoint ist noch nicht verfügbar. Bitte BH_STORAGE_PROVIDER entfernen."),
        var other => throw new InvalidInputError($"Unbekannter Speicherort „{other}“ (BH_STORAGE_PROVIDER)."),
    };
}

/// <summary>Writes log lines to data/logs/app.log (rotated at 2 MB, three old files kept).</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private readonly string _path;
    private readonly object _lock = new();

    public FileLoggerProvider(string dir)
    {
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "app.log");
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    private void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                {
                    for (var i = 3; i >= 1; i--)
                    {
                        var src = i == 1 ? _path : $"{_path}.{i - 1}";
                        if (File.Exists(src)) File.Move(src, $"{_path}.{i}", true);
                    }
                }
                File.AppendAllText(_path, line);
            }
            catch (IOException) { /* logging must never break the program */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _p;
        private readonly string _cat;
        public FileLogger(FileLoggerProvider p, string cat) { _p = p; _cat = cat; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {logLevel,-11} {_cat.Split('.').Last()}: {formatter(state, exception)}";
            if (exception != null) line += Environment.NewLine + exception;
            _p.Write(line + Environment.NewLine);
        }
    }
}
