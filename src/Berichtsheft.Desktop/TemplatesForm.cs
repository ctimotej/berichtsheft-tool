using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Hosting;

namespace Berichtsheft.Desktop;

/// <summary>The Word templates the reports are filled into. One is active.</summary>
internal sealed class TemplatesForm : Form
{
    private readonly AppHost _host;
    private readonly DataGridView _grid = Ui.Grid();
    private readonly Label _detail = new() { Dock = DockStyle.Bottom, Height = 90, Padding = new Padding(8), AutoEllipsis = true };

    public TemplatesForm(AppHost host)
    {
        _host = host;
        Ui.Dialog(this, "Vorlagen", 700, 460, resizable: true);
        _grid.Columns.Add(Ui.Col("Name", 40));
        _grid.Columns.Add(Ui.Col("Datei", 30));
        _grid.Columns.Add(Ui.Col("Zeichen/Zeile", 14));
        _grid.Columns.Add(Ui.Col("Aktiv", 10));
        _grid.SelectionChanged += (_, _) => ShowDetail();
        Controls.Add(_grid);
        Controls.Add(_detail);
        Controls.Add(Ui.ButtonBar(DockStyle.Bottom, Ui.Button("Hinzufügen…", (_, _) => Add(), 100), Ui.Button("Aktivieren", (_, _) => ActivateSelected()),
            Ui.Button("Zeichen/Zeile…", (_, _) => LineChars(), 110), Ui.Button("Speichern unter…", (_, _) => Download(), 110),
            Ui.Button("Löschen", (_, _) => Delete()), Ui.CancelButton(this, "Schließen")));
        Reload();
    }

    private TemplateOut? Selected => _grid.CurrentRow?.Tag as TemplateOut;

    private void Reload()
    {
        var keep = Selected?.Id;
        _grid.Rows.Clear();
        Ui.Try(() =>
        {
            using var s = _host.Open();
            foreach (var t in s.Templates.List())
            {
                var i = _grid.Rows.Add(t.Name, t.OriginalFilename, t.LineChars, t.IsActive ? "ja" : "");
                _grid.Rows[i].Tag = t;
                if (t.Id == keep) _grid.CurrentCell = _grid.Rows[i].Cells[0];
            }
        });
        ShowDetail();
    }

    private void ShowDetail()
    {
        var t = Selected;
        _detail.Text = t == null ? "Noch keine Vorlage. Mit „Hinzufügen“ die Word-Vorlage (.docx) mit {{PLATZHALTERN}} wählen."
            : $"Platzhalter: {(t.Placeholders.Count > 0 ? string.Join(", ", t.Placeholders) : "keine gefunden")}"
              + (t.UnknownPlaceholders.Count > 0 ? $"\nUnbekannt (bleiben leer): {string.Join(", ", t.UnknownPlaceholders)}" : "");
    }

    private void Add()
    {
        using var dlg = new OpenFileDialog { Filter = "Word-Vorlage (*.docx;*.doc)|*.docx;*.doc" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Ui.Try(() =>
        {
            using var s = _host.Open();
            var t = s.Templates.Upload(File.ReadAllBytes(dlg.FileName), Path.GetFileName(dlg.FileName), activate: true);
            var info = TemplateServiceOut(t);
            if (info.Placeholders.Count == 0)
                Ui.Info("In dieser Datei wurden keine {{PLATZHALTER}} gefunden. Ein Formular ohne Platzhalter lässt sich mit\nBerichtsheft.exe --prepare-template <Eingabe.docx> <Ausgabe.docx>\nvorbereiten (siehe docs\\TEMPLATES.md).");
        });
        Reload();
    }

    private static TemplateOut TemplateServiceOut(Berichtsheft.Core.Data.TemplateEntity t) => Berichtsheft.Core.Services.TemplateService.ToOut(t);

    private void ActivateSelected()
    {
        if (Selected is not { } t) return;
        Ui.Try(() => { using var s = _host.Open(); s.Templates.Activate(t.Id); });
        Reload();
    }

    private void LineChars()
    {
        if (Selected is not { } t) return;
        var num = new NumericUpDown { Minimum = 20, Maximum = 400, Value = Math.Clamp(t.LineChars, 20, 400), Width = 80 };
        var hint = new Label { Text = "Wie viele Zeichen in eine Zeile des Formulars passen. Längere Einträge laufen in die nächste Zeile.", AutoSize = true, MaximumSize = new Size(340, 0) };
        var content = Ui.Form(("Zeichen pro Zeile:", num), ("", hint));
        using var dlg = new EditDialog("Zeichen pro Zeile", 420, content, () => Ui.Try(() =>
        {
            using var s = _host.Open();
            s.Templates.Patch(t.Id, null, (int)num.Value);
        }));
        if (dlg.ShowDialog(this) == DialogResult.OK) Reload();
    }

    private void Download()
    {
        if (Selected is not { } t) return;
        Ui.Try(() =>
        {
            using var s = _host.Open();
            var (data, name) = s.Templates.ReadBytes(t.Id);
            Ui.SaveBytes(this, data, name, "Word-Dokument (*.docx)|*.docx");
        });
    }

    private void Delete()
    {
        if (Selected is not { } t || !Ui.Confirm($"Vorlage „{t.Name}“ löschen? Bereits erzeugte Berichte bleiben erhalten.")) return;
        Ui.Try(() => { using var s = _host.Open(); s.Templates.Delete(t.Id); });
        Reload();
    }
}
