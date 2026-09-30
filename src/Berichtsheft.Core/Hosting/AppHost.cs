using System.Reflection;
using Berichtsheft.Core.Config;
using Berichtsheft.Core.Data;
using Berichtsheft.Core.Generation;
using Berichtsheft.Core.Services;
using Berichtsheft.Core.Signature;
using Berichtsheft.Core.Templating;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Berichtsheft.Core.Hosting;

/// <summary>Composition root for a desktop process (and for tests): configuration, shared components,
/// database upgrade and seeding. <see cref="Open"/> hands out a short-lived <see cref="Session"/> (one DB
/// context plus the services on top of it) - one per user action, disposed afterwards.</summary>
public sealed class AppHost : IDisposable
{
    public AppConfig Config { get; }
    public BerichtsheftContext Context { get; }
    private readonly ILoggerFactory _loggers;
    private readonly DbContextOptions<BerichtsheftDbContext> _dbOptions;

    public AppHost(AppConfig config)
    {
        Config = config;
        Config.EnsureDirs();
        _loggers = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(ParseLevel(config.LogLevel));
            b.AddFilter("Microsoft", LogLevel.Warning);
            b.AddProvider(new FileLoggerProvider(config.LogsDir));
        });
        Context = new BerichtsheftContext
        {
            Settings = config,
            Storage = BerichtsheftContext.CreateStorage(config),
            Generator = GenerationProviderRegistry.GetGenerationProvider(config.GenerationProvider),
            Converter = new OfficeConverter(config.LibreOfficePath),
            SignatureProvider = SignatureProviderRegistry.GetSignatureProvider(config.SignatureProvider),
            LoggerFactory = _loggers,
            ReadSeed = ReadSeed,
        };
        _dbOptions = BerichtsheftDbContextOptionsFactory.Create(config.DbPath);

        using var db = new BerichtsheftDbContext(_dbOptions);
        db.Database.Migrate();
        try { Seeder.SeedIfNeeded(db, Context); }
        catch (Exception exc) { _loggers.CreateLogger<AppHost>().LogError(exc, "Seeding failed - continuing without example data"); }
    }

    public Session Open() => new(new BerichtsheftDbContext(_dbOptions), Context);

    public void Dispose() => _loggers.Dispose();

    private static LogLevel ParseLevel(string s) => s.ToUpperInvariant() switch
    {
        "DEBUG" => LogLevel.Debug, "WARNING" or "WARN" => LogLevel.Warning, "ERROR" => LogLevel.Error,
        "CRITICAL" => LogLevel.Critical, "TRACE" => LogLevel.Trace, _ => LogLevel.Information,
    };

    // seed data is embedded in this assembly (logical names "seed/<path>"); BH_SEED_DIR overrides it
    private byte[]? ReadSeed(string name)
    {
        if (!string.IsNullOrEmpty(Config.SeedDir))
        {
            var p = Path.Combine(Config.SeedDir, name);
            if (File.Exists(p)) return File.ReadAllBytes(p);
        }
        var asm = typeof(AppHost).Assembly;
        var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.Replace('\\', '/').Equals("seed/" + name, StringComparison.OrdinalIgnoreCase));
        if (res == null) return null;
        using var s = asm.GetManifestResourceStream(res)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public sealed class Session : IDisposable
    {
        public BerichtsheftDbContext Db { get; }
        public BerichtsheftContext Ctx { get; }
        public CatalogService Catalog { get; }
        public SettingsService Settings { get; }
        public TemplateService Templates { get; }
        public ReportService Reports { get; }
        public PlanService Plan { get; }
        public UnitService Units { get; }
        public ExamService Exam { get; }

        internal Session(BerichtsheftDbContext db, BerichtsheftContext ctx)
        {
            Db = db; Ctx = ctx;
            Catalog = new CatalogService(db);
            Settings = new SettingsService(db);
            Templates = new TemplateService(db, ctx);
            Plan = new PlanService(db);
            Units = new UnitService(db);
            Reports = new ReportService(db, ctx);
            Exam = new ExamService(Reports);
        }

        public void Dispose() => Db.Dispose();
    }
}
