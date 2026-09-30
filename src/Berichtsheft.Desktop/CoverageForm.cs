using Berichtsheft.Core.Hosting;

namespace Berichtsheft.Desktop;

/// <summary>Every week of the training with its state; a missing week can be created from here.</summary>
internal sealed class CoverageForm : Form
{
    private readonly DataGridView _grid = Ui.Grid();
    private readonly Label _hint = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
    private readonly CheckBox _onlyOpen = new() { Text = "Nur fehlende Wochen zeigen", AutoSize = true, Checked = true, Margin = new Padding(8, 8, 0, 0) };
    private readonly AppHost _host;
    private List<Berichtsheft.Core.Dtos.CoverageWeek> _weeks = new();
    /// <summary>Set when the user wants a report for a week that does not exist yet.</summary>
    public DateOnly? CreateWeek { get; private set; }

    public CoverageForm(AppHost host)
    {
        _host = host;
        Ui.Dialog(this, "Wochenübersicht", 640, 460, resizable: true);
        _grid.Columns.Add(Ui.Col("Jahr", 12));
        _grid.Columns.Add(Ui.Col("KW", 12));
        _grid.Columns.Add(Ui.Col("Zeitraum", 34));
        _grid.Columns.Add(Ui.Col("Nr.", 10));
        _grid.Columns.Add(Ui.Col("Status", 30));
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Create(); };
        var create = Ui.Button("Bericht erstellen…", (_, _) => Create(), 130);
        var bar = Ui.ButtonBar(DockStyle.Bottom, create, Ui.CancelButton(this, "Schließen"));
        bar.Controls.Add(_onlyOpen);
        _onlyOpen.CheckedChanged += (_, _) => Fill();
        Controls.Add(_grid);
        Controls.Add(_hint);
        Controls.Add(bar);
        Ui.Try(() =>
        {
            using var s = _host.Open();
            var cov = s.Reports.Coverage();
            _weeks = cov.Weeks;
            _hint.Text = cov.Hint.Length > 0 ? cov.Hint
                : $"{cov.Missing} Wochen ohne Bericht, {cov.Overdue} überfällig (nicht unterschrieben).";
        });
        Fill();
    }

    private void Fill()
    {
        _grid.Rows.Clear();
        foreach (var w in _weeks.Where(w => !_onlyOpen.Checked || w.Status is "missing" or "draft" or "exported").Reverse<Berichtsheft.Core.Dtos.CoverageWeek>())
        {
            var i = _grid.Rows.Add(w.Year, w.CalendarWeek, $"{Ui.Date(w.StartDate)} – {Ui.Date(w.EndDate)}", w.ReportNumber, Ui.StatusName(w.Status) + (w.Overdue ? " – überfällig" : ""));
            _grid.Rows[i].Tag = w;
            if (w.Status == "missing" || w.Overdue) _grid.Rows[i].DefaultCellStyle.ForeColor = Color.Firebrick;
        }
    }

    private void Create()
    {
        if (_grid.CurrentRow?.Tag is not Berichtsheft.Core.Dtos.CoverageWeek w) return;
        if (w.ReportId != null) { Ui.Info("Für diese Woche gibt es bereits einen Bericht. Er ist im Hauptfenster in der Liste."); return; }
        CreateWeek = w.StartDate;
        DialogResult = DialogResult.OK;
    }
}
