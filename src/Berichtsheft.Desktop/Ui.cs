using System.Diagnostics;
using Berichtsheft.Core;
using Berichtsheft.Core.Hosting;

namespace Berichtsheft.Desktop;

/// <summary>Small helpers shared by all windows: error handling, plain layout building blocks.</summary>
internal static class Ui
{
    public const string Title = "Berichtsheft Generator";
    private static Icon? _appIcon;
    /// <summary>The icon embedded in the .exe (null when it cannot be read).</summary>
    public static Icon? AppIcon
    {
        get
        {
            if (_appIcon == null && Environment.ProcessPath is { } exe)
            {
                try { _appIcon = Icon.ExtractAssociatedIcon(exe); } catch (Exception) { }
            }
            return _appIcon;
        }
    }
    public const string Copyright = "© 2026 Tim Kraemer";

    public static readonly string[] DayTypeKeys = { "work", "school", "vacation", "holiday", "sick", "free" };
    public static readonly string[] DayTypeNames = { "Betrieb", "Berufsschule", "Urlaub", "Feiertag", "Krank", "Frei" };
    public static string DayTypeName(string key) => Array.IndexOf(DayTypeKeys, key) is var i and >= 0 ? DayTypeNames[i] : key;

    public static string StatusName(string s) => s switch
    {
        "draft" => "Entwurf", "exported" => "Erzeugt", "signed" => "Unterschrieben", "missing" => "Fehlt", "current" => "Laufende Woche", _ => s,
    };

    private static readonly string[] WeekdayNames = { "So", "Mo", "Di", "Mi", "Do", "Fr", "Sa" }; // invariant globalization: no de-DE culture data
    public static string Day(DateOnly d) => $"{WeekdayNames[(int)d.DayOfWeek]} {d:dd.MM.}";
    public static string Date(DateOnly d) => d.ToString("dd.MM.yyyy");

    // ------------------------------------------------------------ errors
    public static void ShowError(Exception exc)
    {
        var msg = exc is AppError ? exc.Message : "Ein unerwarteter Fehler ist aufgetreten:\n\n" + exc.Message + "\n\nDetails stehen in data\\logs\\app.log.";
        MessageBox.Show(msg, Title, MessageBoxButtons.OK, exc is AppError ? MessageBoxIcon.Warning : MessageBoxIcon.Error);
    }

    /// <summary>Runs a user action; domain errors and unexpected errors are shown in a message box.</summary>
    public static bool Try(Action action)
    {
        try { action(); return true; }
        catch (Exception exc) when (exc is not OutOfMemoryException) { ShowError(exc); return false; }
    }

    public static bool Confirm(string text) =>
        MessageBox.Show(text, Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    public static void Info(string text) => MessageBox.Show(text, Title, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static void Warnings(IEnumerable<string> warnings)
    {
        var list = warnings.ToList();
        if (list.Count > 0) MessageBox.Show(string.Join("\n\n", list), Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>Opens a file in the associated program (Word for .docx).</summary>
    public static void OpenFile(string path)
    {
        Try(() => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
    }

    /// <summary>Writes the document to a temp file and opens it. The report stays owned by this program:
    /// changes made in Word are not read back.</summary>
    public static void OpenBytes(byte[] data, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Berichtsheft");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, data);
        OpenFile(path);
    }

    public static bool SaveBytes(IWin32Window owner, byte[] data, string name, string filter)
    {
        using var dlg = new SaveFileDialog { FileName = name, Filter = filter, OverwritePrompt = true };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return false;
        return Try(() => File.WriteAllBytes(dlg.FileName, data));
    }

    // ------------------------------------------------------------ layout
    public static Label Label(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 6) };

    /// <summary>Two-column form: label left, control right. Returns the panel to be docked into a form.</summary>
    public static TableLayoutPanel Form(params (string Label, Control Control)[] rows)
    {
        var t = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (label, control) in rows)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(Label(label));
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 3, 0, 3);
            t.Controls.Add(control);
        }
        return t;
    }

    public static Button Button(string text, EventHandler onClick, int width = 100)
    {
        var b = new Button { Text = text, Width = width, Height = 26, Margin = new Padding(0, 0, 6, 0) };
        b.Click += onClick;
        return b;
    }

    /// <summary>Row of buttons at the bottom of a window; OK/Cancel buttons are right aligned.</summary>
    public static FlowLayoutPanel ButtonBar(DockStyle dock = DockStyle.Bottom, params Button[] buttons)
    {
        var p = new FlowLayoutPanel { Dock = dock, AutoSize = true, Padding = new Padding(8), FlowDirection = FlowDirection.LeftToRight };
        p.Controls.AddRange(buttons);
        return p;
    }

    public static DataGridView Grid(bool multiSelect = false)
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = multiSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.Fixed3D,
            EnableHeadersVisualStyles = true,
        };
        return g;
    }

    public static DataGridViewTextBoxColumn Col(string header, int weight = 100, bool readOnly = true) =>
        new() { HeaderText = header, FillWeight = weight, ReadOnly = readOnly, SortMode = DataGridViewColumnSortMode.NotSortable };

    /// <summary>Base window settings: fixed dialog or resizable tool window, centered on the owner.</summary>
    public static void Dialog(Form f, string title, int w, int h, bool resizable = false)
    {
        f.Text = title;
        if (AppIcon != null) f.Icon = AppIcon;
        f.ClientSize = new Size(w, h);
        f.StartPosition = FormStartPosition.CenterParent;
        f.ShowInTaskbar = false;
        f.MinimizeBox = false;
        f.MaximizeBox = resizable;
        f.FormBorderStyle = resizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedDialog;
    }

    public static Button OkButton(Form f, EventHandler? onClick = null, string text = "OK")
    {
        var b = new Button { Text = text, Width = 90, Height = 26, Margin = new Padding(0, 0, 6, 0) };
        b.Click += onClick ?? ((_, _) => f.DialogResult = DialogResult.OK);
        f.AcceptButton = b;
        return b;
    }

    public static Button CancelButton(Form f, string text = "Abbrechen")
    {
        var b = new Button { Text = text, Width = 90, Height = 26, DialogResult = DialogResult.Cancel };
        f.CancelButton = b;
        return b;
    }
}

/// <summary>Modal input dialog around a ready-made content panel.</summary>
internal sealed class EditDialog : Form
{
    private readonly Func<bool> _accept;

    public EditDialog(string title, int width, Control content, Func<bool> accept)
    {
        _accept = accept;
        Ui.Dialog(this, title, width, 100, resizable: false);
        content.Dock = DockStyle.Top;
        Controls.Add(content);
        Controls.Add(Ui.ButtonBar(DockStyle.Bottom, Ui.OkButton(this, (_, _) => { if (_accept()) DialogResult = DialogResult.OK; }), Ui.CancelButton(this)));
        // size to content once it is laid out
        Load += (_, _) => ClientSize = new Size(width, content.PreferredSize.Height + 50);
    }
}
