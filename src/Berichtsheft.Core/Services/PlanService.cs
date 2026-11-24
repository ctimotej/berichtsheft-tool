using Berichtsheft.Core.Data;
using Berichtsheft.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Berichtsheft.Core.Services;

/// <summary>Planning: rotation (Abteilung per period), Berufsschule blocks and vacation.</summary>
public sealed class PlanService
{
    private readonly BerichtsheftDbContext _db;
    private static readonly string[] KindNames = { "Einsatz", "Berufsschule", "Urlaub" };

    public PlanService(BerichtsheftDbContext db) => _db = db;

    public List<PlanPeriodOut> List()
    {
        var deps = _db.Departments.AsNoTracking().ToList();
        var labels = deps.ToDictionary(d => d.Id, d => d.Kind == "school" && d.Code.Length > 0 ? d.Code : d.Name);
        var units = _db.Units.AsNoTracking().ToDictionary(u => u.Id, u => u.Name);
        return _db.PlanPeriods.AsNoTracking().AsEnumerable().OrderBy(p => p.StartDate).ThenBy(p => p.Kind).Select(p => new PlanPeriodOut
        {
            Id = p.Id, Kind = p.Kind, StartDate = p.StartDate, EndDate = p.EndDate, UnitId = p.UnitId, DepartmentIds = p.DepartmentIds.ToList(), Note = p.Note,
            UnitName = p.UnitId is { } u && units.TryGetValue(u, out var un) ? un : "",
            Labels = p.DepartmentIds.Where(labels.ContainsKey).Select(i => labels[i]).ToList(),
        }).ToList();
    }

    private void Check(PlanPeriodIn data, int? exceptId)
    {
        data.Validate();
        if (data.UnitId is { } uid && !_db.Units.Any(u => u.Id == uid)) throw new InvalidInputError("Die gewählte Abteilung existiert nicht (mehr).");
        var kind = data.Kind == "school" ? "school" : "topic";
        if (data.Kind != "vacation")
        {
            var valid = _db.Departments.Where(d => d.Kind == kind).Select(d => d.Id).ToHashSet();
            if (data.DepartmentIds.Any(i => !valid.Contains(i))) throw new InvalidInputError("Mindestens ein gewähltes Thema bzw. Fach existiert nicht (mehr).");
        }
        var clash = _db.PlanPeriods.AsNoTracking()
            .Where(p => p.Kind == data.Kind && p.Id != exceptId && p.StartDate <= data.EndDate && p.EndDate >= data.StartDate)
            .AsEnumerable().FirstOrDefault();
        if (clash != null)
            throw new InvalidInputError($"Der Zeitraum überschneidet sich mit „{KindNames[Array.IndexOf(Kinds.PlanKinds, data.Kind)]}“ vom {clash.StartDate:dd.MM.yyyy} bis {clash.EndDate:dd.MM.yyyy}.");
    }

    public PlanPeriodOut Create(PlanPeriodIn data)
    {
        Check(data, null);
        var p = new PlanPeriodEntity { Kind = data.Kind, StartDate = data.StartDate, EndDate = data.EndDate, UnitId = data.Kind == "rotation" ? data.UnitId : null, DepartmentIds = data.Kind == "vacation" ? new() : data.DepartmentIds.Distinct().ToList(), Note = data.Note.Trim() };
        _db.PlanPeriods.Add(p);
        _db.SaveChanges();
        return List().First(x => x.Id == p.Id);
    }

    public PlanPeriodOut Update(int id, PlanPeriodIn data)
    {
        var p = _db.PlanPeriods.Find(id) ?? throw new NotFoundError("Der Zeitraum wurde nicht gefunden.");
        Check(data, id);
        p.Kind = data.Kind; p.StartDate = data.StartDate; p.EndDate = data.EndDate; p.Note = data.Note.Trim();
        p.UnitId = data.Kind == "rotation" ? data.UnitId : null;
        p.DepartmentIds = data.Kind == "vacation" ? new() : data.DepartmentIds.Distinct().ToList();
        _db.SaveChanges();
        return List().First(x => x.Id == id);
    }

    public void Delete(int id)
    {
        var p = _db.PlanPeriods.Find(id) ?? throw new NotFoundError("Der Zeitraum wurde nicht gefunden.");
        _db.PlanPeriods.Remove(p);
        _db.SaveChanges();
    }

    private List<PlanPeriodEntity> Overlapping(DateOnly start, DateOnly end) =>
        _db.PlanPeriods.AsNoTracking().Where(p => p.StartDate <= end && p.EndDate >= start).AsEnumerable().OrderBy(p => p.StartDate).ToList();

    /// <summary>Day types from the plan (vacation &gt; school block &gt; rotation to the Abteilung "Berufsschule") and the
    /// Lernfelder of the school blocks in the range.</summary>
    public (Dictionary<DateOnly, string> Types, List<int> Lernfelder) DayPlan(DateOnly start, DateOnly end)
    {
        var periods = Overlapping(start, end);
        var schoolUnits = _db.Units.AsNoTracking().Where(u => u.IsSchool).Select(u => u.Id).ToHashSet();
        var types = new Dictionary<DateOnly, string>();
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            bool Covers(PlanPeriodEntity p) => p.StartDate <= d && d <= p.EndDate;
            if (periods.Any(p => p.Kind == "vacation" && Covers(p))) types[d] = "vacation";
            else if (periods.Any(p => p.Kind == "school" && Covers(p))) types[d] = "school";
            else if (periods.Any(p => p.Kind == "rotation" && p.UnitId is { } u && schoolUnits.Contains(u) && Covers(p))) types[d] = "school";
        }
        var lernfelder = periods.Where(p => p.Kind == "school").SelectMany(p => p.DepartmentIds).Distinct().ToList();
        return (types, lernfelder);
    }

    /// <summary>The rotation period covering most of the range: its Abteilung and Themen (empty Themen = the Abteilung's defaults).</summary>
    public (int? UnitId, List<int> Topics) Rotation(DateOnly start, DateOnly end)
    {
        var best = Overlapping(start, end).Where(p => p.Kind == "rotation")
            .OrderByDescending(p => Math.Min(end.DayNumber, p.EndDate.DayNumber) - Math.Max(start.DayNumber, p.StartDate.DayNumber))
            .ThenBy(p => p.StartDate).FirstOrDefault();
        if (best == null) return (null, new());
        var topics = best.DepartmentIds.Count > 0 ? best.DepartmentIds.ToList() : new UnitService(_db).DefaultTopics(best.UnitId);
        var active = _db.Departments.AsNoTracking().Where(d => d.Active).Select(d => d.Id).ToHashSet();
        return (best.UnitId, topics.Where(active.Contains).ToList());
    }
}
