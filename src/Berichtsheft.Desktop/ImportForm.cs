using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Hosting;
using Berichtsheft.Core.Templating;

namespace Berichtsheft.Desktop;

/// <summary>Type in or edit one week that was written by hand (also used to fix what was read from a Word file).</summary>
internal sealed class WeekEntryForm : Form
{
    private readonly ImportWeek _week;
    private readonly DateTimePicker _day = new() { Format = DateTimePickerFormat.Short, ShowCheckBox = true, Width = 120 };
    private readonly Label _range = new() { AutoSize = true, Margin = new Padding(8, 6, 0, 0) };
    private readonly TextBox _number = new() { Width = 60 };
    private readonly ComboBox _dept = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 260 };
    private readonly ComboBox[] _types = Enumerable.Range(0, 5).Select(_ => new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 }).ToArray();
    private readonly TextBox[] _texts = Enumerable.Range(0, 5).Select(_ => new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Height = 62, Dock = DockStyle.Fill }).ToArray();
    private readonly TextBox _remarks = new() { Multiline = true, Height = 40, Dock = DockStyle.Fill };

    public WeekEntryForm(ImportWeek week, IEnumerable<string> unitNames)
    {
        _week = week;
        Ui.Dialog(this, "Woche erfassen", 640, 690, resizable: true);
        _dept.Items.AddRange(unitNames.Cast<object>().ToArray());

        var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 0) };
        head.Controls.AddRange(new Control[] { Ui.Label("Woche (beliebiger Tag):"), _day, _range, Ui.Label("Nr.:"), _number, Ui.Label("Abteilung:"), _dept });

        var hint = new Label
        {
            Text = "Ein Eintrag pro Zeile. Stunden sind optional, hinter einem senkrechten Strich:  Ticket bearbeitet | 2,5\nOhne Stunden werden sie automatisch auf den Tag verteilt.",
            AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(8, 6, 8, 0), ForeColor = SystemColors.GrayText,
        };

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Padding = new Padding(8) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
            _types[i].Items.AddRange(Ui.DayTypeNames);
            var left = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            left.Controls.Add(new Label { Text = Scheduling_WeekdayName(i), AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
            left.Controls.Add(_types[i]);
            grid.Controls.Add(left, 0, i);
            grid.Controls.Add(_texts[i], 1, i);
        }
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        grid.Controls.Add(new Label { Text = "Bemerkungen", AutoSize = true }, 0, 5);
        grid.Controls.Add(_remarks, 1, 5);

        Controls.Add(grid);
        Controls.Add(hint);
        Controls.Add(head);
        Controls.Add(Ui.ButtonBar(DockStyle.Bottom, Ui.OkButton(this, (_, _) => Accept()), Ui.CancelButton(this)));

        // fill from the week
        if (week.StartDate is { } d) _day.Value = d.ToDateTime(TimeOnly.MinValue); else { _day.Value = DateTime.Today; _day.Checked = false; }
        _number.Text = week.ReportNumber; _dept.Text = week.DepartmentLabel; _remarks.Text = week.Remarks;
        for (var i = 0; i < 5; i++)
        {
            var day = week.Days.FirstOrDefault(x => x.Weekday == i) ?? new ImportDay { Weekday = i };
            _types[i].SelectedIndex = Math.Max(0, Array.IndexOf(Ui.DayTypeKeys, day.Type));
            _texts[i].Text = string.Join("\r\n", day.Entries.Select(e => e.Hours is { } h ? $"{e.Text} | {h.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("de-DE"))}" : e.Text));
        }
        _day.ValueChanged += (_, _) => UpdateRange();
        UpdateRange();
    }

    private static string Scheduling_WeekdayName(int i) => new[] { "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag" }[i];

    private void UpdateRange()
    {
        if (!_day.Checked) { _range.Text = "(Datum fehlt)"; return; }
        var monday = Berichtsheft.Core.Generation.Scheduling.WeekBounds(DateOnly.FromDateTime(_day.Value)).Monday;
        _range.Text = $"KW {System.Globalization.ISOWeek.GetWeekOfYear(monday.ToDateTime(TimeOnly.MinValue))}: {monday:dd.MM.} – {monday.AddDays(4):dd.MM.yyyy}";
    }

    private void Accept()
    {
        if (!_day.Checked) { Ui.Info("Bitte das Datum der Woche angeben (Häkchen setzen)."); return; }
        _week.StartDate = DateOnly.FromDateTime(_day.Value);
        _week.ReportNumber = _number.Text.Trim(); _week.DepartmentLabel = _dept.Text.Trim(); _week.Remarks = _remarks.Text.Trim();
        _week.Days = Enumerable.Range(0, 5).Select(i => new ImportDay
        {
            Weekday = i, Type = Ui.DayTypeKeys[Math.Max(0, _types[i].SelectedIndex)], Entries = ParseLines(_texts[i].Text),
        }).ToList();
        _week.Warnings.Clear();
        DialogResult = DialogResult.OK;
    }

    /// <summary>"Text | 2,5" per line; a part after the last | that is no number stays part of the text.</summary>
    internal static List<Berichtsheft.Core.Models.ReportEntry> ParseLines(string text)
    {
        var list = new List<Berichtsheft.Core.Models.ReportEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            double? hours = null;
            var bar = line.LastIndexOf('|');
            if (bar >= 0 && DocxReportReader.ParseHours(line[(bar + 1)..]) is { } h) { hours = h; line = line[..bar].Trim(); }
            if (line.Length > 0) list.Add(new Berichtsheft.Core.Models.ReportEntry { Text = line, Hours = hours });
        }
        return list;
    }
}

/// <summary>Brings weeks that were written by hand into the program (from Word files or typed in), so they get
/// the same features as generated reports: signing, PDF, bulk export, coverage.</summary>
internal sealed class ImportForm : Form
{
    private readonly AppHost _host;
    private readonly DataGridView _grid = Ui.Grid(multiSelect: true);
    private readonly List<ImportWeek> _weeks = new();
    private readonly Label _summary = new() { AutoSize = true, Margin = new Padding(12, 8, 0, 0) };
    private readonly Button _import;
    public int Imported { get; private set; }

    public ImportForm(AppHost host)
    {
        _host = host;
        Ui.Dialog(this, "Eigene Berichte importieren", 820, 520, resizable: true);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        top.Controls.Add(new Label
        {
            Text = "Bereits von Hand geschriebene Wochen übernehmen – aus Word-Dateien im Nachweis-Formular oder direkt eingetippt.",
            AutoSize = true, Margin = new Padding(0, 0, 0, 6),
        });
        top.SetFlowBreak(top.Controls[0], true);
        top.Controls.Add(Ui.Button("Word-Dateien…", (_, _) => AddFiles(), 110));
        top.Controls.Add(Ui.Button("Woche eintippen…", (_, _) => AddManual(), 120));
        top.Controls.Add(Ui.Button("Bearbeiten…", (_, _) => EditSelected(), 90));
        top.Controls.Add(Ui.Button("Entfernen", (_, _) => RemoveSelected(), 80));

        _grid.Columns.Add(Ui.Col("KW", 9)); _grid.Columns.Add(Ui.Col("Zeitraum", 20)); _grid.Columns.Add(Ui.Col("Nr.", 7));
        _grid.Columns.Add(Ui.Col("Abteilung", 20)); _grid.Columns.Add(Ui.Col("Einträge", 9)); _grid.Columns.Add(Ui.Col("Status", 35));
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditSelected(); };

        _import = Ui.Button("Importieren", (_, _) => Run(), 100);
        _import.Enabled = false;
        var bar = Ui.ButtonBar(DockStyle.Bottom, _import, Ui.CancelButton(this, "Schließen"));
        bar.Controls.Add(_summary);
        Controls.Add(_grid);
        Controls.Add(top);
        Controls.Add(bar);
    }

    private IEnumerable<string> UnitNames()
    {
        using var s = _host.Open();
        return s.Units.List(onlyActive: true).Select(u => u.Name).ToList();
    }

    private void AddFiles()
    {
        using var dlg = new OpenFileDialog { Filter = "Word-Dokumente (*.docx)|*.docx", Multiselect = true, Title = "Ausgefüllte Ausbildungsnachweise wählen" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var problems = new List<string>();
        foreach (var file in dlg.FileNames.OrderBy(f => f))
        {
            try
            {
                var w = DocxReportReader.Read(File.ReadAllBytes(file), Path.GetFileName(file));
                if (w.Warnings.Count > 0) problems.Add($"{w.Source}: {string.Join(" ", w.Warnings)}");
                _weeks.Add(w);
            }
            catch (Exception exc) when (exc is IOException or InvalidDataException or Berichtsheft.Core.AppError or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
            {
                problems.Add($"{Path.GetFileName(file)}: konnte nicht gelesen werden ({exc.Message})");
            }
        }
        Refresh_();
        if (problems.Count > 0)
            Ui.Info("Bitte kontrollieren (Doppelklick auf die Zeile zum Korrigieren):\n\n" + string.Join("\n", problems.Take(15)) + (problems.Count > 15 ? "\n…" : ""));
    }

    private void AddManual()
    {
        var w = ImportWeek.Blank();
        using var f = new WeekEntryForm(w, UnitNames());
        if (f.ShowDialog(this) != DialogResult.OK) return;
        _weeks.Add(w);
        Refresh_();
    }

    private void EditSelected()
    {
        if (_grid.CurrentRow?.Tag is not ImportWeek w) return;
        using var f = new WeekEntryForm(w, UnitNames());
        if (f.ShowDialog(this) == DialogResult.OK) Refresh_();
    }

    private void RemoveSelected()
    {
        foreach (var w in _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<ImportWeek>().ToList()) _weeks.Remove(w);
        Refresh_();
    }

    /// <summary>Status per week: what will happen on "Importieren".</summary>
    private string StatusOf(Berichtsheft.Core.Hosting.AppHost.Session s, ImportWeek w, HashSet<DateOnly> seen)
    {
        if (w.StartDate is not { } d) return "Datum fehlt – Doppelklick zum Eintragen";
        if (w.EntryCount == 0) return "keine Einträge – Doppelklick zum Eintragen";
        var monday = Berichtsheft.Core.Generation.Scheduling.WeekBounds(d).Monday;
        if (s.Reports.WeekInfo(monday).ExistingReportId != null) return "Bericht vorhanden – wird übersprungen";
        if (!seen.Add(monday)) return "Woche doppelt in der Liste – wird übersprungen";
        return w.Warnings.Count > 0 ? "bitte kontrollieren: " + w.Warnings[0] : "wird importiert";
    }

    private static bool WillImport(string status) => status.StartsWith("wird importiert") || status.StartsWith("bitte kontrollieren");

    private void Refresh_()
    {
        _grid.Rows.Clear();
        var ok = 0;
        Ui.Try(() =>
        {
            using var s = _host.Open();
            var seen = new HashSet<DateOnly>();
            foreach (var w in _weeks.OrderBy(x => x.StartDate ?? DateOnly.MaxValue))
            {
                var status = StatusOf(s, w, seen);
                if (WillImport(status)) ok++;
                var monday = w.StartDate is { } d ? Berichtsheft.Core.Generation.Scheduling.WeekBounds(d).Monday : (DateOnly?)null;
                var i = _grid.Rows.Add(monday is { } m ? System.Globalization.ISOWeek.GetWeekOfYear(m.ToDateTime(TimeOnly.MinValue)).ToString() : "?",
                    monday is { } m2 ? $"{m2:dd.MM.} – {m2.AddDays(4):dd.MM.yyyy}" : "?", w.ReportNumber, w.DepartmentLabel, w.EntryCount, status);
                _grid.Rows[i].Tag = w;
            }
        });
        _summary.Text = $"{_weeks.Count} Wochen, davon {ok} importierbar";
        _import.Enabled = ok > 0;
    }

    private void Run()
    {
        var imported = new List<ImportWeek>();
        var failures = new List<string>();
        Ui.Try(() =>
        {
            using var s = _host.Open();
            var seen = new HashSet<DateOnly>();
            foreach (var w in _weeks.OrderBy(x => x.StartDate ?? DateOnly.MaxValue).ToList())
            {
                if (!WillImport(StatusOf(s, w, seen))) continue;
                try { s.Reports.ImportWeek(w); imported.Add(w); }
                catch (Berichtsheft.Core.AppError exc) { s.Db.ChangeTracker.Clear(); failures.Add($"{(w.Source.Length > 0 ? w.Source : w.StartDate?.ToString("dd.MM.yyyy"))}: {exc.Message}"); }
            }
        });
        foreach (var w in imported) _weeks.Remove(w);
        Imported += imported.Count;
        Refresh_();
        Ui.Info($"{imported.Count} Wochen importiert." + (failures.Count > 0 ? "\n\nNicht importiert:\n" + string.Join("\n", failures.Take(15)) : "")
            + (imported.Count > 0 ? "\n\nDie Berichte sind jetzt in der Liste und können wie alle anderen unterschrieben und als PDF oder Word exportiert werden (auch mehrere auf einmal)." : ""));
    }
}
