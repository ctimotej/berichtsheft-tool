using Berichtsheft.Core;
using System.Runtime.InteropServices;
using Berichtsheft.Core.Dtos;
using Berichtsheft.Core.Generation;
using Berichtsheft.Core.Hosting;
using Berichtsheft.Core.Services;

namespace Berichtsheft.Desktop;

/// <summary>Main window: the list of reports plus the menu to everything else.</summary>
internal sealed class MainForm : Form
{
    private readonly AppHost _host;
    private readonly DataGridView _grid = Ui.Grid(multiSelect: true);
    private readonly ToolStripStatusLabel _status = new();

    // ------------------------------------------------------------ systray + global hotkey (Ctrl+Shift+B)
    private const int WmHotkey = 0x0312, HotkeyId = 0x4247, ModControl = 0x2, ModShift = 0x4;
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    private NotifyIcon? _tray;
    private bool _hotkeyOn, _exiting, _startHidden;
    private readonly System.Windows.Forms.Timer _reminderTimer = new() { Interval = 30 * 60 * 1000 };
    private DateOnly? _reminderWeek; // week to open when the reminder balloon is clicked

    public MainForm(AppHost host, bool startHidden = false)
    {
        _host = host;
        _startHidden = startHidden;
        Text = Ui.Title;
        if (Ui.AppIcon != null) Icon = Ui.AppIcon;
        ClientSize = new Size(820, 480);
        MinimumSize = new Size(640, 360);
        StartPosition = FormStartPosition.CenterScreen;

        _statusBar.Items.Add(_status);
        ApplyMode();
        Shown += (_, _) => FirstStart();
        _reminderTimer.Tick += (_, _) => Ui.Try(CheckReminder);
    }

    // ------------------------------------------------------------ modes: Azubi (write reports) / Ausbilder (sign files)
    private readonly StatusStrip _statusBar = new();
    private TrainerPanel? _trainerPanel;
    private bool _trainer;          // current mode of this session - never saved, every start is the normal mode
    private bool _trainerAllowed;   // "Ausbilder-Modus anbieten" in the settings

    /// <summary>Rebuilds menu and content for the mode saved in the settings.</summary>
    private void ApplyMode()
    {
        using (var s = _host.Open()) _trainerAllowed = s.Settings.Get().TrainerModeEnabled;
        if (!_trainerAllowed) _trainer = false; // switched off in the settings while in that mode
        SuspendLayout();
        foreach (Control c in Controls.OfType<Control>().ToList()) { Controls.Remove(c); if (c != _grid && c != _statusBar) c.Dispose(); }
        _trainerPanel = null;
        _status.Text = "";
        if (_trainer) BuildTrainerUi(); else BuildTraineeUi();
        Text = _trainer ? Ui.Title + " – Ausbilder" : Ui.Title;
        ResumeLayout(true);
        if (_tray != null) _tray.ContextMenuStrip = TrayMenu();
        if (!_trainer && IsHandleCreated) Reload();
    }

    private ToolStripMenuItem ViewMenu()
    {
        var mode = Item("&Ausbilder-Modus", Keys.None, (_, _) => SetMode(!_trainer));
        mode.Checked = _trainer;
        mode.Visible = _trainerAllowed;
        var view = Menu("&Ansicht", mode);
        view.Visible = _trainerAllowed;   // without the opt-in there is no Ansicht menu at all
        return view;
    }

    private ToolStripMenuItem HelpMenu() => Menu("&Hilfe", Item("&Info", Keys.None, (_, _) =>
        Ui.Info($"{Ui.Title} {Program.Version}\n{Ui.Copyright}\n\n" +
                (_trainer ? "Ausbilder-Modus: unterschreibt fertige Berichtshefte (PDF/Word) per Drag & Drop.\n"
                          : "Erstellt Ausbildungsnachweise aus deiner Word-Vorlage.\n") + $"Daten: {_host.Config.DataDir}")));

    private void SetMode(bool trainer)
    {
        if (!_trainerAllowed || trainer == _trainer) return;
        _trainer = trainer;
        ApplyMode();
        if (!_trainer) Reload();
    }

    private void BuildTrainerUi()
    {
        var menu = new MenuStrip();
        menu.Items.Add(Menu("&Datei",
            Item("&Dateien zum Unterschreiben wählen…", Keys.Control | Keys.O, (_, _) => _trainerPanel?.PickFiles()),
            new ToolStripSeparator(),
            Item("&Beenden", Keys.None, (_, _) => Quit())));
        menu.Items.Add(Menu("&Ausbilder", Item("&Unterschrift festlegen…", Keys.None, (_, _) => _trainerPanel?.SetSignature())));
        menu.Items.Add(ViewMenu());
        menu.Items.Add(Menu("&Extras", Item("&Einstellungen…", Keys.None, (_, _) => Show(new SettingsForm(_host, trainerMode: true)))));
        menu.Items.Add(HelpMenu());
        MainMenuStrip = menu;
        _trainerPanel = new TrainerPanel(_host, t => _status.Text = t);
        Controls.Add(_trainerPanel);
        Controls.Add(_statusBar);
        Controls.Add(menu);
    }

    private void BuildTraineeUi()
    {
        var menu = new MenuStrip();
        menu.Items.Add(Menu("&Datei",
            Item("&Neuer Bericht…", Keys.Control | Keys.N, (_, _) => NewReport()),
            Item("&Mehrere Wochen erstellen…", Keys.None, (_, _) => Show(new BulkForm(_host))),
            Item("&Prüfungsexport…", Keys.None, (_, _) => Show(new ExamForm(_host))),
            new ToolStripSeparator(),
            Item("&Eigene Berichte importieren…", Keys.None, (_, _) => Show(new ImportForm(_host))),
            Item("Berichte &unterschreiben und exportieren…", Keys.None, (_, _) => Show(new BulkExportForm(_host))),
            new ToolStripSeparator(),
            Item("&Datensicherung erstellen…", Keys.None, (_, _) => Backup()),
            Item("Datensicherung &wiederherstellen…", Keys.None, (_, _) => Restore()),
            new ToolStripSeparator(),
            Item("&Beenden", Keys.None, (_, _) => Quit())));
        menu.Items.Add(Menu("&Bericht",
            Item("&Bearbeiten", Keys.Enter, (_, _) => EditSelected()),
            Item("In &Word öffnen", Keys.None, (_, _) => OpenInWord()),
            Item("&Speichern unter…", Keys.Control | Keys.S, (_, _) => SaveAs()),
            Item("&Unterschreiben…", Keys.None, (_, _) => Sign()),
            new ToolStripSeparator(),
            Item("&Löschen", Keys.Delete, (_, _) => DeleteSelected())));
        menu.Items.Add(Menu("&Extras",
            Item("&Wochenübersicht…", Keys.None, (_, _) => ShowCoverage()),
            Item("&Planung…", Keys.None, (_, _) => Show(new PlanForm(_host))),
            Item("&Themen und Textbausteine…", Keys.None, (_, _) => Show(new CatalogForm(_host))),
            Item("&Vorlagen…", Keys.None, (_, _) => Show(new TemplatesForm(_host))),
            new ToolStripSeparator(),
            Item("&Einstellungen…", Keys.None, (_, _) => Show(new SettingsForm(_host)))));
        menu.Items.Add(ViewMenu());
        menu.Items.Add(HelpMenu());
        MainMenuStrip = menu;

        if (_grid.Columns.Count == 0)
        {
            _grid.Columns.Add(Ui.Col("Nr.", 8));
            _grid.Columns.Add(Ui.Col("KW", 10));
            _grid.Columns.Add(Ui.Col("Zeitraum", 22));
            _grid.Columns.Add(Ui.Col("Abteilung", 28));
            _grid.Columns.Add(Ui.Col("Std.", 9));
            _grid.Columns.Add(Ui.Col("Status", 18));
            _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditSelected(); };
        }

        var buttons = Ui.ButtonBar(DockStyle.Bottom,
            Ui.Button("Neuer Bericht…", (_, _) => NewReport(), 120),
            Ui.Button("Bearbeiten", (_, _) => EditSelected()),
            Ui.Button("In Word öffnen", (_, _) => OpenInWord(), 110),
            Ui.Button("Unterschreiben…", (_, _) => Sign(), 110),
            Ui.Button("Löschen", (_, _) => DeleteSelected()));

        Controls.Add(_grid);
        Controls.Add(buttons);
        Controls.Add(_statusBar);
        Controls.Add(menu);
    }

    // With --hidden (autostart) the window is never shown at start: only the handle is created and the
    // start-up work runs; the tray icon (or, without tray, the window) takes over from there.
    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden && value)
        {
            _startHidden = false;
            if (!IsHandleCreated) CreateHandle();
            BeginInvoke(() => { FirstStart(); if (_tray == null) ShowMain(); });
            base.SetVisibleCore(false);
            return;
        }
        base.SetVisibleCore(value);
    }

    /// <summary>Called when a second instance is started: bring this window to the front.</summary>
    public void Summon() => ShowMain();

    private static ToolStripMenuItem Menu(string text, params ToolStripItem[] items)
    {
        var m = new ToolStripMenuItem(text);
        m.DropDownItems.AddRange(items);
        return m;
    }

    private static ToolStripMenuItem Item(string text, Keys shortcut, EventHandler click)
    {
        var i = new ToolStripMenuItem(text, null, click);
        if (shortcut != Keys.None && shortcut != Keys.Enter && shortcut != Keys.Delete) i.ShortcutKeys = shortcut;
        return i;
    }

    // Enter and Delete are handled by the grid only, so they do not steal keys from text boxes in other windows
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_grid.Focused && keyData == Keys.Enter) { EditSelected(); return true; }
        if (_grid.Focused && keyData == Keys.Delete) { DeleteSelected(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void FirstStart()
    {
        bool needsSetup;
        using (var s = _host.Open()) needsSetup = s.Settings.Get().TraineeName.Trim().Length == 0;
        ApplyMode();
        if (needsSetup)
        {
            ShowMain();
            Ui.Info("Willkommen! Bitte zuerst deinen Namen, den Ausbildungsbeginn und die Vorlage-Angaben eintragen.");
            Show(new SettingsForm(_host));
        }
        Reload();
        ApplyTray();
    }

    private void Show(Form f)
    {
        using (f) f.ShowDialog(this);
        Reload();
        if (f is SettingsForm) { ApplyTray(); ApplyMode(); }
    }

    private void Quit() { _exiting = true; Close(); }

    /// <summary>Applies the "Systray" setting: tray icon and hotkey on, or both removed again.</summary>
    private void ApplyTray()
    {
        bool enabled;
        using (var s = _host.Open()) enabled = s.Settings.Get().TrayEnabled;
        if (enabled && _tray == null)
        {
            _tray = new NotifyIcon { Icon = Ui.AppIcon ?? SystemIcons.Application, Text = Ui.Title + " (Strg+Umschalt+B)", ContextMenuStrip = TrayMenu(), Visible = true };
            _tray.DoubleClick += (_, _) => ShowMain();
            _tray.BalloonTipClicked += (_, _) => { ShowMain(); if (_reminderWeek is { } w) { _reminderWeek = null; NewReport(w); } };
            _reminderTimer.Start();
            BeginInvoke(() => Ui.Try(CheckReminder));
            _hotkeyOn = RegisterHotKey(Handle, HotkeyId, ModControl | ModShift, (uint)Keys.B);
            if (!_hotkeyOn)
                _tray.ShowBalloonTip(4000, Ui.Title, "Strg+Umschalt+B ist schon von einem anderen Programm belegt. Das Symbol im Infobereich funktioniert trotzdem.", ToolTipIcon.Warning);
        }
        else if (!enabled && _tray != null)
        {
            _reminderTimer.Stop();
            DisposeTray();
            ShowMain(); // never leave the program hidden without a tray icon
        }
    }

    private ContextMenuStrip TrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Berichtsheft Generator öffnen", null, (_, _) => ShowMain());
        if (_trainer) menu.Items.Add("Dateien unterschreiben…", null, (_, _) => { ShowMain(); _trainerPanel?.PickFiles(); });
        else menu.Items.Add("Neuer Bericht…", null, (_, _) => { ShowMain(); NewReport(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => Quit());
        return menu;
    }

    /// <summary>At most one tray reminder per day: missing weeks, or on Friday the current week without a report.</summary>
    private void CheckReminder()
    {
        if (_tray == null) return;
        using var s = _host.Open();
        var cfg = s.Settings.Get();
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (_trainer || !cfg.ReminderEnabled || cfg.ReminderLastDate == today) return;
        var cov = s.Reports.Coverage();
        var missing = cov.Weeks.Where(w => w.Status == "missing").ToList();
        var currentOpen = DateTime.Today.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday or DayOfWeek.Sunday
            ? cov.Weeks.FirstOrDefault(w => w.Status == "current") : null;
        if (missing.Count == 0 && currentOpen == null) return;
        var parts = new List<string>();
        if (currentOpen != null) parts.Add($"Für KW {currentOpen.CalendarWeek} gibt es noch keinen Bericht.");
        if (missing.Count > 0) parts.Add(missing.Count == 1 ? $"KW {missing[0].CalendarWeek}/{missing[0].Year} fehlt noch."
            : $"{missing.Count} frühere Wochen fehlen noch (zuerst KW {missing[0].CalendarWeek}/{missing[0].Year}).");
        _reminderWeek = (missing.Count > 0 ? missing[0].StartDate : currentOpen!.StartDate);
        _tray.ShowBalloonTip(10000, "Berichtsheft: Bericht fehlt", string.Join(" ", parts) + " Klicken zum Erstellen.", ToolTipIcon.Info);
        cfg.ReminderLastDate = today;
        s.Settings.Update(cfg);
    }

    private void DisposeTray()
    {
        _reminderTimer.Stop();
        if (_hotkeyOn) { UnregisterHotKey(Handle, HotkeyId); _hotkeyOn = false; }
        _tray?.Dispose();
        _tray = null;
    }

    /// <summary>Brings the window to the front (also from the tray or the hotkey).</summary>
    private void ShowMain()
    {
        _startHidden = false;
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        TopMost = true; TopMost = false; // Windows only raises the window this way
        Activate();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId) ShowMain();
        else base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_tray != null && !_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; // with the tray active, the X only hides the window
            Hide();
            _tray.ShowBalloonTip(2000, Ui.Title, "Läuft weiter im Infobereich. Strg+Umschalt+B öffnet das Programm wieder.", ToolTipIcon.Info);
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        DisposeTray();
        base.OnFormClosed(e);
    }

    // ------------------------------------------------------------ list
    private List<ReportSummary> _rows = new();

    private void Reload(int? select = null)
    {
        if (_trainer) return;
        var keep = select != null ? new List<int> { select.Value } : SelectedIds();
        Ui.Try(() =>
        {
            using var s = _host.Open();
            _rows = s.Reports.List(1000);
            var cov = s.Reports.Coverage();
            _status.Text = $"{_rows.Count} Berichte" + (cov.Missing > 0 ? $" – {cov.Missing} Wochen fehlen (Extras > Wochenübersicht)" : "");
        });
        _grid.Rows.Clear();
        foreach (var r in _rows)
        {
            var i = _grid.Rows.Add(r.ReportNumber, $"{r.CalendarWeek}/{r.Year}", $"{Ui.Date(r.StartDate)} – {Ui.Date(r.EndDate)}", r.DepartmentLabel,
                Scheduling.FormatHours(r.TotalHours) + (r.HoursIssues > 0 ? " ⚠" : ""), Ui.StatusName(r.Status));
            if (r.HoursIssues > 0) _grid.Rows[i].Cells[4].ToolTipText = $"An {r.HoursIssues} Tag(en) passen die Stunden nicht zur Sollarbeitszeit (im Bericht auf „Stunden prüfen“ klicken).";
            _grid.Rows[i].Tag = r.Id;
            _grid.Rows[i].Selected = keep.Contains(r.Id);
        }
        if (_grid.SelectedRows.Count == 0 && _grid.Rows.Count > 0) _grid.Rows[0].Selected = true;
    }

    private List<int> SelectedIds() =>
        _grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(r => r.Index).Select(r => (int)r.Tag!).ToList();

    private bool NeedSelection(out List<int> ids)
    {
        ids = SelectedIds();
        if (ids.Count == 0) Ui.Info("Bitte zuerst einen Bericht in der Liste auswählen.");
        return ids.Count > 0;
    }

    // ------------------------------------------------------------ actions
    private void NewReport(DateOnly? day = null)
    {
        using var f = new NewReportForm(_host, day);
        if (f.ShowDialog(this) != DialogResult.OK || f.CreatedId == null) { Reload(); return; }
        Reload(f.CreatedId);
        Edit(f.CreatedId.Value);
    }

    private void Edit(int id)
    {
        using (var f = new EditorForm(_host, id)) f.ShowDialog(this);
        Reload(id);
    }

    private void EditSelected()
    {
        if (NeedSelection(out var ids)) Edit(ids[0]);
    }

    private void OpenInWord()
    {
        if (!NeedSelection(out var ids)) return;
        Ui.Try(() =>
        {
            using var s = _host.Open();
            var (data, name) = ReportFiles.Document(s, ids[0]);
            Ui.OpenBytes(data, name);
        });
        Reload();
    }

    private void SaveAs()
    {
        if (!NeedSelection(out var ids)) return;
        ReportFiles.SaveAs(this, _host, ids);
        Reload();
    }

    private void Sign()
    {
        if (!NeedSelection(out var ids)) return;
        SignForm.Run(this, _host, ids);
        Reload();
    }

    private void DeleteSelected()
    {
        if (!NeedSelection(out var ids)) return;
        if (!Ui.Confirm(ids.Count == 1 ? "Diesen Bericht löschen?" : $"{ids.Count} Berichte löschen?")) return;
        Ui.Try(() =>
        {
            using var s = _host.Open();
            var res = s.Reports.BulkAction(ids, "delete");
            if (res.Failed.Count > 0) Ui.Info(string.Join("\n", res.Failed.Select(f => f.Message)));
        });
        Reload();
    }

    private void Backup()
    {
        using var dlg = new SaveFileDialog
        {
            FileName = $"Berichtsheft-Sicherung_{DateTime.Now:yyyy-MM-dd}.zip", Filter = "Sicherung (*.zip)|*.zip", AddExtension = true, OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var old = Cursor.Current; Cursor.Current = Cursors.WaitCursor;
        try
        {
            if (Ui.Try(() => BackupService.Create(_host.Config, dlg.FileName, Program.Version)))
                Ui.Info("Die Sicherung wurde erstellt:\n" + dlg.FileName + "\n\nSie enthält alle Berichte, Vorlagen, Themen und Einstellungen.");
        }
        finally { Cursor.Current = old; }
    }

    private void Restore()
    {
        using var dlg = new OpenFileDialog { Filter = "Sicherung (*.zip)|*.zip", Title = "Datensicherung wählen" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (!Ui.Confirm("Die aktuellen Daten werden durch die Sicherung ersetzt.\n\nDie bisherigen Daten bleiben als Ordner neben „data“ erhalten. " +
                        "Das Programm startet dazu neu.\n\nFortfahren?")) return;
        if (!Ui.Try(() => BackupService.StageRestore(_host.Config, dlg.FileName))) return;
        _exiting = true;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--restarted") { UseShellExecute = true });
        Close();
    }

    private void ShowCoverage()
    {
        using var f = new CoverageForm(_host);
        f.ShowDialog(this);
        Reload();
        if (f.CreateWeek is { } day) NewReport(day);
    }
}

/// <summary>Document access shared by the main window and the editor.</summary>
internal static class ReportFiles
{
    public static (byte[] Data, string Name) Document(AppHost.Session s, int id)
    {
        var r = s.Reports.Get(id);
        if (string.IsNullOrEmpty(r.DocumentKey) || r.Status == "draft") Ui.Warnings(s.Reports.Export(id).Warnings);
        return s.Reports.Download(id);
    }

    /// <summary>Save one report as PDF (default) or Word, several as a ZIP of PDFs or Word documents.
    /// The format is picked in the save dialog.</summary>
    public static void SaveAs(IWin32Window owner, AppHost host, IReadOnlyList<int> ids)
    {
        Ui.Try(() =>
        {
            using var s = host.Open();
            var single = ids.Count == 1;
            string suggested;
            if (single)
            {
                var r = s.Reports.Get(ids[0]);
                suggested = Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(r.DocumentKey) ? $"Berichtsheft_{r.Year}_KW{r.CalendarWeek}" : r.DocumentKey.Split('/').Last());
            }
            else suggested = "Berichtshefte";
            using var dlg = new SaveFileDialog
            {
                FileName = suggested,
                OverwritePrompt = true,
                AddExtension = true,
                FilterIndex = 1, // PDF is the default
                Filter = single ? "PDF-Dokument (*.pdf)|*.pdf|Word-Dokument (*.docx)|*.docx"
                                : "ZIP mit PDF-Dokumenten (*.zip)|*.zip|ZIP mit Word-Dokumenten (*.zip)|*.zip",
            };
            if (dlg.ShowDialog(owner) != DialogResult.OK) return;
            var pdf = dlg.FilterIndex == 1;
            if (single && Path.GetExtension(dlg.FileName).Equals(".docx", StringComparison.OrdinalIgnoreCase)) pdf = false; // typed extension wins
            byte[] data;
            var old = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try { data = Build(s, ids, single, pdf); }
            catch (FeatureUnavailableError exc)
            {
                Cursor.Current = old;
                if (!Ui.Confirm(exc.Message + "\n\nStattdessen als Word-Dokument speichern?")) return;
                var file = single ? Path.ChangeExtension(dlg.FileName, ".docx") : dlg.FileName;
                Cursor.Current = Cursors.WaitCursor;
                try { File.WriteAllBytes(file, Build(s, ids, single, false)); }
                finally { Cursor.Current = old; }
                return;
            }
            finally { Cursor.Current = old; }
            File.WriteAllBytes(dlg.FileName, data);
        });
    }

    private static byte[] Build(AppHost.Session s, IReadOnlyList<int> ids, bool single, bool pdf)
    {
        if (!single) return s.Reports.ZipDocuments(ids.ToList(), pdf);
        if (pdf)
        {
            var r = s.Reports.Get(ids[0]);
            if (string.IsNullOrEmpty(r.DocumentKey) || r.Status == "draft") Ui.Warnings(s.Reports.Export(ids[0]).Warnings);
            return s.Reports.DownloadPdf(ids[0]).Data;
        }
        return Document(s, ids[0]).Data;
    }
}
