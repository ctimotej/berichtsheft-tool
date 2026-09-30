using Berichtsheft.Core.Hosting;
using Berichtsheft.Core.Models;
using Berichtsheft.Core.Signature;

namespace Berichtsheft.Desktop;

internal sealed class SettingsForm : Form
{
    private static readonly string[] States = { "", "BW", "BY", "BE", "BB", "HB", "HH", "HE", "MV", "NI", "NW", "RP", "SL", "SN", "ST", "SH", "TH" };
    private readonly AppHost _host;
    private static readonly string[] Professions =
    {
        "Fachinformatiker für Systemintegration", "Fachinformatiker für Anwendungsentwicklung",
        "Fachinformatiker für Digitale Vernetzung", "Fachinformatiker für Daten- und Prozessanalyse",
    };
    private readonly TextBox _name = new(), _company = new();
    private readonly ComboBox _profession = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly DateTimePicker _start = DatePicker(), _end = DatePicker();
    private readonly ComboBox _state = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _hours = Num(0, 24, 1), _schoolHours = Num(0, 24, 1), _count = Num(1, 100, 0), _countMax = Num(1, 100, 0), _recent = Num(0, 20, 0),
        _schoolEntries = Num(1, 10, 0), _overdue = Num(0, 365, 0);
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _fill = new() { Text = "Stunden automatisch auf die Einträge verteilen", AutoSize = true };
    private readonly CheckBox _school = new() { Text = "Berufsschultage mit Unterrichtsthemen füllen", AutoSize = true };
    private readonly CheckBox _tray = new() { Text = "Im Infobereich (Systray) laufen; Strg+Umschalt+B öffnet das Programm", AutoSize = true };
    private readonly CheckBox _autostart = new() { Text = "Mit Windows starten (versteckt im Infobereich)", AutoSize = true };
    private readonly CheckBox _reminder = new() { Text = "Erinnern, wenn ein Bericht fehlt (freitags und für frühere Wochen)", AutoSize = true };
    private readonly CheckBox _trainerMode = new() { Text = "Ausbilder-Modus anbieten (Menü „Ansicht“): Berichtshefte per Drag & Drop unterschreiben", AutoSize = true };
    private readonly Label _sigState = new() { AutoSize = true, Margin = new Padding(6, 6, 0, 0) };
    private AppSettings _cfg = new();

    private static DateTimePicker DatePicker() => new() { Format = DateTimePickerFormat.Short, ShowCheckBox = true, Width = 120 };
    private static NumericUpDown Num(int min, int max, int decimals) => new() { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = decimals > 0 ? 0.5m : 1, Width = 70 };

    public SettingsForm(AppHost host, bool trainerMode = false)
    {
        _host = host;
        Ui.Dialog(this, "Einstellungen", 520, trainerMode ? 230 : 770);
        _state.Items.AddRange(States.Select(s => (object)(s.Length == 0 ? "(keins – keine Feiertage)" : s)).ToArray());
        _mode.Items.AddRange(new object[] { "pro Woche", "pro Tag" });

        var countRow = new FlowLayoutPanel { AutoSize = true };
        countRow.Controls.AddRange(new Control[] { _count, new Label { Text = "bis", AutoSize = true, Margin = new Padding(3, 6, 3, 0) }, _countMax, _mode });
        _mode.SelectedIndexChanged += (_, _) => _countMax.Enabled = _mode.SelectedIndex == 1;
        _count.ValueChanged += (_, _) => { if (_countMax.Value < _count.Value) _countMax.Value = _count.Value; };
        var sigRow = new FlowLayoutPanel { AutoSize = true };
        sigRow.Controls.Add(Ui.Button("Festlegen…", (_, _) => SetSignature(), 90));
        sigRow.Controls.Add(Ui.Button("Entfernen", (_, _) => RemoveSignature(), 80));
        sigRow.Controls.Add(_sigState);

        // Ausbilder mode: only what matters there (tray, autostart); the report settings keep their values
        var form = trainerMode ? Ui.Form(("", _tray), ("", _autostart), ("", _trainerMode)) : Ui.Form(
            ("Name:", _name), ("Ausbildungsbetrieb:", _company), ("Beruf:", _profession),
            ("Ausbildungsbeginn:", _start), ("Ausbildungsende:", _end), ("Bundesland (Feiertage):", _state),
            ("Stunden pro Arbeitstag:", _hours), ("Stunden pro Schultag:", _schoolHours),
            ("Einträge (von – bis pro Tag):", countRow), ("Themen der letzten Berichte meiden:", _recent),
            ("", _fill), ("", _school), ("", _tray), ("", _autostart), ("", _reminder), ("", _trainerMode), ("Einträge pro Schultag:", _schoolEntries),
            ("Als überfällig zeigen nach (Tagen):", _overdue), ("Standard-Unterschrift:", sigRow));
        form.Dock = DockStyle.Fill;
        Controls.Add(form);
        Controls.Add(Ui.ButtonBar(DockStyle.Bottom, Ui.OkButton(this, (_, _) => Save()), Ui.CancelButton(this)));

        Ui.Try(() =>
        {
            using var s = _host.Open();
            _cfg = s.Settings.Get();
            var has = s.Settings.GetDefaultSignature() != null;
            UpdateSignatureState(has);
            if (s.Settings.LastSignatureProblem is { } problem) { _sigState.Text = problem.Deleted ? "gelöscht" : "nicht lesbar"; Ui.Info(problem.Message); }
        });
        _profession.Items.AddRange(Professions);
        if (_cfg.Profession.Length > 0 && !Professions.Contains(_cfg.Profession)) _profession.Items.Add(_cfg.Profession); // keep an older free-text entry
        _profession.SelectedIndex = _cfg.Profession.Length == 0 ? -1 : _profession.Items.IndexOf(_cfg.Profession);
        _name.Text = _cfg.TraineeName; _company.Text = _cfg.Company;
        SetDate(_start, _cfg.TrainingStartDate); SetDate(_end, _cfg.TrainingEndDate);
        _state.SelectedIndex = Math.Max(0, Array.IndexOf(States, _cfg.FederalState));
        _hours.Value = (decimal)_cfg.HoursPerDay; _schoolHours.Value = (decimal)_cfg.SchoolHoursPerDay;
        _count.Value = _cfg.DefaultCount; _countMax.Value = Math.Max(_cfg.DefaultCount, _cfg.DefaultCountMax); _mode.SelectedIndex = _cfg.DefaultCountMode == "per_day" ? 1 : 0;
        _recent.Value = _cfg.AvoidRecentReports; _fill.Checked = _cfg.FillHours; _school.Checked = _cfg.SchoolTopics;
        _schoolEntries.Value = _cfg.SchoolEntriesPerDay; _overdue.Value = _cfg.OverdueDays;
        _tray.Checked = _cfg.TrayEnabled; _autostart.Checked = _cfg.AutostartEnabled; _reminder.Checked = _cfg.ReminderEnabled; _trainerMode.Checked = _cfg.TrainerModeEnabled;
        _tray.CheckedChanged += (_, _) => TrayChanged();
        TrayChanged();
    }

    private void TrayChanged()
    {
        _autostart.Enabled = _reminder.Enabled = _tray.Checked;
    }

    private static void SetDate(DateTimePicker p, DateOnly? d)
    {
        p.Checked = d != null;
        if (d != null) p.Value = d.Value.ToDateTime(TimeOnly.MinValue);
    }

    private static DateOnly? GetDate(DateTimePicker p) => p.Checked ? DateOnly.FromDateTime(p.Value) : null;

    private void UpdateSignatureState(bool has) => _sigState.Text = has ? "gespeichert" : "keine";

    private void SetSignature()
    {
        using var f = new SignForm(hasDefault: false, "Unterschrift, die beim Unterschreiben vorgeschlagen wird:", showSaveDefault: false);
        if (f.ShowDialog(this) != DialogResult.OK || f.Result?.Image == null) return;
        Ui.Try(() =>
        {
            var png = ImageSignature.NormalizeSignatureImage(ImageSignature.DecodeDataUrl(f.Result.Image));
            using var s = _host.Open();
            s.Settings.SetDefaultSignature(png);
            UpdateSignatureState(true);
        });
    }

    private void RemoveSignature()
    {
        Ui.Try(() => { using var s = _host.Open(); s.Settings.SetDefaultSignature(null); UpdateSignatureState(false); });
    }

    private void Save()
    {
        _cfg.TraineeName = _name.Text.Trim(); _cfg.Company = _company.Text.Trim(); _cfg.Profession = _profession.SelectedItem as string ?? "";
        _cfg.TrainingStartDate = GetDate(_start); _cfg.TrainingEndDate = GetDate(_end);
        _cfg.FederalState = States[Math.Max(0, _state.SelectedIndex)];
        _cfg.HoursPerDay = (double)_hours.Value; _cfg.SchoolHoursPerDay = (double)_schoolHours.Value;
        _cfg.DefaultCount = (int)_count.Value; _cfg.DefaultCountMax = _countMax.Value > _count.Value ? (int)_countMax.Value : 0; _cfg.DefaultCountMode = _mode.SelectedIndex == 1 ? "per_day" : "per_week";
        _cfg.AvoidRecentReports = (int)_recent.Value; _cfg.FillHours = _fill.Checked; _cfg.SchoolTopics = _school.Checked;
        _cfg.SchoolEntriesPerDay = (int)_schoolEntries.Value; _cfg.OverdueDays = (int)_overdue.Value;
        _cfg.TrayEnabled = _tray.Checked;
        _cfg.AutostartEnabled = _autostart.Checked && _tray.Checked; // autostart hides the window, so it needs the tray
        _cfg.ReminderEnabled = _reminder.Checked && _tray.Checked;
        _cfg.TrainerModeEnabled = _trainerMode.Checked;
        if (Ui.Try(() => { _cfg.Validate(); using var s = _host.Open(); s.Settings.Update(_cfg); }))
        {
            Autostart.Apply(_cfg.AutostartEnabled);
            DialogResult = DialogResult.OK;
        }
    }
}
