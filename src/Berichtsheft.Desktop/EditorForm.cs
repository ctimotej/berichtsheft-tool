using System.Globalization;
using System.Text.Json;
using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Generation;
using Berichtsheft.Core.Hosting;
using Berichtsheft.Core.Models;
using Berichtsheft.Core.Services;

namespace Berichtsheft.Desktop;

/// <summary>Edits one report: header fields, and per day the type and the lines of the form.</summary>
internal sealed class EditorForm : Form
{
    private readonly AppHost _host;
    private readonly int _id;
    private readonly TextBox _number = new() { Width = 50 };
    private readonly TextBox _name = new() { Width = 160 };
    private readonly TextBox _year = new() { Width = 40 };
    private readonly TextBox _dept = new() { Width = 200 };
    private readonly Label _state = new() { AutoSize = true };
    private readonly ListBox _dayList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ComboBox _type = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly DataGridView _grid = Ui.Grid();
    private readonly TextBox _remarks = new() { Multiline = true, Height = 44, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly Label _hoursHint = new() { AutoSize = true, ForeColor = Color.Firebrick };
    private AppSettings _cfg = new();
    private readonly Label _total = new() { AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
    private List<ReportDay> _days = new();
    private ReportOut _out = new();
    private bool _dirty, _loading;
    private int _suppressDayChange;

    public EditorForm(AppHost host, int id)
    {
        _host = host; _id = id;
        Ui.Dialog(this, "Bericht bearbeiten", 860, 600, resizable: true);
        MinimumSize = new Size(720, 480);
        MaximizeBox = true;
        ShowInTaskbar = true;

        // header
        var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 0) };
        void Field(string label, Control c) { head.Controls.Add(Ui.Label(label)); c.Margin = new Padding(0, 3, 12, 3); head.Controls.Add(c); c.TextChanged += (_, _) => Dirty(); }
        Field("Nr.:", _number); Field("Name:", _name); Field("Ausbildungsjahr:", _year); Field("Abteilung:", _dept);
        head.Controls.Add(_state);

        // days | entries
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 200, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Padding = new Padding(8, 8, 4, 0);
        split.Panel2.Padding = new Padding(4, 8, 8, 0);
        split.Panel1.Controls.Add(_dayList);

        _type.Items.AddRange(Ui.DayTypeNames);
        var typeRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        typeRow.Controls.Add(Ui.Label("Tagesart:"));
        typeRow.Controls.Add(_type);

        _grid.ReadOnly = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.MultiSelect = false;
        _grid.EditMode = DataGridViewEditMode.EditOnEnter;
        _grid.Columns.Add(Ui.Col("Tätigkeit", 90, readOnly: false));
        _grid.Columns.Add(Ui.Col("Std.", 10, readOnly: false));
        _grid.Columns[0].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _grid.CellEndEdit += (_, e) => EntryEdited(e.RowIndex);

        var entryButtons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 118, FlowDirection = FlowDirection.TopDown, Padding = new Padding(6, 0, 0, 0) };
        foreach (var (t, h) in new (string, Action)[]
        {
            ("Zeile hinzufügen", AddEntry), ("Zeile löschen", DeleteEntry), ("Nach oben", () => MoveEntry(-1)),
            ("Nach unten", () => MoveEntry(1)), ("Zeile neu erzeugen", RegenerateEntry),
        })
            entryButtons.Controls.Add(Ui.Button(t, (_, _) => Ui.Try(h), 110));

        var entryArea = new Panel { Dock = DockStyle.Fill };
        entryArea.Controls.Add(_grid);
        entryArea.Controls.Add(entryButtons);
        split.Panel2.Controls.Add(entryArea);
        split.Panel2.Controls.Add(typeRow);

        // bottom
        var remarksBox = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 70, ColumnCount = 2, Padding = new Padding(8, 4, 8, 0) };
        remarksBox.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        remarksBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        remarksBox.Controls.Add(Ui.Label("Bemerkungen:"));
        remarksBox.Controls.Add(_remarks);
        _remarks.TextChanged += (_, _) => Dirty();

        var buttons = Ui.ButtonBar(DockStyle.Bottom,
            Ui.Button("Speichern", (_, _) => Ui.Try(() => Save())),
            Ui.Button("Alles neu erzeugen", (_, _) => Ui.Try(RegenerateAll), 120),
            Ui.Button("Stunden prüfen…", (_, _) => Ui.Try(CheckHours), 110),
            Ui.Button("In Word öffnen", (_, _) => Ui.Try(OpenWord), 110),
            Ui.Button("Unterschreiben…", (_, _) => Ui.Try(Sign), 110),
            Ui.Button("Schließen", (_, _) => Close()));
        buttons.Controls.Add(_total);
        _total.Margin = new Padding(20, 6, 0, 0);
        buttons.Controls.Add(_hoursHint);
        _hoursHint.Margin = new Padding(10, 6, 0, 0);

        Controls.Add(split);
        Controls.Add(remarksBox);
        Controls.Add(buttons);
        Controls.Add(head);

        _dayList.SelectedIndexChanged += (_, _) => { if (_suppressDayChange == 0) ShowDay(); };
        _type.SelectionChangeCommitted += (_, _) => Ui.Try(TypeChanged);
        Load += (_, _) => Ui.Try(() => LoadReport());
    }

    private void Dirty() { if (!_loading) _dirty = true; }
    private ReportDay? Current => _dayList.SelectedIndex >= 0 && _dayList.SelectedIndex < _days.Count ? _days[_dayList.SelectedIndex] : null;

    // ------------------------------------------------------------ load / save
    private void LoadReport(int keepDay = 0)
    {
        _loading = true;
        try
        {
            using var s = _host.Open();
            _cfg = s.Settings.Get();
            _out = s.Reports.ToOut(s.Reports.Get(_id));
            // edit a copy: the lists inside the loaded entity must not be shared with the UI
            _days = JsonSerializer.Deserialize<List<ReportDay>>(JsonSerializer.Serialize(_out.Days)) ?? new();
            Text = $"Bericht Nr. {_out.ReportNumber} – KW {_out.CalendarWeek}/{_out.Year}";
            _number.Text = _out.ReportNumber; _name.Text = _out.TraineeName; _year.Text = _out.TrainingYear; _dept.Text = _out.DepartmentLabel;
            _remarks.Text = _out.Remarks;
            var signed = _out.Signatures.Any(x => x.Role == "trainee");
            _state.Text = "Status: " + Ui.StatusName(_out.Status) + (signed ? " (unterschrieben)" : "");
            FillDayList(Math.Min(keepDay, _days.Count - 1));
        }
        finally { _loading = false; }
        _dirty = false;
    }

    private void FillDayList(int select)
    {
        _suppressDayChange++;
        try
        {
            _dayList.Items.Clear();
            foreach (var d in _days) _dayList.Items.Add(DayText(d));
            if (_days.Count > 0) _dayList.SelectedIndex = Math.Max(0, select);
        }
        finally { _suppressDayChange--; }
        ShowDay();
        UpdateTotal();
    }

    private static string DayText(ReportDay d)
    {
        var h = d.Entries.Sum(e => e.Hours ?? 0);
        return $"{Ui.Day(d.Date)}  {Ui.DayTypeName(d.Type)}" + (h > 0 ? $"  {Scheduling.FormatHours(h)} h" : "");
    }

    private void UpdateTotal()
    {
        _total.Text = "Gesamt: " + Scheduling.FormatHours(_days.SelectMany(d => d.Entries).Sum(e => e.Hours ?? 0)) + " Std.";
        var n = HoursCheck.Check(_days, _cfg).Count;
        _hoursHint.Text = n > 0 ? $"⚠ {n} Tag(e) weichen von der Sollzeit ab" : "";
    }

    /// <summary>Lists the days whose hours do not match the working hours and offers to spread the expected hours over the entries.</summary>
    private void CheckHours()
    {
        var issues = HoursCheck.Check(_days, _cfg);
        if (issues.Count == 0) { Ui.Info("Alle Tage stimmen mit der Sollarbeitszeit überein (Betrieb "
            + Scheduling.FormatHours(_cfg.HoursPerDay) + " Std., Berufsschule " + Scheduling.FormatHours(_cfg.SchoolHoursPerDay) + " Std.)."); return; }
        if (!Ui.Confirm("Diese Tage weichen von der Sollarbeitszeit ab:\n\n" + string.Join("\n", issues.Select(i => "• " + i.Message))
                        + "\n\nSollstunden gleichmäßig auf die Einträge dieser Tage verteilen?")) return;
        HoursCheck.Redistribute(_days, _cfg, issues.Select(i => i.DayIndex));
        var keep = Math.Max(0, _dayList.SelectedIndex);
        FillDayList(keep);
        Dirty();
    }

    private bool Save(bool quiet = false)
    {
        _grid.EndEdit();
        if (!_dirty) return true;
        if (_out.Signatures.Count > 0 && !quiet &&
            !Ui.Confirm("Beim Speichern von Änderungen wird die Unterschrift entfernt und muss erneut gesetzt werden. Speichern?"))
            return false;
        var upd = new ReportUpdate
        {
            ReportNumber = _number.Text.Trim(), TraineeName = _name.Text.Trim(), TrainingYear = _year.Text.Trim(),
            DepartmentLabel = _dept.Text.Trim(), Remarks = _remarks.Text, Days = _days,
        };
        upd.Validate();
        using (var s = _host.Open()) s.Reports.Update(_id, upd);
        LoadReport(Math.Max(0, _dayList.SelectedIndex));
        return true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel || !_dirty) return;
        _grid.EndEdit();
        var r = MessageBox.Show("Änderungen speichern?", Ui.Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) e.Cancel = true;
        else if (r == DialogResult.Yes)
        {
            try { if (!Save()) e.Cancel = true; }
            catch (Exception exc) { Ui.ShowError(exc); e.Cancel = true; }
        }
    }

    // ------------------------------------------------------------ day / entries
    private void ShowDay()
    {
        _grid.EndEdit();
        var d = Current;
        _grid.Rows.Clear();
        if (d == null) return;
        var was = _loading;
        _loading = true;
        _type.SelectedIndex = Math.Max(0, Array.IndexOf(Ui.DayTypeKeys, d.Type));
        _loading = was;
        foreach (var e in d.Entries) _grid.Rows.Add(e.Text, Scheduling.FormatHours(e.Hours));
        for (var i = 0; i < d.Entries.Count; i++) _grid.Rows[i].Tag = d.Entries[i];
    }

    /// <summary>Updates the caption of a day in the list without the list re-loading the entry grid.</summary>
    private void UpdateDayText(int idx)
    {
        var selected = _dayList.SelectedIndex;
        _suppressDayChange++;
        try
        {
            _dayList.Items[idx] = DayText(_days[idx]);
            if (_dayList.SelectedIndex != selected) _dayList.SelectedIndex = selected;
        }
        finally { _suppressDayChange--; }
    }

    private void RefreshDay(int selectRow = -1)
    {
        UpdateDayText(_dayList.SelectedIndex);
        ShowDay();
        if (selectRow >= 0 && selectRow < _grid.Rows.Count) { _grid.ClearSelection(); _grid.Rows[selectRow].Selected = true; _grid.CurrentCell = _grid.Rows[selectRow].Cells[0]; }
        UpdateTotal();
        Dirty();
    }

    private void EntryEdited(int row)
    {
        if (Current == null || row < 0 || row >= Current.Entries.Count) return;
        var e = Current.Entries[row];
        e.Text = Convert.ToString(_grid.Rows[row].Cells[0].Value) ?? "";
        var raw = (Convert.ToString(_grid.Rows[row].Cells[1].Value) ?? "").Trim().Replace(',', '.');
        e.Hours = raw.Length > 0 && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && h > 0 ? h : null;
        _grid.Rows[row].Cells[1].Value = Scheduling.FormatHours(e.Hours);
        UpdateDayText(_dayList.SelectedIndex);
        UpdateTotal();
        Dirty();
    }

    private int SelectedRow => _grid.CurrentRow?.Index ?? -1;

    private void AddEntry()
    {
        if (Current == null) return;
        var at = SelectedRow >= 0 ? SelectedRow + 1 : Current.Entries.Count;
        Current.Entries.Insert(at, new ReportEntry());
        RefreshDay(at);
        _grid.BeginEdit(true);
    }

    private void DeleteEntry()
    {
        var row = SelectedRow;
        if (Current == null || row < 0) return;
        Current.Entries.RemoveAt(row);
        RefreshDay(Math.Min(row, Current.Entries.Count - 1));
    }

    /// <summary>Moves a line up/down; at the first/last line of a day it moves over to the neighbouring day.</summary>
    private void MoveEntry(int dir)
    {
        var row = SelectedRow;
        if (Current == null || row < 0) return;
        var list = Current.Entries;
        var to = row + dir;
        if (to >= 0 && to < list.Count) { (list[row], list[to]) = (list[to], list[row]); RefreshDay(to); return; }
        var otherIdx = _dayList.SelectedIndex + dir;
        if (otherIdx < 0 || otherIdx >= _days.Count) return;
        var e = list[row];
        list.RemoveAt(row);
        var other = _days[otherIdx].Entries;
        var pos = dir < 0 ? other.Count : 0;
        other.Insert(pos, e);
        UpdateDayText(_dayList.SelectedIndex);
        _suppressDayChange++;
        try { _dayList.SelectedIndex = otherIdx; }
        finally { _suppressDayChange--; }
        UpdateDayText(otherIdx);
        RefreshDay(pos);
    }

    private HashSet<int> UsedSnippets() =>
        _days.SelectMany(d => d.Entries).Where(e => e.SnippetId is > 0).Select(e => e.SnippetId!.Value).ToHashSet();

    private ReportEntry NewEntryFor(AppHost.Session s, ReportDay day, HashSet<int> exclude) =>
        s.Reports.GenerateEntry(_id, new EntryRequest { Date = day.Date, School = day.Type == "school", ExcludeSnippetIds = exclude.ToList() });

    private void RegenerateEntry()
    {
        var row = SelectedRow;
        if (Current == null || row < 0) return;
        if (Current.Type is not ("work" or "school")) { Ui.Info("Für diese Tagesart gibt es keine Textbausteine."); return; }
        using var s = _host.Open();
        var fresh = NewEntryFor(s, Current, UsedSnippets());
        var old = Current.Entries[row];
        fresh.Hours = old.Hours;
        Current.Entries[row] = fresh;
        RefreshDay(row);
    }

    private void TypeChanged()
    {
        var day = Current;
        if (day == null || _loading) return;
        var type = Ui.DayTypeKeys[Math.Max(0, _type.SelectedIndex)];
        if (type == day.Type) return;
        var hasText = day.Entries.Any(e => e.Text.Trim().Length > 0);
        var replace = !hasText || Ui.Confirm($"Die Einträge dieses Tages durch passende Einträge für „{Ui.DayTypeName(type)}“ ersetzen?");
        if (replace)
        {
            using var s = _host.Open();
            var cfg = s.Settings.Get();
            day.Type = type;
            if (type == "work")
            {
                var used = UsedSnippets();
                var list = new List<ReportEntry>();
                var n = Math.Max(1, cfg.DefaultCount / 5);
                var hours = Scheduling.DistributeHours(cfg.HoursPerDay, n);
                for (var i = 0; i < n; i++)
                {
                    var e = NewEntryFor(s, day, used);
                    if (e.SnippetId is > 0) used.Add(e.SnippetId.Value);
                    e.Hours = cfg.FillHours && hours[i] > 0 ? hours[i] : null;
                    list.Add(e);
                }
                day.Entries = list;
            }
            else if (type == "school")
            {
                var subjects = day.SubjectIds.Count > 0 ? day.SubjectIds : s.Catalog.ActiveIds("school");
                try
                {
                    day.Entries = s.Reports.SchoolDayEntries(_id, new SchoolDayRequest { Date = day.Date, SubjectIds = subjects, ExcludeSnippetIds = UsedSnippets().ToList(), Count = cfg.SchoolEntriesPerDay });
                }
                catch (Berichtsheft.Core.AppError) { day.Entries = new() { new ReportEntry { Text = "Berufsschule", Hours = cfg.FillHours ? cfg.SchoolHoursPerDay : null } }; }
            }
            else day.Entries = new() { new ReportEntry { Text = Ui.DayTypeName(type) } };
        }
        else day.Type = type;
        RefreshDay();
    }

    // ------------------------------------------------------------ report actions
    private void RegenerateAll()
    {
        if (!Ui.Confirm("Alle Einträge der Betriebstage neu erzeugen? Manuelle Änderungen gehen verloren.")) return;
        using (var s = _host.Open())
        {
            var (_, warnings) = s.Reports.Regenerate(_id);
            Ui.Warnings(warnings);
        }
        _dirty = false;
        LoadReport();
        _dirty = false;
    }

    private void OpenWord()
    {
        if (!Save(quiet: false)) return;
        using var s = _host.Open();
        var (data, name) = ReportFiles.Document(s, _id);
        Ui.OpenBytes(data, name);
        LoadReport(Math.Max(0, _dayList.SelectedIndex));
    }

    private void Sign()
    {
        if (!Save(quiet: true)) return;
        if (SignForm.Run(this, _host, new[] { _id }) > 0) LoadReport(Math.Max(0, _dayList.SelectedIndex));
    }
}
