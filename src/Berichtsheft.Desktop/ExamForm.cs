using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Hosting;

namespace Berichtsheft.Desktop;

/// <summary>All reports (or a period) as one PDF with cover sheet, or as a ZIP of Word files.</summary>
internal sealed class ExamForm : Form
{
    private readonly AppHost _host;
    private readonly CheckBox _all = new() { Text = "Ganze Ausbildung", Checked = true, AutoSize = true };
    private readonly DateTimePicker _from = new() { Format = DateTimePickerFormat.Short, Width = 110, Enabled = false };
    private readonly DateTimePicker _to = new() { Format = DateTimePickerFormat.Short, Width = 110, Enabled = false };
    private readonly RadioButton _pdf = new() { Text = "PDF mit Deckblatt", AutoSize = true, Checked = true };
    private readonly RadioButton _zip = new() { Text = "ZIP mit Word-Dateien", AutoSize = true };
    private readonly CheckBox _sign = new() { Text = "Noch nicht unterschriebene Berichte mit der Standard-Unterschrift unterschreiben", AutoSize = true, Checked = true };
    private readonly Label _info = new() { Dock = DockStyle.Fill, Padding = new Padding(10) };

    public ExamForm(AppHost host)
    {
        _host = host;
        Ui.Dialog(this, "Prüfungsexport", 520, 380);
        var range = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 10, 10, 0) };
        range.Controls.AddRange(new Control[] { _all, Ui.Label("Von:"), _from, Ui.Label("Bis:"), _to });
        var opts = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(10, 6, 10, 0), WrapContents = false };
        opts.Controls.AddRange(new Control[] { _pdf, _zip, _sign });
        var export = Ui.Button("Exportieren…", (_, _) => Export(), 110);
        Controls.Add(_info);
        Controls.Add(opts);
        Controls.Add(range);
        Controls.Add(Ui.ButtonBar(DockStyle.Bottom, export, Ui.CancelButton(this, "Schließen")));
        _all.CheckedChanged += (_, _) => { _from.Enabled = _to.Enabled = !_all.Checked; Preview(); };
        _from.ValueChanged += (_, _) => Preview();
        _to.ValueChanged += (_, _) => Preview();
        _pdf.CheckedChanged += (_, _) => Preview();
        Preview();
    }

    private ExamExportRequest Request() => new()
    {
        StartDate = _all.Checked ? null : DateOnly.FromDateTime(_from.Value),
        EndDate = _all.Checked ? null : DateOnly.FromDateTime(_to.Value),
        AutoSign = _sign.Checked, Format = _pdf.Checked ? "pdf" : "zip", IncludeCover = true,
    };

    private void Preview()
    {
        Ui.Try(() =>
        {
            using var s = _host.Open();
            var p = s.Exam.Preview(Request());
            var lines = new List<string>
            {
                $"{p.Reports} Berichte im Zeitraum.",
                p.Unsigned == 0 ? "Alle sind unterschrieben." : $"{p.Unsigned} sind noch nicht unterschrieben" + (p.HasDefaultSignature ? "." : " – es ist keine Standard-Unterschrift gespeichert (Einstellungen)."),
                p.MissingWeeks == 0 ? "Es fehlt keine Woche." : $"Achtung: {p.MissingWeeks} Wochen haben noch keinen Bericht (Extras > Wochenübersicht).",
            };
            if (_pdf.Checked && !p.PdfAvailable) lines.Add("PDF-Export braucht Microsoft Word oder LibreOffice (libreoffice.org). Sonst bitte ZIP wählen.");
            _info.Text = string.Join("\n\n", lines);
        });
    }

    private void Export()
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            Ui.Try(() =>
            {
                using var s = _host.Open();
                var (data, name, mime) = s.Exam.Export(Request());
                Cursor = Cursors.Default;
                var ext = mime.Contains("pdf") ? "PDF-Datei (*.pdf)|*.pdf" : "ZIP-Archiv (*.zip)|*.zip";
                if (Ui.SaveBytes(this, data, name, ext)) DialogResult = DialogResult.OK;
            });
        }
        finally { Cursor = Cursors.Default; }
    }
}
