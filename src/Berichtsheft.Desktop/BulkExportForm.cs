using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Hosting;

namespace Berichtsheft.Desktop;

/// <summary>Sign all not yet signed reports of a period in one go and save them all as one ZIP (PDF or Word).</summary>
internal sealed class BulkExportForm : Form
{
    private readonly AppHost _host;
    private readonly DateTimePicker _from = new() { Format = DateTimePickerFormat.Short, Width = 110 };
    private readonly DateTimePicker _to = new() { Format = DateTimePickerFormat.Short, Width = 110 };
    private readonly CheckBox _sign = new() { Text = "Noch nicht unterschriebene Berichte jetzt unterschreiben", AutoSize = true, Checked = true };
    private readonly Label _info = new() { AutoSize = true, MaximumSize = new Size(440, 0) };
    private List<ReportSummary> _inRange = new();

    public BulkExportForm(AppHost host)
    {
        _host = host;
        var content = Ui.Form(("Von:", _from), ("Bis:", _to), ("", _sign), ("", _info));
        Ui.Dialog(this, "Berichte unterschreiben und exportieren", 480, 210);
        Controls.Add(content);
        content.Dock = DockStyle.Top;
        Controls.Add(Ui.ButtonBar(DockStyle.Bottom, Ui.OkButton(this, (_, _) => { if (Go()) DialogResult = DialogResult.OK; }, "Weiter…"), Ui.CancelButton(this, "Schließen")));
        Ui.Try(() =>
        {
            using var s = _host.Open();
            var all = s.Reports.List(1000);
            if (all.Count > 0) { _from.Value = all.Min(r => r.StartDate).ToDateTime(TimeOnly.MinValue); _to.Value = all.Max(r => r.EndDate).ToDateTime(TimeOnly.MinValue); }
        });
        _from.ValueChanged += (_, _) => Update_();
        _to.ValueChanged += (_, _) => Update_();
        Update_();
    }

    private List<ReportSummary> LoadRange()
    {
        using var s = _host.Open();
        var from = DateOnly.FromDateTime(_from.Value); var to = DateOnly.FromDateTime(_to.Value);
        return s.Reports.List(1000).Where(r => r.StartDate >= from && r.StartDate <= to).OrderBy(r => r.StartDate).ToList();
    }

    private void Update_()
    {
        Ui.Try(() => _inRange = LoadRange());
        var unsigned = _inRange.Count(r => !r.Signed);
        _info.Text = _inRange.Count == 0 ? "In diesem Zeitraum gibt es keine Berichte."
            : $"{_inRange.Count} Berichte im Zeitraum, davon {unsigned} noch nicht unterschrieben.";
        _sign.Enabled = unsigned > 0;
    }

    private bool Go()
    {
        Update_();
        if (_inRange.Count == 0) { Ui.Info("In diesem Zeitraum gibt es keine Berichte."); return false; }
        var ids = _inRange.Select(r => r.Id).ToList();
        if (_sign.Enabled && _sign.Checked)
        {
            SignForm.Run(this, _host, _inRange.Where(r => !r.Signed).Select(r => r.Id).ToList());
            Update_();
            if (_inRange.Any(r => !r.Signed) && !Ui.Confirm($"{_inRange.Count(r => !r.Signed)} Berichte sind noch nicht unterschrieben. Trotzdem exportieren?")) return false;
        }
        ReportFiles.SaveAs(this, _host, ids);
        return true;
    }
}
