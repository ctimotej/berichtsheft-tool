using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Hosting;

namespace Berichtsheft.Desktop;

/// <summary>Plain drawing area: draw with mouse/pen, read back as PNG.</summary>
internal sealed class SignaturePad : Control
{
    private readonly List<List<Point>> _strokes = new();
    private List<Point>? _current;
    public bool IsEmpty => _strokes.Count == 0;

    public SignaturePad()
    {
        BackColor = Color.White;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
    }

    public void Clear() { _strokes.Clear(); Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _current = new List<Point> { e.Location };
        _strokes.Add(_current);
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_current == null) return;
        _current.Add(e.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e) { _current = null; Capture = false; }

    private void Draw(Graphics g, float scale)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.Black, 2.2f * scale) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        foreach (var s in _strokes)
        {
            if (s.Count == 1) g.FillEllipse(Brushes.Black, s[0].X * scale - pen.Width / 2, s[0].Y * scale - pen.Width / 2, pen.Width, pen.Width);
            else g.DrawLines(pen, s.Select(p => new PointF(p.X * scale, p.Y * scale)).ToArray());
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var y = Height * 0.75f;
        using (var baseline = new Pen(Color.Silver)) e.Graphics.DrawLine(baseline, 12, y, Width - 12, y);
        Draw(e.Graphics, 1f);
        ControlPaint.DrawBorder3D(e.Graphics, ClientRectangle, Border3DStyle.Sunken);
    }

    /// <summary>PNG of the drawing at 2x resolution on white (the signature module crops and thresholds it).</summary>
    public byte[] ToPng()
    {
        using var bmp = new Bitmap(Width * 2, Height * 2);
        using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.White); Draw(g, 2f); }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}

/// <summary>Asks for a signature: draw, load from a file, or use the saved default. The result is a
/// <see cref="SignRequest"/> that is applied to one or many reports.</summary>
internal sealed class SignForm : Form
{
    private readonly SignaturePad _pad = new() { Dock = DockStyle.Fill };
    private readonly RadioButton _useDefault = new() { Text = "Gespeicherte Unterschrift verwenden", AutoSize = true };
    private readonly RadioButton _useNew = new() { Text = "Neu unterschreiben:", AutoSize = true, Checked = true };
    private readonly CheckBox _saveDefault = new() { Text = "Als Standard-Unterschrift speichern", AutoSize = true, Checked = true };
    private byte[]? _fileImage;
    private readonly Label _fileInfo = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    public SignRequest? Result { get; private set; }

    public SignForm(bool hasDefault, string heading, bool showSaveDefault = true)
    {
        Ui.Dialog(this, "Unterschreiben", 520, 340);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(10, 10, 10, 0), WrapContents = false };
        top.Controls.Add(new Label { Text = heading, AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        if (hasDefault)
        {
            _useDefault.Checked = true;
            _useNew.Checked = false;
            top.Controls.Add(_useDefault);
        }
        top.Controls.Add(_useNew);

        var padBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 10, 0) };
        padBox.Controls.Add(_pad);

        var side = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10, 6, 10, 0), FlowDirection = FlowDirection.LeftToRight };
        side.Controls.Add(Ui.Button("Löschen", (_, _) => { _pad.Clear(); _fileImage = null; _fileInfo.Text = ""; _useNew.Checked = true; }, 80));
        side.Controls.Add(Ui.Button("Bilddatei…", (_, _) => LoadFile(), 90));
        side.Controls.Add(_fileInfo);

        var save = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10, 0, 10, 0) };
        if (showSaveDefault) save.Controls.Add(_saveDefault);

        var ok = Ui.OkButton(this, (_, _) => Accept(hasDefault));
        var bar = Ui.ButtonBar(DockStyle.Bottom, ok, Ui.CancelButton(this));
        bar.FlowDirection = FlowDirection.RightToLeft;
        bar.Controls.SetChildIndex(ok, 1);

        Controls.Add(padBox);
        Controls.Add(top);
        Controls.Add(side);
        Controls.Add(save);
        Controls.Add(bar);
        _useDefault.CheckedChanged += (_, _) => _pad.Enabled = !_useDefault.Checked;
        _pad.MouseDown += (_, _) => _useNew.Checked = true;
        _pad.Enabled = !hasDefault;
    }

    private void LoadFile()
    {
        using var dlg = new OpenFileDialog { Filter = "Bilder (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (!Ui.Try(() => _fileImage = File.ReadAllBytes(dlg.FileName))) return;
        _fileInfo.Text = Path.GetFileName(dlg.FileName);
        _useNew.Checked = true;
    }

    private void Accept(bool hasDefault)
    {
        if (hasDefault && _useDefault.Checked) Result = new SignRequest { UseDefault = true };
        else
        {
            byte[]? png = _fileImage ?? (_pad.IsEmpty ? null : _pad.ToPng());
            if (png == null) { Ui.Info("Bitte im Feld unterschreiben oder eine Bilddatei wählen."); return; }
            Result = new SignRequest { Image = "data:image/png;base64," + Convert.ToBase64String(png), SaveAsDefault = _saveDefault.Checked };
        }
        DialogResult = DialogResult.OK;
    }

    /// <summary>Signs the reports (asks for the signature first). Returns the number signed.</summary>
    public static int Run(IWin32Window owner, AppHost host, IReadOnlyList<int> ids)
    {
        bool hasDefault;
        using (var s = host.Open()) hasDefault = s.Settings.GetDefaultSignature() != null;
        using var form = new SignForm(hasDefault, ids.Count == 1 ? "Bericht unterschreiben" : $"{ids.Count} Berichte unterschreiben");
        if (form.ShowDialog(owner) != DialogResult.OK || form.Result == null) return 0;
        var done = 0;
        var failures = new List<string>();
        using var session = host.Open();
        foreach (var id in ids)
        {
            try
            {
                var (_, warnings) = session.Reports.Sign(id, form.Result);
                Ui.Warnings(warnings);
                done++;
            }
            catch (Berichtsheft.Core.AppError exc)
            {
                session.Db.ChangeTracker.Clear();
                failures.Add(exc.Message);
            }
        }
        if (failures.Count > 0) Ui.Info(string.Join("\n", failures.Distinct()));
        return done;
    }
}
