using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Hosting;
using Berichtsheft.Core.Services;

namespace Berichtsheft.Desktop;

/// <summary>Pick a week; Abteilung, Themen, day types and the Lernfelder of the school days are suggested from the plan
/// and the Ausbildungsjahr. Creates the report.</summary>
internal sealed class NewReportForm : Form
{
    private readonly AppHost _host;
    private readonly DateTimePicker _day = new() { Format = DateTimePickerFormat.Short };
    private readonly Label _week = new() { AutoSize = true };
    private readonly ComboBox _unit = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckedListBox _topics = new() { CheckOnClick = true, Height = 120, IntegralHeight = false };
    private readonly ComboBox[] _types = Enumerable.Range(0, 5).Select(_ => new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 }).ToArray();
    private readonly Label[] _dayLabels = Enumerable.Range(0, 5).Select(_ => new Label { AutoSize = true, Width = 90, Margin = new Padding(0, 6, 0, 0) }).ToArray();
    private readonly NumericUpDown _count = new() { Minimum = 1, Maximum = 100, Width = 60 };
    private readonly NumericUpDown _countMax = new() { Minimum = 1, Maximum = 100, Width = 60 };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly Label _schoolHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(330, 0) };
    private readonly LernfeldList _lernfelder;
    private List<UnitOut> _units = new();
    private List<DepartmentOut> _topicList = new(), _subjects = new();
    private WeekInfo? _info;
    private bool _loading;
    public int? CreatedId { get; private set; }

    public NewReportForm(AppHost host, DateOnly? day)
    {
        _host = host;
        Ui.Dialog(this, "Neuer Bericht", 520, 760);

        Curriculum.Specialization? spec = null;
        Ui.Try(() =>
        {
            using var s = _host.Open();
            _subjects = s.Catalog.ListDepartments(onlyActive: true, kind: "school");
            spec = Curriculum.SpecializationOf(s.Settings.Get().Profession);
        });
        _lernfelder = new LernfeldList(_subjects, spec, 170);

        _mode.Items.AddRange(new object[] { "pro Woche", "pro Tag" });
        foreach (var c in _types)
        {
            c.Items.AddRange(Ui.DayTypeNames);
            c.SelectedIndexChanged += (_, _) => UpdateSchoolState();
        }

        var days = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        for (var i = 0; i < 5; i++)
        {
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            row.Controls.Add(_dayLabels[i]);
            row.Controls.Add(_types[i]);
            days.Controls.Add(row);
        }
        var countRow = new FlowLayoutPanel { AutoSize = true };
        countRow.Controls.Add(_count);
        countRow.Controls.Add(new Label { Text = "bis", AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
        countRow.Controls.Add(_countMax);
        countRow.Controls.Add(_mode);

        _mode.SelectedIndexChanged += (_, _) => _countMax.Enabled = _mode.SelectedIndex == 1;
        _count.ValueChanged += (_, _) => { if (_countMax.Value < _count.Value) _countMax.Value = _count.Value; };
        var form = Ui.Form(("Woche (beliebiger Tag):", _day), ("", _week), ("Abteilung:", _unit), ("Themen:", _topics), ("Tage:", days),
            ("Einträge (von – bis pro Tag):", countRow), ("Lernfelder (Schultage):", _lernfelder), ("", _schoolHint));
        _topics.Height = 120;

        Controls.Add(form);
        var ok = Ui.OkButton(this, (_, _) => Create(), "Erstellen");
        var bar = Ui.ButtonBar(DockStyle.Bottom, ok, Ui.CancelButton(this));
        Controls.Add(bar);

        Ui.Try(() =>
        {
            using var s = _host.Open();
            var cfg = s.Settings.Get();
            _units = s.Units.List(onlyActive: true);
            _topicList = s.Catalog.ListDepartments(onlyActive: true, kind: "topic");
            _count.Value = Math.Clamp(cfg.DefaultCount, 1, 100);
            _countMax.Value = Math.Clamp(Math.Max(cfg.DefaultCount, cfg.DefaultCountMax), 1, 100);
            _mode.SelectedIndex = cfg.DefaultCountMode == "per_day" ? 1 : 0;
            _unit.Items.Add("(keine)");
            _unit.Items.AddRange(_units.Select(u => (object)u.Name).ToArray());
            foreach (var t in _topicList) _topics.Items.Add(t.Name);
            _day.Value = (day ?? SuggestedDay(s)).ToDateTime(TimeOnly.MinValue);
        });
        _day.ValueChanged += (_, _) => Ui.Try(RefreshWeek);
        _unit.SelectedIndexChanged += (_, _) => { if (!_loading) Ui.Try(UnitChanged); };
        Ui.Try(RefreshWeek);
    }

    /// <summary>The oldest week without a report, otherwise this week.</summary>
    private static DateOnly SuggestedDay(AppHost.Session s)
    {
        var missing = s.Reports.Coverage().Weeks.FirstOrDefault(w => w.Status == "missing");
        return missing?.StartDate ?? DateOnly.FromDateTime(DateTime.Today);
    }

    private UnitOut? SelectedUnit => _units.ElementAtOrDefault(_unit.SelectedIndex - 1);

    private void RefreshWeek()
    {
        _loading = true;
        try
        {
            using var s = _host.Open();
            var info = _info = s.Reports.WeekInfo(DateOnly.FromDateTime(_day.Value));
            _week.Text = $"KW {info.CalendarWeek}/{info.Year}: {Ui.Date(info.StartDate)} – {Ui.Date(info.EndDate)}, Nr. {info.ReportNumber}"
                + (info.TrainingYear.Length > 0 ? $", {info.TrainingYear}. Ausbildungsjahr" : "")
                + (info.ExistingReportId != null ? "\n(für diese Woche gibt es bereits einen Bericht)" : "");
            for (var i = 0; i < 5; i++)
            {
                var d = info.StartDate.AddDays(i);
                _dayLabels[i].Text = Ui.Day(d) + (info.Holidays.TryGetValue(d, out var h) ? " " + h : "");
                _types[i].SelectedIndex = Math.Max(0, Array.IndexOf(Ui.DayTypeKeys, info.DayTypes.GetValueOrDefault(d, "work")));
            }
            var unitIdx = info.PlanUnitId is { } u ? _units.FindIndex(x => x.Id == u) + 1 : 0;
            _unit.SelectedIndex = Math.Max(0, unitIdx);
            var topics = info.PlanDepartmentIds.Count > 0 ? info.PlanDepartmentIds : s.Units.DefaultTopics(SelectedUnit?.Id);
            CheckTopics(topics);
            _lernfelder.Check(info.SchoolDepartmentIds);
        }
        finally { _loading = false; }
        UpdateSchoolState();
    }

    private void UnitChanged()
    {
        using var s = _host.Open();
        var unit = SelectedUnit;
        if (unit == null || _info == null) return;
        if (unit.IsSchool)
        {
            // Abteilung "Berufsschule": every normal day becomes a school day, no Themen needed
            for (var i = 0; i < 5; i++)
                if (Ui.DayTypeKeys[Math.Max(0, _types[i].SelectedIndex)] == "work") _types[i].SelectedIndex = Array.IndexOf(Ui.DayTypeKeys, "school");
            CheckTopics(Array.Empty<int>());
        }
        else
        {
            for (var i = 0; i < 5; i++)
            {
                var planned = _info.DayTypes.GetValueOrDefault(_info.StartDate.AddDays(i), "work");
                if (Ui.DayTypeKeys[Math.Max(0, _types[i].SelectedIndex)] == "school" && planned == "work") _types[i].SelectedIndex = 0;
            }
            CheckTopics(s.Units.DefaultTopics(unit.Id));
        }
        UpdateSchoolState();
    }

    private void UpdateSchoolState()
    {
        var school = _types.Count(c => Ui.DayTypeKeys[Math.Max(0, c.SelectedIndex)] == "school");
        _lernfelder.Enabled = school > 0;
        _schoolHint.Text = school == 0 ? "Keine Schultage in dieser Woche."
            : $"{school} Schultag(e): Themen aus den gewählten Lernfeldern. Vorausgewählt sind die Lernfelder, die zu diesem Zeitpunkt " +
              "des Ausbildungsjahres dran sind (3. Jahr: die der Fachrichtung) bzw. die der Berufsschul-Planung.";
    }

    private void CheckTopics(IEnumerable<int> ids)
    {
        var set = ids.ToHashSet();
        for (var i = 0; i < _topicList.Count; i++) _topics.SetItemChecked(i, set.Contains(_topicList[i].Id));
    }

    private void Create()
    {
        if (_info == null) return;
        if (_info.ExistingReportId != null) { Ui.Info("Für diese Woche gibt es schon einen Bericht."); return; }
        var schoolDays = _types.Count(c => Ui.DayTypeKeys[Math.Max(0, c.SelectedIndex)] == "school");
        if (schoolDays > 0 && _lernfelder.CheckedIds.Count == 0 && _subjects.Count > 0)
        {
            Ui.Info("Bitte mindestens ein Lernfeld für die Schultage auswählen.");
            return;
        }
        var req = new GenerateRequest
        {
            StartDate = _info.StartDate,
            UnitId = SelectedUnit?.Id,
            DepartmentIds = _topics.CheckedIndices.Cast<int>().Select(i => _topicList[i].Id).ToList(),
            Count = (int)_count.Value,
            CountMax = _countMax.Value > _count.Value ? (int)_countMax.Value : 0,
            CountMode = _mode.SelectedIndex == 1 ? "per_day" : "per_week",
            SchoolDepartmentIds = schoolDays > 0 && _lernfelder.CheckedIds.Count > 0 ? _lernfelder.CheckedIds : null,
        };
        for (var i = 0; i < 5; i++) req.DayTypes[_info.StartDate.AddDays(i)] = Ui.DayTypeKeys[Math.Max(0, _types[i].SelectedIndex)];
        Ui.Try(() =>
        {
            using var s = _host.Open();
            var cfg = s.Settings.Get();
            req.FillHours = cfg.FillHours;
            req.AvoidRecent = cfg.AvoidRecentReports > 0;
            req.Validate();
            var (r, warnings) = s.Reports.Generate(req);
            CreatedId = r.Id;
            Ui.Warnings(warnings);
            DialogResult = DialogResult.OK;
        });
    }
}
