using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Berichtsheft.Core.Data;
using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Generation;
using Microsoft.EntityFrameworkCore;

namespace Berichtsheft.Core.Services;

/// <summary>Themen (work areas) and Fächer/Lernfelder (school subjects) with their Textbausteine.</summary>
public sealed class CatalogService
{
    public const string ExportFormat = "berichtsheft-snippets";
    public const int ExportVersion = 3;
    private const int MaxImportBytes = 10 * 1024 * 1024;
    private readonly BerichtsheftDbContext _db;

    public CatalogService(BerichtsheftDbContext db) => _db = db;

    // ------------------------------------------------------------ departments
    public List<DepartmentOut> ListDepartments(bool onlyActive = false, string? kind = null)
    {
        var q = _db.Departments.AsNoTracking().AsQueryable();
        if (onlyActive) q = q.Where(d => d.Active);
        if (kind != null) q = q.Where(d => d.Kind == kind);
        var counts = _db.Snippets.AsNoTracking().GroupBy(s => s.DepartmentId)
            .Select(g => new { g.Key, All = g.Count(), Active = g.Count(s => s.Active) }).ToDictionary(x => x.Key);
        return q.AsEnumerable().Select(d => new DepartmentOut
            {
                Id = d.Id, Name = d.Name, Kind = d.Kind, Code = d.Code, Description = d.Description, Active = d.Active, SortOrder = d.SortOrder,
                SnippetCount = counts.TryGetValue(d.Id, out var c) ? c.All : 0, ActiveSnippetCount = counts.TryGetValue(d.Id, out var c2) ? c2.Active : 0,
            })
            .OrderBy(d => d.Kind == "school" ? 1 : 0).ThenBy(d => d.SortOrder)
            .ThenBy(d => d.Kind == "school" ? Curriculum.SortKey(d) : (0, 0, ""))
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public List<int> ActiveIds(string kind) => ListDepartments(onlyActive: true, kind: kind).Select(d => d.Id).ToList();

    private DepartmentEntity GetDepartment(int id) => _db.Departments.Find(id) ?? throw new NotFoundError("Das Thema bzw. Fach wurde nicht gefunden.");

    private DepartmentOut OutOf(int id) => ListDepartments().First(d => d.Id == id);

    private void EnsureUniqueName(string name, int? exceptId)
    {
        var lower = name.ToLower();
        if (_db.Departments.Any(d => d.Name.ToLower() == lower && d.Id != exceptId))
            throw new InvalidInputError($"Es gibt schon ein Thema oder Fach mit dem Namen „{name}“.", "name");
    }

    public DepartmentOut CreateDepartment(DepartmentIn data)
    {
        data.Validate();
        EnsureUniqueName(data.Name, null);
        var e = new DepartmentEntity
        {
            Name = data.Name, Kind = data.Kind, Code = data.Code.Trim(), Description = data.Description.Trim(), Active = data.Active, SortOrder = data.SortOrder,
        };
        _db.Departments.Add(e);
        _db.SaveChanges();
        return OutOf(e.Id);
    }

    public DepartmentOut UpdateDepartment(int id, DepartmentIn data)
    {
        data.Validate();
        var e = GetDepartment(id);
        EnsureUniqueName(data.Name, id);
        e.Name = data.Name; e.Kind = data.Kind; e.Code = data.Code.Trim(); e.Description = data.Description.Trim();
        e.Active = data.Active; e.SortOrder = data.SortOrder;
        _db.SaveChanges();
        return OutOf(id);
    }

    /// <summary>Deletes a Thema/Fach with its snippets and removes it from Abteilungen and plan periods
    /// (old reports keep their copied lines).</summary>
    public void DeleteDepartment(int id)
    {
        var e = GetDepartment(id);
        foreach (var u in _db.Units.ToList().Where(u => u.TopicIds.Contains(id))) u.TopicIds = u.TopicIds.Where(x => x != id).ToList();
        foreach (var p in _db.PlanPeriods.ToList().Where(p => p.DepartmentIds.Contains(id))) p.DepartmentIds = p.DepartmentIds.Where(x => x != id).ToList();
        _db.Departments.Remove(e);
        _db.SaveChanges();
    }

    // ------------------------------------------------------------ snippets
    public List<SnippetOut> ListSnippets(int? departmentId = null, string? search = null, bool onlyActive = false)
    {
        var q = _db.Snippets.AsNoTracking().Include(s => s.Department).AsQueryable();
        if (departmentId != null) q = q.Where(s => s.DepartmentId == departmentId);
        if (onlyActive) q = q.Where(s => s.Active);
        var list = q.AsEnumerable();
        var term = (search ?? "").Trim();
        if (term.Length > 0)
            list = list.Where(s => s.Text.Contains(term, StringComparison.OrdinalIgnoreCase)
                                   || s.Category.Contains(term, StringComparison.OrdinalIgnoreCase)
                                   || s.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase)));
        return list.OrderBy(s => s.Department!.Kind == "school" ? 1 : 0).ThenBy(s => s.Department!.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id)
            .Select(ToOut).ToList();
    }

    private static SnippetOut ToOut(SnippetEntity s) => new()
    {
        Id = s.Id, DepartmentId = s.DepartmentId, Text = s.Text, Category = s.Category, Difficulty = s.Difficulty, Tags = s.Tags.ToList(),
        Active = s.Active, DepartmentName = s.Department?.Name ?? "", DepartmentKind = s.Department?.Kind ?? "topic", DepartmentCode = s.Department?.Code ?? "",
    };

    private SnippetOut SnippetOutOf(int id) => ToOut(_db.Snippets.Include(s => s.Department).First(s => s.Id == id));

    public SnippetOut CreateSnippet(SnippetIn data)
    {
        data.Validate();
        GetDepartment(data.DepartmentId);
        var e = new SnippetEntity
        {
            DepartmentId = data.DepartmentId, Text = data.Text, Category = data.Category.Trim(), Difficulty = data.Difficulty.Trim(), Tags = data.Tags, Active = data.Active,
        };
        _db.Snippets.Add(e);
        _db.SaveChanges();
        return SnippetOutOf(e.Id);
    }

    public SnippetOut UpdateSnippet(int id, SnippetPatch patch)
    {
        patch.Validate();
        var e = _db.Snippets.Find(id) ?? throw new NotFoundError("Der Textbaustein wurde nicht gefunden.");
        if (patch.DepartmentId is { } dep) { GetDepartment(dep); e.DepartmentId = dep; }
        if (patch.Text != null)
        {
            var t = Kinds.Ws(patch.Text);
            if (t.Length == 0) throw Check.Fail("text", "darf nicht leer sein.");
            e.Text = t;
        }
        if (patch.Category != null) e.Category = patch.Category.Trim();
        if (patch.Difficulty != null) e.Difficulty = patch.Difficulty.Trim();
        if (patch.Tags != null) e.Tags = Kinds.CleanTags(patch.Tags);
        if (patch.Active is { } a) e.Active = a;
        _db.SaveChanges();
        return SnippetOutOf(id);
    }

    public void DeleteSnippet(int id)
    {
        var e = _db.Snippets.Find(id) ?? throw new NotFoundError("Der Textbaustein wurde nicht gefunden.");
        _db.Snippets.Remove(e);
        _db.SaveChanges();
    }

    // ------------------------------------------------------------ generation pool
    /// <summary>Active snippets of the given departments. School subjects with a Kürzel produce lines like "LF3: Topic".
    /// <paramref name="strict"/>: unknown or inactive ids are an error (otherwise they are skipped).</summary>
    public (Dictionary<int, List<SnippetCandidate>> Pool, Dictionary<int, string> Names) BuildPool(IEnumerable<int> departmentIds, bool strict = true)
    {
        var ids = departmentIds.Distinct().ToList();
        var deps = _db.Departments.AsNoTracking().Where(d => ids.Contains(d.Id)).ToDictionary(d => d.Id);
        if (strict)
        {
            if (ids.Any(i => !deps.ContainsKey(i))) throw new InvalidInputError("Mindestens ein gewähltes Thema existiert nicht (mehr).");
            if (deps.Values.FirstOrDefault(d => !d.Active) is { } off) throw new InvalidInputError($"Das Thema „{off.Name}“ ist deaktiviert.");
        }
        var use = ids.Where(i => deps.TryGetValue(i, out var d) && (d.Active || strict)).ToList();
        var snippets = _db.Snippets.AsNoTracking().Where(s => use.Contains(s.DepartmentId) && s.Active).OrderBy(s => s.Id).ToList();
        var pool = new Dictionary<int, List<SnippetCandidate>>();
        foreach (var id in use)
        {
            var d = deps[id];
            var prefix = d.Kind == "school" && d.Code.Trim().Length > 0 ? d.Code.Trim() + ": " : "";
            pool[id] = snippets.Where(s => s.DepartmentId == id).Select(s => new SnippetCandidate(s.Id, id, prefix + s.Text)).ToList();
        }
        return (pool, use.ToDictionary(i => i, i => deps[i].Name));
    }

    // ------------------------------------------------------------ import / export
    public ImportResult ImportFile(byte[] data, string filename, bool replace = false)
    {
        if (data.Length > MaxImportBytes) throw new InvalidInputError("Die Datei ist zu groß (höchstens 10 MB).");
        var text = Encoding.UTF8.GetString(data).TrimStart('﻿');
        var isCsv = filename.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || !text.TrimStart().StartsWith('{');
        var departments = isCsv ? ParseCsv(text) : ParseJson(text, out _);
        var importUnits = isCsv ? new List<ImportUnit>() : ParseUnits(text);

        using var tx = _db.Database.BeginTransaction();
        if (replace)
        {
            _db.Snippets.RemoveRange(_db.Snippets);
            _db.Departments.RemoveRange(_db.Departments);
            foreach (var u in _db.Units) u.TopicIds = new();
            foreach (var p in _db.PlanPeriods) p.DepartmentIds = new();
            _db.SaveChanges();
        }
        var result = Import(departments, importUnits);
        tx.Commit();
        return result;
    }

    internal sealed class ImportDepartment
    {
        public string Name = "", Kind = "topic", Code = "", Description = "";
        public bool Active = true;
        public List<SnippetIn> Snippets = new();
    }

    internal sealed class ImportUnit
    {
        public string Name = "", Description = "";
        public bool IsSchool;
        public List<string> Topics = new();
    }

    internal ImportResult Import(List<ImportDepartment> departments, List<ImportUnit> units)
    {
        int depCreated = 0, created = 0, skipped = 0;
        var existing = _db.Departments.ToList();
        foreach (var d in departments)
        {
            var name = Kinds.Ws(d.Name);
            if (name.Length == 0) continue;
            var e = existing.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (e == null)
            {
                var din = new DepartmentIn { Name = name, Kind = Kinds.DepartmentKinds.Contains(d.Kind) ? d.Kind : "topic", Code = d.Code.Trim(), Description = d.Description.Trim(), Active = d.Active };
                din.Validate();
                e = new DepartmentEntity { Name = din.Name, Kind = din.Kind, Code = din.Code, Description = din.Description, Active = din.Active };
                _db.Departments.Add(e);
                _db.SaveChanges();
                existing.Add(e);
                depCreated++;
            }
            var texts = _db.Snippets.Where(s => s.DepartmentId == e.Id).Select(s => s.Text).AsEnumerable()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var s in d.Snippets)
            {
                try { s.Validate(); }
                catch (InvalidInputError) { skipped++; continue; }
                if (!texts.Add(s.Text)) { skipped++; continue; }
                _db.Snippets.Add(new SnippetEntity { DepartmentId = e.Id, Text = s.Text, Category = s.Category, Difficulty = s.Difficulty, Tags = s.Tags, Active = s.Active });
                created++;
            }
            _db.SaveChanges();
        }
        foreach (var u in units)
        {
            var name = Kinds.Ws(u.Name);
            if (name.Length == 0 || _db.Units.AsEnumerable().Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            var topicIds = u.Topics.Select(t => existing.FirstOrDefault(x => x.Name.Equals(t, StringComparison.OrdinalIgnoreCase) && x.Kind == "topic")?.Id)
                .Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
            _db.Units.Add(new UnitEntity { Name = name, Description = u.Description, TopicIds = topicIds, IsSchool = u.IsSchool, Active = true });
            _db.SaveChanges();
        }
        return new ImportResult(depCreated, created, skipped);
    }

    private static JsonNode ParseRoot(string text)
    {
        try { return JsonNode.Parse(text) ?? throw new InvalidInputError("Die JSON-Datei ist leer."); }
        catch (JsonException exc) { throw new InvalidInputError($"Die JSON-Datei ist fehlerhaft ({exc.Message})."); }
    }

    private static string Str(JsonNode? n, string key, string dflt = "")
    {
        try { return n?[key]?.GetValue<string>() ?? dflt; } catch (InvalidOperationException) { return n?[key]?.ToString() ?? dflt; }
    }

    private static bool Bool(JsonNode? n, string key, bool dflt)
    {
        try { return n?[key]?.GetValue<bool>() ?? dflt; } catch (InvalidOperationException) { return dflt; }
    }

    internal static List<ImportDepartment> ParseJson(string text, out JsonNode root)
    {
        root = ParseRoot(text);
        if (root is not JsonObject obj || obj["departments"] is not JsonArray arr)
            throw new InvalidInputError("Die JSON-Datei hat nicht das erwartete Format (Liste „departments“ fehlt).");
        var list = new List<ImportDepartment>();
        foreach (var d in arr.OfType<JsonObject>())
        {
            var dep = new ImportDepartment
            {
                Name = Str(d, "name"), Kind = Str(d, "kind", "topic"), Code = Str(d, "code"), Description = Str(d, "description"), Active = Bool(d, "active", true),
            };
            if (d["snippets"] is JsonArray snips)
                foreach (var s in snips)
                {
                    if (s is JsonValue v && v.TryGetValue<string>(out var plain)) { dep.Snippets.Add(new SnippetIn { Text = plain }); continue; }
                    if (s is not JsonObject so) continue;
                    var tags = so["tags"] is JsonArray ta ? ta.Select(t => t?.ToString() ?? "").ToList() : new List<string>();
                    dep.Snippets.Add(new SnippetIn
                    {
                        Text = Str(so, "text"), Category = Str(so, "category"), Difficulty = Str(so, "difficulty"), Tags = tags, Active = Bool(so, "active", true),
                    });
                }
            list.Add(dep);
        }
        return list;
    }

    private static List<ImportUnit> ParseUnits(string text)
    {
        var root = ParseRoot(text);
        if (root["units"] is not JsonArray arr) return new();
        return arr.OfType<JsonObject>().Select(u => new ImportUnit
        {
            Name = Str(u, "name"), Description = Str(u, "description"), IsSchool = Bool(u, "is_school", false),
            Topics = u["topics"] is JsonArray t ? t.Select(x => x?.ToString() ?? "").ToList() : new(),
        }).ToList();
    }

    /// <summary>CSV with a header row: thema/department; text; kategorie; schwierigkeit; tags (separated by |); aktiv; art (topic|school); kuerzel.</summary>
    private static List<ImportDepartment> ParseCsv(string text)
    {
        var rows = Csv.Parse(text);
        if (rows.Count == 0) throw new InvalidInputError("Die CSV-Datei ist leer.");
        var head = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => head.FindIndex(names.Contains);
        int cDep = Col("thema", "department", "fach", "abteilung"), cText = Col("text", "textbaustein"), cCat = Col("kategorie", "category"),
            cDiff = Col("schwierigkeit", "difficulty"), cTags = Col("tags"), cAct = Col("aktiv", "active"), cKind = Col("art", "kind"), cCode = Col("kuerzel", "kürzel", "code");
        if (cDep < 0 || cText < 0) throw new InvalidInputError("Die CSV-Datei braucht die Spalten „thema“ und „text“.");
        var deps = new Dictionary<string, ImportDepartment>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows.Skip(1))
        {
            string At(int i) => i >= 0 && i < r.Count ? r[i].Trim() : "";
            var name = At(cDep);
            if (name.Length == 0) continue;
            if (!deps.TryGetValue(name, out var d))
            {
                var kind = At(cKind).ToLowerInvariant();
                d = deps[name] = new ImportDepartment { Name = name, Kind = kind is "school" or "fach" or "schule" ? "school" : "topic", Code = At(cCode) };
            }
            var act = At(cAct).ToLowerInvariant();
            d.Snippets.Add(new SnippetIn
            {
                Text = At(cText), Category = At(cCat), Difficulty = At(cDiff),
                Tags = At(cTags).Split('|', StringSplitOptions.RemoveEmptyEntries).ToList(),
                Active = act is not ("0" or "nein" or "false" or "no"),
            });
        }
        return deps.Values.ToList();
    }

    public byte[] ExportJson()
    {
        var deps = _db.Departments.AsNoTracking().Include(d => d.Snippets).AsEnumerable().OrderBy(d => d.Kind == "school" ? 1 : 0).ThenBy(d => d.SortOrder).ThenBy(d => d.Id).ToList();
        var names = deps.ToDictionary(d => d.Id, d => d.Name);
        var doc = new
        {
            format = ExportFormat, version = ExportVersion,
            departments = deps.Select(d => new
            {
                name = d.Name, description = d.Description, active = d.Active, kind = d.Kind, code = d.Code,
                snippets = d.Snippets.OrderBy(s => s.Id).Select(s => new { text = s.Text, category = s.Category, difficulty = s.Difficulty, tags = s.Tags, active = s.Active }),
            }),
            units = _db.Units.AsNoTracking().AsEnumerable().OrderBy(u => u.SortOrder).ThenBy(u => u.Id).Select(u => new
            {
                name = u.Name, description = u.Description, is_school = u.IsSchool, topics = u.TopicIds.Where(names.ContainsKey).Select(i => names[i]),
            }),
        };
        return JsonSerializer.SerializeToUtf8Bytes(doc, new JsonSerializerOptions(Json.Options) { WriteIndented = true });
    }

    public byte[] ExportCsv()
    {
        var sb = new StringBuilder();
        sb.Append("thema;text;kategorie;schwierigkeit;tags;aktiv;art;kuerzel\r\n");
        foreach (var s in _db.Snippets.AsNoTracking().Include(x => x.Department).AsEnumerable().OrderBy(x => x.Department!.Name).ThenBy(x => x.Id))
            sb.Append(string.Join(';', new[]
            {
                s.Department!.Name, s.Text, s.Category, s.Difficulty, string.Join('|', s.Tags), s.Active ? "1" : "0", s.Department.Kind, s.Department.Code,
            }.Select(Csv.Quote))).Append("\r\n");
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(); // BOM: Excel reads umlauts correctly
    }
}

/// <summary>Minimal CSV reader/writer (";" or "," separated, quotes, line breaks in quoted fields).</summary>
internal static class Csv
{
    public static string Quote(string s) => s.IndexOfAny(new[] { ';', '"', '\n', '\r', ',' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static List<List<string>> Parse(string text)
    {
        var firstLine = text.Split('\n')[0];
        var sep = firstLine.Count(c => c == ';') >= firstLine.Count(c => c == ',') ? ';' : ',';
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cur = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cur.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cur.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == sep) { row.Add(cur.ToString()); cur.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cur.ToString()); cur.Clear();
                if (row.Any(x => x.Length > 0)) rows.Add(row);
                row = new List<string>();
            }
            else cur.Append(c);
        }
        row.Add(cur.ToString());
        if (row.Any(x => x.Length > 0)) rows.Add(row);
        return rows;
    }
}
