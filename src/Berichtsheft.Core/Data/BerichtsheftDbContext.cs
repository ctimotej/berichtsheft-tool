using System.Text.Json;
using Berichtsheft.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Berichtsheft.Core.Data;

public sealed class BerichtsheftDbContext : DbContext
{
    public BerichtsheftDbContext(DbContextOptions<BerichtsheftDbContext> options) : base(options) { }

    public DbSet<SettingEntity> Settings => Set<SettingEntity>();
    public DbSet<DepartmentEntity> Departments => Set<DepartmentEntity>();
    public DbSet<SnippetEntity> Snippets => Set<SnippetEntity>();
    public DbSet<UnitEntity> Units => Set<UnitEntity>();
    public DbSet<TemplateEntity> Templates => Set<TemplateEntity>();
    public DbSet<ReportEntity> Reports => Set<ReportEntity>();
    public DbSet<SignatureEntity> Signatures => Set<SignatureEntity>();
    public DbSet<PlanPeriodEntity> PlanPeriods => Set<PlanPeriodEntity>();

    public override int SaveChanges()
    {
        var now = DateTime.Now;
        foreach (var e in ChangeTracker.Entries())
        {
            if (e.State != EntityState.Modified) continue;
            switch (e.Entity)
            {
                case ReportEntity r: r.UpdatedAt = now; break;
                case DepartmentEntity d: d.UpdatedAt = now; break;
                case SnippetEntity s: s.UpdatedAt = now; break;
            }
        }
        return base.SaveChanges();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SettingEntity>(e => { e.ToTable("settings"); e.HasKey(x => x.Key); });

        b.Entity<DepartmentEntity>(e =>
        {
            e.ToTable("departments");
            e.HasIndex(x => x.Name).IsUnique();
            e.HasMany(x => x.Snippets).WithOne(x => x.Department).HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SnippetEntity>(e =>
        {
            e.ToTable("snippets");
            e.HasIndex(x => x.DepartmentId);
            JsonColumn(e.Property(x => x.Tags));
        });
        b.Entity<UnitEntity>(e =>
        {
            e.ToTable("units");
            e.HasIndex(x => x.Name).IsUnique();
            JsonColumn(e.Property(x => x.TopicIds));
        });
        b.Entity<TemplateEntity>(e =>
        {
            e.ToTable("templates");
            JsonColumn(e.Property(x => x.Placeholders));
        });
        b.Entity<ReportEntity>(e =>
        {
            e.ToTable("reports");
            e.HasIndex(x => x.StartDate);
            JsonColumn(e.Property(x => x.DepartmentIds));
            JsonColumn(e.Property(x => x.GenerationOptions));
            JsonColumn(e.Property(x => x.Content));
            e.HasMany(x => x.Signatures).WithOne(x => x.Report).HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SignatureEntity>(e => e.ToTable("signatures"));
        b.Entity<PlanPeriodEntity>(e =>
        {
            e.ToTable("plan_periods");
            JsonColumn(e.Property(x => x.DepartmentIds));
        });
    }

    /// <summary>Stores a value as JSON text. The comparer works on the JSON form, so changes inside lists
    /// and objects (not only new instances) are detected and saved.</summary>
    private static void JsonColumn<T>(PropertyBuilder<T> p) where T : class, new()
    {
        p.HasConversion(
            v => JsonSerializer.Serialize(v, Json.Options),
            s => Json.Deserialize<T>(s, null) ?? new T(),
            new ValueComparer<T>(
                (a, c) => JsonSerializer.Serialize(a, Json.Options) == JsonSerializer.Serialize(c, Json.Options),
                v => JsonSerializer.Serialize(v, Json.Options).GetHashCode(),
                v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Json.Options), Json.Options)!))
         .IsRequired();
    }
}

public static class BerichtsheftDbContextOptionsFactory
{
    public static DbContextOptions<BerichtsheftDbContext> Create(string dbPath) =>
        new DbContextOptionsBuilder<BerichtsheftDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .UseSnakeCaseNamingConvention()
            .Options;
}

/// <summary>Used by <c>dotnet ef migrations add</c> only.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<BerichtsheftDbContext>
{
    public BerichtsheftDbContext CreateDbContext(string[] args) =>
        new(BerichtsheftDbContextOptionsFactory.Create(Path.Combine(Path.GetTempPath(), "bh-design.db")));
}
