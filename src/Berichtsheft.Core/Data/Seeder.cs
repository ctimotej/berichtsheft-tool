using System.Text;
using Berichtsheft.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Berichtsheft.Core.Data;

/// <summary>Example data on the first start, plus one-time additions for existing databases. Every step runs once
/// (a flag in the settings table), so data the user deleted does not come back.</summary>
public static class Seeder
{
    public static readonly string[] LernfeldFiles =
    {
        "lernfelder/Lernfelder_LF1-LF9_alle_Fachrichtungen.json",
        "lernfelder/Lernfelder_LF10-LF12_Anwendungsentwicklung.json",
        "lernfelder/Lernfelder_LF10-LF12_Systemintegration.json",
        "lernfelder/Lernfelder_LF10-LF12_Daten_und_Prozessanalyse.json",
        "lernfelder/Lernfelder_LF10-LF12_Digitale_Vernetzung.json",
    };

    private const string CatalogFlag = "seed:catalog", LernfelderFlag = "seed:lernfelder_v1", SchoolUnitFlag = "seed:school_unit_v1", TemplateFlag = "seed:template";

    public static void SeedIfNeeded(BerichtsheftDbContext db, BerichtsheftContext ctx)
    {
        var log = ctx.Log<BerichtsheftDbContext>();
        var settings = new SettingsService(db);
        var catalog = new CatalogService(db);

        void Once(string flag, Func<bool> applies, Action run)
        {
            if (settings.GetFlag(flag)) return;
            if (applies())
            {
                try { run(); }
                catch (Exception exc) when (exc is AppError or IOException or DbUpdateException)
                {
                    db.ChangeTracker.Clear();
                    log.LogError(exc, "Seed step {Flag} failed", flag);
                    return; // try again at the next start
                }
            }
            settings.SetFlag(flag, true);
        }

        // 1. example Themen, Textbausteine and Abteilungen – only for an empty catalog
        Once(CatalogFlag, () => !db.Departments.Any(), () =>
        {
            if (ctx.ReadSeed("example_snippets.json") is { } data) catalog.ImportFile(data, "example_snippets.json");
        });

        // 2. Lernfelder LF1–LF12 of all Fachrichtungen – new and existing databases; existing names are kept
        Once(LernfelderFlag, () => true, () =>
        {
            foreach (var f in LernfeldFiles)
            {
                if (ctx.ReadSeed(f) is not { } data) { log.LogWarning("Seed file {File} missing", f); continue; }
                var deps = CatalogService.ParseJson(Encoding.UTF8.GetString(data).TrimStart('﻿'), out _);
                catalog.Import(deps, new());
            }
        });

        // 3. the Abteilung "Berufsschule" (weeks with only school days)
        Once(SchoolUnitFlag, () => !db.Units.Any(u => u.IsSchool), () =>
        {
            var existing = db.Units.AsEnumerable().FirstOrDefault(u => u.Name.Equals(UnitService.SchoolUnitName, StringComparison.OrdinalIgnoreCase));
            if (existing != null) existing.IsSchool = true;
            else db.Units.Add(new UnitEntity
            {
                Name = UnitService.SchoolUnitName, IsSchool = true, Active = true, SortOrder = 1000,
                Description = "Berufsschulwochen: alle Tage sind Schultage mit Themen aus den Lernfeldern des Ausbildungsjahres.",
            });
            db.SaveChanges();
        });

        // 4. the standard template
        Once(TemplateFlag, () => !db.Templates.Any(), () =>
        {
            if (ctx.ReadSeed("templates/Standard_Tagesbericht.docx") is { } data)
                new TemplateService(db, ctx).Upload(data, "Standard_Tagesbericht.docx", activate: true, name: "Standard Tagesbericht");
        });
    }
}
