using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Hosting;

namespace Berichtsheft.Desktop;

/// <summary>Rotation (Abteilung per period), Berufsschule blocks and vacation. New reports take day types,
/// Abteilung and Themen from here.</summary>
internal sealed class PlanForm : Form
{
    private static readonly string[] Kinds = { "rotation", "school", "vacation" };
    private static readonly string[] KindNames = { "Einsatz in Abteilung", "Berufsschule", "Urlaub" };
    private readonly AppHost _host;
    private readonly DataGridView _grid = Ui.Grid();

    public PlanForm(AppHost host)
    {
        _host = host;
        Ui.Dialog(this, "Planung", 720, 440, resizable: true);
        _grid.Columns.Add(Ui.Col("Art", 20));
        _grid.Columns.Add(Ui.Col("Von", 12));
        _grid.Columns.Add(Ui.Col("Bis", 12));
        _grid.Columns.Add(Ui.Col("Abteilung / Themen / Fächer", 40));
        _grid.Columns.Add(Ui.Col("Notiz", 20));
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Edit(); };
        Controls.Add(_grid);
        Controls.Add(Ui.ButtonBar(DockStyle.Bottom, Ui.Button("Hinzufügen…", (_, _) => AddOrEdit(null), 100), Ui.Button("Ändern…", (_, _) => Edit()),
            Ui.Button("Löschen", (_, _) => Delete()), Ui.CancelButton(this, "Schließen")));
        Reload();
    }

    private void Reload()
    {
        _grid.Rows.Clear();
        Ui.Try(() =>
        {
            using var s = _host.Open();
            foreach (var p in s.Plan.List())
            {
                var what = string.Join(p.Kind == "rotation" ? ", " : ", ", new[] { p.UnitName }.Concat(p.Labels).Where(x => x.Length > 0));
                var i = _grid.Rows.Add(KindNames[Array.IndexOf(Kinds, p.Kind)], Ui.Date(p.StartDate), Ui.Date(p.EndDate), what, p.Note);
                _grid.Rows[i].Tag = p;
            }
        });
    }

    private void Edit() { if (_grid.CurrentRow?.Tag is PlanPeriodOut p) AddOrEdit(p); }

    private void Delete()
    {
        if (_grid.CurrentRow?.Tag is not PlanPeriodOut p || !Ui.Confirm("Diesen Zeitraum löschen?")) return;
        Ui.Try(() => { using var s = _host.Open(); s.Plan.Delete(p.Id); });
        Reload();
    }

    private void AddOrEdit(PlanPeriodOut? existing)
    {
        List<UnitOut> units = new(); List<DepartmentOut> topics = new(), subjects = new();
        Ui.Try(() =>
        {
            using var s = _host.Open();
            units = s.Units.List(onlyActive: true);
            topics = s.Catalog.ListDepartments(onlyActive: true, kind: "topic");
            subjects = s.Catalog.ListDepartments(onlyActive: true, kind: "school");
        });
        var kind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        kind.Items.AddRange(KindNames);
        var from = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 110 };
        var to = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 110 };
        var unit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        unit.Items.Add("(keine)");
        unit.Items.AddRange(units.Select(u => (object)u.Name).ToArray());
        var list = new CheckedListBox { CheckOnClick = true, Height = 130, IntegralHeight = false };
        var listLabel = new Label { AutoSize = true, Margin = new Padding(0, 6, 6, 6) };
        var note = new TextBox();
        List<DepartmentOut> shown = topics;

        void Fill()
        {
            var k = Kinds[Math.Max(0, kind.SelectedIndex)];
            shown = k == "school" ? subjects : topics;
            var keep = existing != null && existing.Kind == k ? existing.DepartmentIds.ToHashSet() : new HashSet<int>();
            list.Items.Clear();
            foreach (var d in shown) list.Items.Add(d.Code.Length > 0 ? $"{d.Code}: {d.Name}" : d.Name, keep.Contains(d.Id));
            list.Enabled = k != "vacation";
            unit.Enabled = k == "rotation";
        }
        kind.SelectedIndexChanged += (_, _) => Fill();
        kind.SelectedIndex = existing != null ? Array.IndexOf(Kinds, existing.Kind) : 0;
        if (existing != null)
        {
            from.Value = existing.StartDate.ToDateTime(TimeOnly.MinValue); to.Value = existing.EndDate.ToDateTime(TimeOnly.MinValue);
            unit.SelectedIndex = Math.Max(0, units.FindIndex(u => u.Id == existing.UnitId) + 1);
            note.Text = existing.Note;
        }
        else unit.SelectedIndex = 0;

        var hint = new Label { Text = "Bei „Einsatz“ ohne Themen werden die Standard-Themen der Abteilung verwendet.", AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(360, 0) };
        var content = Ui.Form(("Art:", kind), ("Von:", from), ("Bis:", to), ("Abteilung:", unit), ("Themen / Fächer:", list), ("", hint), ("Notiz:", note));
        using var dlg = new EditDialog(existing == null ? "Zeitraum hinzufügen" : "Zeitraum ändern", 480, content, () =>
        {
            var k = Kinds[Math.Max(0, kind.SelectedIndex)];
            var data = new PlanPeriodIn
            {
                Kind = k, StartDate = DateOnly.FromDateTime(from.Value), EndDate = DateOnly.FromDateTime(to.Value),
                UnitId = k == "rotation" ? units.ElementAtOrDefault(unit.SelectedIndex - 1)?.Id : null,
                DepartmentIds = k == "vacation" ? new() : list.CheckedIndices.Cast<int>().Select(i => shown[i].Id).ToList(), Note = note.Text.Trim(),
            };
            return Ui.Try(() =>
            {
                data.Validate();
                using var s = _host.Open();
                if (existing == null) s.Plan.Create(data); else s.Plan.Update(existing.Id, data);
            });
        });
        if (dlg.ShowDialog(this) == DialogResult.OK) Reload();
    }
}
