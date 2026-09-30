using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Hosting;

namespace Berichtsheft.Desktop;

/// <summary>Creates the reports of a whole range of weeks at once (Abteilung/Themen come from the plan).</summary>
internal sealed class BulkForm : Form
{
    private readonly AppHost _host;
    private readonly DateTimePicker _from = new() { Format = DateTimePickerFormat.Short, Width = 110 };
    private readonly DateTimePicker _to = new() { Format = DateTimePickerFormat.Short, Width = 110 };
    private readonly ComboBox _unit = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly DataGridView _grid = Ui.Grid();
    private readonly Label _summary = new() { AutoSize = true, Margin = new Padding(12, 6, 0, 0) };
    private List<UnitOut> _units = new();
    private Button _create = null!;

    public BulkForm(AppHost host)
    {
        _host = host;
        Ui.Dialog(this, "Mehrere Wochen erstellen", 760, 520, resizable: true);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        top.Controls.AddRange(new Control[] { Ui.Label("Von:"), _from, Ui.Label("Bis:"), _to, Ui.Label("Falls im Plan leer, Abteilung:"), _unit,
            Ui.Button("Vorschau", (_, _) => Ui.Try(() => Run(dryRun: true)), 80) });
        _grid.Columns.Add(Ui.Col("KW", 12));
        _grid.Columns.Add(Ui.Col("Zeitraum", 26));
        _grid.Columns.Add(Ui.Col("Abteilung / Themen", 42));
        _grid.Columns.Add(Ui.Col("Aktion", 26));
        _create = Ui.Button("Erstellen", (_, _) => Ui.Try(() => Run(dryRun: false)), 90);
        _create.Enabled = false;
        var bar = Ui.ButtonBar(DockStyle.Bottom, _create, Ui.CancelButton(this, "Schließen"));
        bar.Controls.Add(_summary);
        Controls.Add(_grid);
        Controls.Add(top);
        Controls.Add(bar);

        Ui.Try(() =>
        {
            using var s = _host.Open();
            var cfg = s.Settings.Get();
            _units = s.Units.List(onlyActive: true);
            _unit.Items.Add("(keine)");
            _unit.Items.AddRange(_units.Select(u => (object)u.Name).ToArray());
            _unit.SelectedIndex = 0;
            var first = s.Reports.Coverage().Weeks.FirstOrDefault(w => w.Status == "missing");
            _from.Value = (first?.StartDate ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue);
            _to.Value = DateTime.Today;
        });
        _from.ValueChanged += (_, _) => _create.Enabled = false;
        _to.ValueChanged += (_, _) => _create.Enabled = false;
    }

    private static string ActionText(BulkWeek w) => w.Action switch
    {
        "create" => "wird erstellt", "exists" => "vorhanden", "no_departments" => "keine Themen – im Plan eintragen",
        "no_workdays" => "keine Betriebstage", "too_few" => "zu wenig Textbausteine", "error" => "Fehler", _ => w.Action,
    };

    private void Run(bool dryRun)
    {
        var s0 = _host.Open();
        using var s = s0;
        var cfg = s.Settings.Get();
        var fallback = _units.ElementAtOrDefault(_unit.SelectedIndex - 1);
        var req = new BulkRequest
        {
            StartDate = DateOnly.FromDateTime(_from.Value), EndDate = DateOnly.FromDateTime(_to.Value),
            FallbackUnitId = fallback?.Id, FallbackDepartmentIds = fallback?.TopicIds ?? new(),
            Count = cfg.DefaultCount, CountMax = cfg.DefaultCountMax, CountMode = cfg.DefaultCountMode, FillHours = cfg.FillHours, AvoidRecent = cfg.AvoidRecentReports > 0,
        };
        req.Validate();
        var res = s.Reports.Bulk(req, dryRun);
        _grid.Rows.Clear();
        foreach (var w in res.Weeks)
            _grid.Rows.Add($"{w.CalendarWeek}/{w.Year}", $"{Ui.Date(w.StartDate)} – {Ui.Date(w.EndDate)}",
                (w.DepartmentLabel + (w.TopicLabel.Length > 0 ? " – " + w.TopicLabel : "")).Trim(' ', '–'), ActionText(w));
        var n = res.Weeks.Count(w => w.Action == "create");
        if (dryRun)
        {
            _summary.Text = $"{n} Berichte würden erstellt.";
            _create.Enabled = n > 0;
        }
        else
        {
            _summary.Text = $"{res.Created} Berichte erstellt.";
            _create.Enabled = false;
            Ui.Warnings(res.Warnings);
        }
    }
}
