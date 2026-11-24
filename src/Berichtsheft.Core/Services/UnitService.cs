using Berichtsheft.Core.Data;
using Berichtsheft.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Berichtsheft.Core.Services;

/// <summary>Abteilungen (organisational units printed in the report header).</summary>
public sealed class UnitService
{
    public const string SchoolUnitName = "Berufsschule";
    private readonly BerichtsheftDbContext _db;

    public UnitService(BerichtsheftDbContext db) => _db = db;

    public List<UnitOut> List(bool onlyActive = false)
    {
        var names = _db.Departments.AsNoTracking().Where(d => d.Kind == "topic").ToDictionary(d => d.Id, d => d.Name);
        var q = _db.Units.AsNoTracking().AsEnumerable();
        if (onlyActive) q = q.Where(u => u.Active);
        return q.OrderBy(u => u.IsSchool ? 1 : 0).ThenBy(u => u.SortOrder).ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase).Select(u => new UnitOut
        {
            Id = u.Id, Name = u.Name, Description = u.Description, Active = u.Active, SortOrder = u.SortOrder, IsSchool = u.IsSchool,
            TopicIds = u.TopicIds.Where(names.ContainsKey).ToList(),
            TopicNames = u.TopicIds.Where(names.ContainsKey).Select(i => names[i]).ToList(),
        }).ToList();
    }

    public string NameOf(int? unitId) =>
        unitId is { } id && _db.Units.AsNoTracking().FirstOrDefault(u => u.Id == id) is { } u ? u.Name : "";

    public bool IsSchool(int? unitId) =>
        unitId is { } id && _db.Units.AsNoTracking().Any(u => u.Id == id && u.IsSchool);

    /// <summary>The Abteilung "Berufsschule" (null if it was deleted).</summary>
    public int? SchoolUnitId() =>
        _db.Units.AsNoTracking().Where(u => u.IsSchool && u.Active).OrderBy(u => u.Id).Select(u => (int?)u.Id).FirstOrDefault();

    /// <summary>The default Themen of an Abteilung that still exist and are active.</summary>
    public List<int> DefaultTopics(int? unitId)
    {
        if (unitId is not { } id || _db.Units.AsNoTracking().FirstOrDefault(u => u.Id == id) is not { } u) return new();
        var active = _db.Departments.AsNoTracking().Where(d => d.Active && d.Kind == "topic").Select(d => d.Id).ToHashSet();
        return u.TopicIds.Where(active.Contains).ToList();
    }

    private void Check(UnitIn data, int? exceptId)
    {
        data.Validate();
        var lower = data.Name.ToLower();
        if (_db.Units.Any(u => u.Name.ToLower() == lower && u.Id != exceptId))
            throw new InvalidInputError($"Es gibt schon eine Abteilung „{data.Name}“.", "name");
        var topics = _db.Departments.Where(d => d.Kind == "topic").Select(d => d.Id).ToHashSet();
        if (data.TopicIds.Any(i => !topics.Contains(i))) throw new InvalidInputError("Mindestens ein gewähltes Thema existiert nicht (mehr).");
    }

    public UnitOut Create(UnitIn data)
    {
        Check(data, null);
        var u = new UnitEntity { Name = data.Name, Description = data.Description.Trim(), Active = data.Active, SortOrder = data.SortOrder, TopicIds = data.TopicIds.Distinct().ToList(), IsSchool = data.IsSchool };
        _db.Units.Add(u);
        _db.SaveChanges();
        return List().First(x => x.Id == u.Id);
    }

    public UnitOut Update(int id, UnitIn data)
    {
        var u = _db.Units.Find(id) ?? throw new NotFoundError("Die Abteilung wurde nicht gefunden.");
        Check(data, id);
        u.Name = data.Name; u.Description = data.Description.Trim(); u.Active = data.Active; u.SortOrder = data.SortOrder;
        u.TopicIds = data.TopicIds.Distinct().ToList(); u.IsSchool = data.IsSchool;
        _db.SaveChanges();
        return List().First(x => x.Id == id);
    }

    /// <summary>Deletes an Abteilung; plan periods lose the reference, reports keep their header text.</summary>
    public void Delete(int id)
    {
        var u = _db.Units.Find(id) ?? throw new NotFoundError("Die Abteilung wurde nicht gefunden.");
        foreach (var p in _db.PlanPeriods.Where(p => p.UnitId == id)) p.UnitId = null;
        foreach (var r in _db.Reports.Where(r => r.UnitId == id)) r.UnitId = null;
        _db.Units.Remove(u);
        _db.SaveChanges();
    }
}
