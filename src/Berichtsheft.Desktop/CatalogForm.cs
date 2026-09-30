using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Hosting;

namespace Berichtsheft.Desktop;

/// <summary>Abteilungen, Themen/Fächer and the Textbausteine (the building blocks of the generated lines).</summary>
internal sealed class CatalogForm : Form
{
    private readonly AppHost _host;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _units = Ui.Grid(), _deps = Ui.Grid(), _snips = Ui.Grid(multiSelect: true);
    private readonly ComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly TextBox _search = new() { Width = 180 };
    private List<DepartmentOut> _depList = new();

    public CatalogForm(AppHost host)
    {
        _host = host;
        Ui.Dialog(this, "Themen und Textbausteine", 860, 540, resizable: true);

        _units.Columns.Add(Ui.Col("Abteilung", 30)); _units.Columns.Add(Ui.Col("Standard-Themen", 60)); _units.Columns.Add(Ui.Col("Aktiv", 10));
        _deps.Columns.Add(Ui.Col("Art", 14)); _deps.Columns.Add(Ui.Col("Kürzel", 10)); _deps.Columns.Add(Ui.Col("Name", 40));
        _deps.Columns.Add(Ui.Col("Bausteine", 12)); _deps.Columns.Add(Ui.Col("Aktiv", 10));
        _snips.Columns.Add(Ui.Col("Thema / Fach", 25)); _snips.Columns.Add(Ui.Col("Text", 65)); _snips.Columns.Add(Ui.Col("Aktiv", 10));

        _tabs.TabPages.Add(Tab("Textbausteine", _snips, SnippetTop(),
            ("Hinzufügen…", () => EditSnippet(null)), ("Ändern…", () => EditSnippet(Snippet())), ("Löschen", DeleteSnippets),
            ("Importieren…", ImportSnippets), ("Exportieren…", ExportSnippets)));
        _tabs.TabPages.Add(Tab("Themen und Fächer", _deps, null,
            ("Hinzufügen…", () => EditDepartment(null)), ("Ändern…", () => EditDepartment(Dep())), ("Löschen", DeleteDepartment)));
        _tabs.TabPages.Add(Tab("Abteilungen", _units, null,
            ("Hinzufügen…", () => EditUnit(null)), ("Ändern…", () => EditUnit(Unit())), ("Löschen", DeleteUnit)));
        Controls.Add(_tabs);
        Controls.Add(Ui.ButtonBar(DockStyle.Bottom, Ui.CancelButton(this, "Schließen")));
        _snips.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditSnippet(Snippet()); };
        _deps.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditDepartment(Dep()); };
        _units.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditUnit(Unit()); };
        _search.TextChanged += (_, _) => ReloadSnippets();
        ReloadAll();
    }

    private Control SnippetTop()
    {
        var p = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        p.Controls.AddRange(new Control[] { Ui.Label("Thema/Fach:"), _filter, Ui.Label("Suche:"), _search });
        return p;
    }

    private static TabPage Tab(string title, DataGridView grid, Control? top, params (string Text, Action Do)[] actions)
    {
        var page = new TabPage(title);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
        foreach (var (t, a) in actions) bar.Controls.Add(Ui.Button(t, (_, _) => a(), 100));
        page.Controls.Add(grid);
        if (top != null) page.Controls.Add(top);
        page.Controls.Add(bar);
        return page;
    }

    private SnippetOut? Snippet() => _snips.CurrentRow?.Tag as SnippetOut;
    private DepartmentOut? Dep() => _deps.CurrentRow?.Tag as DepartmentOut;
    private UnitOut? Unit() => _units.CurrentRow?.Tag as UnitOut;

    // ------------------------------------------------------------ loading
    private void ReloadAll()
    {
        Ui.Try(() =>
        {
            using var s = _host.Open();
            _depList = s.Catalog.ListDepartments();
            _deps.Rows.Clear();
            foreach (var d in _depList)
            {
                var i = _deps.Rows.Add(d.Kind == "school" ? "Fach" : "Thema", d.Code, d.Name, d.SnippetCount, d.Active ? "ja" : "");
                _deps.Rows[i].Tag = d;
            }
            _units.Rows.Clear();
            foreach (var u in s.Units.List())
            {
                var i = _units.Rows.Add(u.Name, string.Join(", ", u.TopicNames), u.Active ? "ja" : "");
                _units.Rows[i].Tag = u;
            }
            var sel = _filter.SelectedIndex;
            _filter.SelectedIndexChanged -= FilterChanged;
            _filter.Items.Clear();
            _filter.Items.Add("(alle)");
            _filter.Items.AddRange(_depList.Select(d => (object)(d.Code.Length > 0 ? $"{d.Code}: {d.Name}" : d.Name)).ToArray());
            _filter.SelectedIndex = sel >= 0 && sel < _filter.Items.Count ? sel : 0;
            _filter.SelectedIndexChanged += FilterChanged;
        });
        ReloadSnippets();
    }

    private void FilterChanged(object? sender, EventArgs e) => ReloadSnippets();

    private void ReloadSnippets()
    {
        var keep = Snippet()?.Id;
        _snips.Rows.Clear();
        Ui.Try(() =>
        {
            using var s = _host.Open();
            var dep = _filter.SelectedIndex > 0 ? _depList.ElementAtOrDefault(_filter.SelectedIndex - 1)?.Id : null;
            foreach (var sn in s.Catalog.ListSnippets(dep, _search.Text))
            {
                var i = _snips.Rows.Add(sn.DepartmentCode.Length > 0 ? sn.DepartmentCode : sn.DepartmentName, sn.Text, sn.Active ? "ja" : "");
                _snips.Rows[i].Tag = sn;
                if (sn.Id == keep) _snips.CurrentCell = _snips.Rows[i].Cells[0];
            }
        });
    }

    // ------------------------------------------------------------ snippets
    private void EditSnippet(SnippetOut? sn)
    {
        var dep = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        dep.Items.AddRange(_depList.Select(d => (object)(d.Code.Length > 0 ? $"{d.Code}: {d.Name}" : d.Name)).ToArray());
        var text = new TextBox { Multiline = true, Height = 90, ScrollBars = ScrollBars.Vertical };
        var active = new CheckBox { Text = "Aktiv", Checked = sn?.Active ?? true, AutoSize = true };
        var filterDep = _filter.SelectedIndex > 0 ? _filter.SelectedIndex - 1 : 0;
        dep.SelectedIndex = sn != null ? Math.Max(0, _depList.FindIndex(d => d.Id == sn.DepartmentId)) : Math.Min(filterDep, _depList.Count - 1);
        text.Text = sn?.Text ?? "";
        if (_depList.Count == 0) { Ui.Info("Bitte zuerst ein Thema oder Fach anlegen."); return; }
        var content = Ui.Form(("Thema / Fach:", dep), ("Text:", text), ("", active));
        using var dlg = new EditDialog(sn == null ? "Textbaustein hinzufügen" : "Textbaustein ändern", 520, content, () => Ui.Try(() =>
        {
            var data = new SnippetIn { DepartmentId = _depList[dep.SelectedIndex].Id, Text = text.Text, Active = active.Checked, Category = sn?.Category ?? "", Difficulty = sn?.Difficulty ?? "", Tags = sn?.Tags ?? new() };
            data.Validate();
            using var s = _host.Open();
            if (sn == null) s.Catalog.CreateSnippet(data);
            else s.Catalog.UpdateSnippet(sn.Id, new SnippetPatch { DepartmentId = data.DepartmentId, Text = data.Text, Active = data.Active });
        }));
        if (dlg.ShowDialog(this) == DialogResult.OK) ReloadAll();
    }

    private void DeleteSnippets()
    {
        var ids = _snips.SelectedRows.Cast<DataGridViewRow>().Select(r => ((SnippetOut)r.Tag!).Id).ToList();
        if (ids.Count == 0 || !Ui.Confirm(ids.Count == 1 ? "Diesen Textbaustein löschen?" : $"{ids.Count} Textbausteine löschen?")) return;
        Ui.Try(() => { using var s = _host.Open(); foreach (var id in ids) s.Catalog.DeleteSnippet(id); });
        ReloadAll();
    }

    private void ImportSnippets()
    {
        using var dlg = new OpenFileDialog { Filter = "Textbausteine (*.json;*.csv)|*.json;*.csv" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var replace = Ui.Confirm("Vorhandene Themen und Textbausteine vorher löschen (ersetzen)?\n\nNein = zu den vorhandenen hinzufügen.");
        Ui.Try(() =>
        {
            using var s = _host.Open();
            var r = s.Catalog.ImportFile(File.ReadAllBytes(dlg.FileName), Path.GetFileName(dlg.FileName), replace);
            Ui.Info($"{r.SnippetsCreated} Textbausteine und {r.DepartmentsCreated} Themen/Fächer importiert, {r.SnippetsSkipped} übersprungen (schon vorhanden).");
        });
        ReloadAll();
    }

    private void ExportSnippets()
    {
        using var dlg = new SaveFileDialog { Filter = "JSON (*.json)|*.json|CSV (*.csv)|*.csv", FileName = "textbausteine" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Ui.Try(() =>
        {
            using var s = _host.Open();
            File.WriteAllBytes(dlg.FileName, dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? s.Catalog.ExportCsv() : s.Catalog.ExportJson());
        });
    }

    // ------------------------------------------------------------ Themen / Fächer
    private void EditDepartment(DepartmentOut? d)
    {
        var kind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        kind.Items.AddRange(new object[] { "Thema (Tätigkeit im Betrieb)", "Fach (Berufsschule)" });
        kind.SelectedIndex = d?.Kind == "school" ? 1 : 0;
        var code = new TextBox { Text = d?.Code ?? "", MaxLength = 20 };
        var name = new TextBox { Text = d?.Name ?? "" };
        var desc = new TextBox { Text = d?.Description ?? "" };
        var active = new CheckBox { Text = "Aktiv", Checked = d?.Active ?? true, AutoSize = true };
        var content = Ui.Form(("Art:", kind), ("Kürzel (z. B. LF 3):", code), ("Name:", name), ("Beschreibung:", desc), ("", active));
        using var dlg = new EditDialog(d == null ? "Thema / Fach hinzufügen" : "Thema / Fach ändern", 460, content, () => Ui.Try(() =>
        {
            var data = new DepartmentIn { Name = name.Text, Kind = kind.SelectedIndex == 1 ? "school" : "topic", Code = code.Text.Trim(), Description = desc.Text, Active = active.Checked, SortOrder = d?.SortOrder ?? 0 };
            data.Validate();
            using var s = _host.Open();
            if (d == null) s.Catalog.CreateDepartment(data); else s.Catalog.UpdateDepartment(d.Id, data);
        }));
        if (dlg.ShowDialog(this) == DialogResult.OK) ReloadAll();
    }

    private void DeleteDepartment()
    {
        if (Dep() is not { } d) return;
        if (!Ui.Confirm($"„{d.Name}“ mit allen {d.SnippetCount} Textbausteinen löschen?")) return;
        Ui.Try(() => { using var s = _host.Open(); s.Catalog.DeleteDepartment(d.Id); });
        ReloadAll();
    }

    // ------------------------------------------------------------ Abteilungen
    private void EditUnit(UnitOut? u)
    {
        var name = new TextBox { Text = u?.Name ?? "" };
        var topics = _depList.Where(d => d.Kind == "topic").ToList();
        var list = new CheckedListBox { CheckOnClick = true, Height = 150, IntegralHeight = false };
        foreach (var t in topics) list.Items.Add(t.Name, u?.TopicIds.Contains(t.Id) ?? false);
        var active = new CheckBox { Text = "Aktiv", Checked = u?.Active ?? true, AutoSize = true };
        var content = Ui.Form(("Name:", name), ("Standard-Themen:", list), ("", active));
        using var dlg = new EditDialog(u == null ? "Abteilung hinzufügen" : "Abteilung ändern", 460, content, () => Ui.Try(() =>
        {
            var data = new UnitIn { Name = name.Text, Active = active.Checked, SortOrder = u?.SortOrder ?? 0, Description = u?.Description ?? "", TopicIds = list.CheckedIndices.Cast<int>().Select(i => topics[i].Id).ToList() };
            data.Validate();
            using var s = _host.Open();
            if (u == null) s.Units.Create(data); else s.Units.Update(u.Id, data);
        }));
        if (dlg.ShowDialog(this) == DialogResult.OK) ReloadAll();
    }

    private void DeleteUnit()
    {
        if (Unit() is not { } u || !Ui.Confirm($"Abteilung „{u.Name}“ löschen?")) return;
        Ui.Try(() => { using var s = _host.Open(); s.Units.Delete(u.Id); });
        ReloadAll();
    }
}
