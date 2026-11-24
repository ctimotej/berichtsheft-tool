using System.Text.RegularExpressions;
using Berichtsheft.Core.Data;
using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Templating;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Berichtsheft.Core.Services;

/// <summary>The Word templates (stored below data/templates under a UUID name). Exactly one is active.</summary>
public sealed class TemplateService
{
    private static readonly HashSet<string> KnownFixed = new()
    {
        "NAME", "NR", "KW", "YEAR", "START_DATE", "END_DATE", "PERIOD", "DATE", "DEPARTMENT", "TRAINING_YEAR", "COMPANY", "PROFESSION",
        "REMARKS", "TOTAL_HOURS", "ENTRIES", "SIGNATURE", "SIGNATURE_DATE", "SIGNER_NAME", "TRAINER_SIGNATURE", "TRAINER_SIGNATURE_DATE", "TRAINER_NAME",
    };
    private static readonly Regex KnownDay = new(@"^(MO|DI|MI|DO|FR|SA|SO)_(\d+(_H)?|DATE|HOURS|TYPE)$", RegexOptions.Compiled);

    private readonly BerichtsheftDbContext _db;
    private readonly BerichtsheftContext _ctx;
    private readonly ILogger _log;

    public TemplateService(BerichtsheftDbContext db, BerichtsheftContext ctx)
    {
        _db = db; _ctx = ctx;
        _log = ctx.Log<TemplateService>();
    }

    public static bool IsKnown(string placeholder) => KnownFixed.Contains(placeholder) || KnownDay.IsMatch(placeholder);

    public static TemplateOut ToOut(TemplateEntity t)
    {
        var info = DocxTemplateEngine.InfoFrom(t.Placeholders);
        return new TemplateOut
        {
            Id = t.Id, Name = t.Name, Description = t.Description, OriginalFilename = t.OriginalFilename, Placeholders = t.Placeholders.ToList(),
            IsActive = t.IsActive, CreatedAt = t.CreatedAt, LineChars = t.LineChars > 0 ? t.LineChars : RenderContext.DefaultLineChars,
            DayCapacity = info.DayCapacity, UnknownPlaceholders = t.Placeholders.Where(p => !IsKnown(p)).ToList(),
        };
    }

    public List<TemplateOut> List() =>
        _db.Templates.AsNoTracking().OrderByDescending(t => t.IsActive).ThenBy(t => t.Name).AsEnumerable().Select(ToOut).ToList();

    public TemplateEntity Get(int id) => _db.Templates.Find(id) ?? throw new NotFoundError("Die Vorlage wurde nicht gefunden.");

    public TemplateEntity GetActive() =>
        _db.Templates.FirstOrDefault(t => t.IsActive) ?? throw new NotFoundError("Es ist keine Vorlage aktiv. Bitte unter Extras > Vorlagen eine Word-Vorlage hinzufügen.");

    public string PathOf(TemplateEntity t)
    {
        var path = Path.Combine(_ctx.Settings.TemplatesDir, Path.GetFileName(t.StoredFilename));
        if (!File.Exists(path)) throw new TemplateError($"Die Datei der Vorlage „{t.Name}“ fehlt im Ordner data\\templates. Bitte die Vorlage neu hinzufügen.");
        return path;
    }

    /// <summary>Stores a .docx (or .doc, converted with Word/LibreOffice) and detects its placeholders.</summary>
    public TemplateEntity Upload(byte[] data, string filename, bool activate = true, string? name = null)
    {
        if (data.Length == 0) throw new InvalidInputError("Die Datei ist leer.");
        if (data.Length > _ctx.Settings.MaxUploadBytes) throw new InvalidInputError($"Die Datei ist zu groß (höchstens {_ctx.Settings.MaxUploadBytes / 1024 / 1024} MB).");
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        if (ext == ".doc") data = _ctx.Converter.DocToDocx(data);
        else if (ext != ".docx") throw new InvalidInputError("Bitte eine Word-Datei (.docx) wählen.");

        Directory.CreateDirectory(_ctx.Settings.TemplatesDir);
        var stored = Guid.NewGuid().ToString("N") + ".docx";
        var path = Path.Combine(_ctx.Settings.TemplatesDir, stored);
        File.WriteAllBytes(path, data);
        TemplateInfo info;
        try { info = DocxTemplateEngine.Inspect(path); }
        catch { File.Delete(path); throw; }

        var t = new TemplateEntity
        {
            Name = (name ?? Path.GetFileNameWithoutExtension(filename)).Trim() is { Length: > 0 } n ? (n.Length > 120 ? n[..120] : n) : "Vorlage",
            OriginalFilename = Path.GetFileName(filename), StoredFilename = stored, Placeholders = info.Placeholders, CreatedAt = DateTime.Now,
        };
        _db.Templates.Add(t);
        _db.SaveChanges();
        if (activate || !_db.Templates.Any(x => x.IsActive)) Activate(t.Id);
        _log.LogInformation("Template {Id} uploaded ({Count} placeholders)", t.Id, info.Placeholders.Count);
        return t;
    }

    public TemplateEntity Activate(int id)
    {
        var t = Get(id);
        foreach (var other in _db.Templates.Where(x => x.IsActive && x.Id != id)) other.IsActive = false;
        t.IsActive = true;
        _db.SaveChanges();
        return t;
    }

    public TemplateEntity Patch(int id, string? name, int? lineChars)
    {
        new TemplatePatch { Name = name, LineChars = lineChars }.Validate();
        var t = Get(id);
        if (name != null) t.Name = name.Trim();
        if (lineChars is { } lc) t.LineChars = lc;
        _db.SaveChanges();
        return t;
    }

    public (byte[] Data, string Name) ReadBytes(int id)
    {
        var t = Get(id);
        var name = Path.GetFileNameWithoutExtension(t.OriginalFilename);
        return (File.ReadAllBytes(PathOf(t)), (name.Length > 0 ? name : t.Name) + ".docx");
    }

    /// <summary>Deletes a template; reports that used it render with the active one from now on.</summary>
    public void Delete(int id)
    {
        var t = Get(id);
        foreach (var r in _db.Reports.Where(r => r.TemplateId == id)) r.TemplateId = null;
        var wasActive = t.IsActive;
        _db.Templates.Remove(t);
        _db.SaveChanges();
        try { File.Delete(Path.Combine(_ctx.Settings.TemplatesDir, Path.GetFileName(t.StoredFilename))); }
        catch (IOException exc) { _log.LogWarning(exc, "Template file of {Id} could not be deleted", id); }
        if (wasActive && _db.Templates.OrderByDescending(x => x.CreatedAt).FirstOrDefault() is { } next) Activate(next.Id);
    }
}
