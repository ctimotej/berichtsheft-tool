using Berichtsheft.Core.Hosting;
using Berichtsheft.Core.Services;
using Berichtsheft.Core.Signature;

namespace Berichtsheft.Desktop;

/// <summary>The whole window content in Ausbilder mode: drop .pdf/.docx files (or folders) here and get them
/// back signed in a new subfolder "Berichtsheft-signiert-&lt;date&gt;_&lt;time&gt;".</summary>
internal sealed class TrainerPanel : UserControl
{
    private readonly AppHost _host;
    private readonly Action<string> _status;
    private readonly DataGridView _grid = Ui.Grid(multiSelect: true);
    private readonly Label _drop = new()
    {
        Text = "PDF- oder Word-Dateien hierher ziehen\n(auch ganze Ordner) – sie werden unterschrieben und in einem neuen Unterordner abgelegt.",
        TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top, Height = 120, Font = new Font("Segoe UI", 11f), Margin = new Padding(12),
    };
    private readonly Label _sigState = new() { AutoSize = true, Margin = new Padding(6, 7, 12, 0) };
    private readonly CheckBox _alsoPdf = new() { Text = "Word-Dateien zusätzlich als PDF ablegen", AutoSize = true, Margin = new Padding(6, 5, 0, 0) };
    private string? _lastFolder;
    private bool _dragOver;

    public TrainerPanel(AppHost host, Action<string> setStatus)
    {
        _host = host;
        _status = setStatus;
        Dock = DockStyle.Fill;

        _drop.Paint += (_, e) =>
        {
            using var pen = new Pen(_dragOver ? SystemColors.Highlight : SystemColors.ControlDark, 2f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            e.Graphics.DrawRectangle(pen, 6, 6, _drop.Width - 13, _drop.Height - 13);
        };
        _drop.BackColor = SystemColors.ControlLightLight;

        _grid.Columns.Add(Ui.Col("Datei", 34)); _grid.Columns.Add(Ui.Col("Ergebnis", 36)); _grid.Columns.Add(Ui.Col("Gespeichert in", 30));
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].Tag is string file) Ui.Try(() => Ui.OpenFile(file)); };

        var bar = Ui.ButtonBar(DockStyle.Bottom,
            Ui.Button("Dateien wählen…", (_, _) => PickFiles(), 120),
            Ui.Button("Unterschrift…", (_, _) => SetSignature(), 100),
            Ui.Button("Ordner öffnen", (_, _) => OpenFolder(), 100),
            Ui.Button("Liste leeren", (_, _) => _grid.Rows.Clear(), 90));
        bar.Controls.Add(_alsoPdf);
        bar.Controls.Add(_sigState);

        Controls.Add(_grid);
        Controls.Add(_drop);
        Controls.Add(bar);

        AllowDropOn(this);
        Ui.Try(() =>
        {
            using var s = _host.Open();
            _alsoPdf.Checked = s.Settings.Get().TrainerAlsoPdf;
        });
        _alsoPdf.CheckedChanged += (_, _) => Ui.Try(() =>
        {
            using var s = _host.Open();
            var cfg = s.Settings.Get(); cfg.TrainerAlsoPdf = _alsoPdf.Checked; s.Settings.Update(cfg);
        });
        RefreshSignatureState();
    }

    private void AllowDropOn(Control c)
    {
        c.AllowDrop = true;
        c.DragEnter += OnDragEnter;
        c.DragOver += OnDragEnter;
        c.DragLeave += (_, _) => SetDragOver(false);
        c.DragDrop += OnDrop;
        foreach (Control child in c.Controls) AllowDropOn(child);
    }

    private void SetDragOver(bool on) { if (_dragOver != on) { _dragOver = on; _drop.BackColor = on ? Color.AliceBlue : SystemColors.ControlLightLight; _drop.Invalidate(); } }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var ok = e.Data?.GetDataPresent(DataFormats.FileDrop) == true;
        e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
        SetDragOver(ok);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        SetDragOver(false);
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) Sign(paths);
    }

    private void RefreshSignatureState()
    {
        bool has = false;
        SignatureProblem? problem = null;
        Ui.Try(() => { using var s = _host.Open(); has = s.Settings.GetTrainerSignature() != null; problem = s.Settings.LastSignatureProblem; });
        _sigState.Text = problem != null ? (problem.Deleted ? "Unterschrift: nicht lesbar und gelöscht – bitte neu festlegen" : $"Unterschrift: nicht lesbar (Versuch {problem.Attempts} von {problem.MaxAttempts}) – bitte neu festlegen")
            : has ? "Unterschrift: gespeichert" : "Unterschrift: noch nicht festgelegt";
        _sigState.ForeColor = has ? SystemColors.ControlText : Color.Firebrick;
    }

    /// <summary>Draw or load the Ausbilder's signature; it is saved once and used for every file.</summary>
    public bool SetSignature()
    {
        using var form = new SignForm(false, "Unterschrift des Ausbilders (wird gespeichert)", showSaveDefault: false);
        if (form.ShowDialog(this) != DialogResult.OK || form.Result?.Image == null) return false;
        var ok = Ui.Try(() =>
        {
            using var s = _host.Open();
            s.Settings.SetTrainerSignature(ImageSignature.DecodeDataUrl(form.Result.Image));
        });
        RefreshSignatureState();
        return ok;
    }

    public void PickFiles()
    {
        using var dlg = new OpenFileDialog { Filter = "Berichtshefte (*.pdf;*.docx)|*.pdf;*.docx", Multiselect = true, Title = "Berichtshefte zum Unterschreiben wählen" };
        if (dlg.ShowDialog(this) == DialogResult.OK) Sign(dlg.FileNames);
    }

    private void Sign(string[] paths)
    {
        byte[]? sig = null;
        Ui.Try(() => { using var s = _host.Open(); sig = s.Settings.GetTrainerSignature(); });
        if (sig == null)
        {
            Ui.Info("Bitte zuerst deine Unterschrift festlegen – sie wird einmal gespeichert und dann für alle Dateien verwendet.");
            if (!SetSignature()) return;
            Ui.Try(() => { using var s = _host.Open(); sig = s.Settings.GetTrainerSignature(); });
            if (sig == null) return;
        }
        var files = TrainerBatch.Expand(paths);
        if (files.Count == 0) { Ui.Info("Keine PDF- oder Word-Dateien gefunden."); return; }

        List<TrainerFileResult> results = new();
        var old = Cursor.Current; Cursor.Current = Cursors.WaitCursor;
        try
        {
            Ui.Try(() => results = TrainerBatch.Process(files, sig!, DateOnly.FromDateTime(DateTime.Today), DateTime.Now, _alsoPdf.Checked, _host.Context.Converter));
        }
        finally { Cursor.Current = old; }

        foreach (var r in results)
        {
            var i = _grid.Rows.Add(Path.GetFileName(r.Source), (r.Ok ? "✔ " : "✘ ") + r.Message, r.Output != null ? Path.GetDirectoryName(r.Output) : "");
            _grid.Rows[i].Tag = r.Output;
            _grid.Rows[i].DefaultCellStyle.ForeColor = r.Ok ? SystemColors.ControlText : Color.Firebrick;
            if (r.Ok) _lastFolder = Path.GetDirectoryName(r.Output);
        }
        if (_grid.Rows.Count > 0) _grid.FirstDisplayedScrollingRowIndex = _grid.Rows.Count - 1;
        var done = results.Count(r => r.Ok);
        _status($"{done} von {results.Count} Dateien unterschrieben" + (_lastFolder != null ? $" – Ordner: {Path.GetFileName(_lastFolder)}" : ""));
    }

    private void OpenFolder()
    {
        if (_lastFolder != null && Directory.Exists(_lastFolder)) Ui.Try(() => Ui.OpenFile(_lastFolder));
        else Ui.Info("Noch nichts unterschrieben.");
    }
}
