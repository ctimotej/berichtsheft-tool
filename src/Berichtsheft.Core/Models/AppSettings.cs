namespace Berichtsheft.Core.Models;

/// <summary>
/// Stored as one JSON document in the settings table (key "app_settings").
/// Default count modes: "per_week" | "per_day".
/// </summary>
public sealed class AppSettings
{

    public string TraineeName { get; set; } = "";
    public string Company { get; set; } = "";
    /// <summary>Also selects the Fachrichtung, i.e. the Lernfelder 10–12 of the 3rd year (see Services/Curriculum.cs).</summary>
    public string Profession { get; set; } = "";
    public DateOnly? TrainingStartDate { get; set; }
    public double HoursPerDay { get; set; } = 8.0;
    public double SchoolHoursPerDay { get; set; } = 8.0;
    public int DefaultCount { get; set; } = 8;
    /// <summary>Upper bound of entries per day (mode "per_day"); 0 = always exactly DefaultCount.</summary>
    public int DefaultCountMax { get; set; }
    public string DefaultCountMode { get; set; } = "per_week";
    public int AvoidRecentReports { get; set; } = 2;
    public bool FillHours { get; set; } = true;
    public string FederalState { get; set; } = "";
    public bool SchoolTopics { get; set; } = true;
    public int SchoolEntriesPerDay { get; set; } = 3;
    /// <summary>School days: only the Lernfelder taught at that point of the Ausbildungsjahr (in order, by their hours);
    /// off = all Lernfelder of the Ausbildungsjahr. See Services/Curriculum.cs.</summary>
    public bool SchoolSubjectsByDate { get; set; } = true;
    public DateOnly? TrainingEndDate { get; set; }
    public int OverdueDays { get; set; } = 14;
    /// <summary>Tray icon + global hotkey Ctrl+Shift+B; closing the window then only hides it.</summary>
    public bool TrayEnabled { get; set; } = true;
    /// <summary>Start with Windows (hidden in the tray).</summary>
    public bool AutostartEnabled { get; set; }
    /// <summary>Offers the Ausbilder mode (menu "Ansicht"). The program always starts in the normal mode; the
    /// Ausbilder mode must be enabled here and is switched on per session.</summary>
    public bool TrainerModeEnabled { get; set; }
    /// <summary>Format preselected when saving reports ("Speichern unter", ZIP of several reports, Prüfungsexport):
    /// "pdf" (default) | "docx". The save dialog can still switch it per file.</summary>
    public string SaveFormat { get; set; } = "pdf";
    /// <summary>Ausbilder mode: also save Word files as PDF.</summary>
    public bool TrainerAlsoPdf { get; set; }
    /// <summary>Tray notification for missing reports (needs the tray).</summary>
    public bool ReminderEnabled { get; set; } = true;
    /// <summary>Day of the last reminder - at most one per day.</summary>
    public DateOnly? ReminderLastDate { get; set; }

    public void Validate()
    {
        Dtos.Check.Str("trainee_name", TraineeName, 0, 120);
        Dtos.Check.Str("company", Company, 0, 120);
        Dtos.Check.Str("profession", Profession, 0, 120);
        Dtos.Check.Num("hours_per_day", HoursPerDay, 0, 24);
        Dtos.Check.Num("school_hours_per_day", SchoolHoursPerDay, 0, 24);
        Dtos.Check.Num("default_count", DefaultCount, 1, 100);
        if (DefaultCountMax != 0) Dtos.Check.Num("default_count_max", DefaultCountMax, DefaultCount, 100);
        Dtos.Check.OneOf("default_count_mode", DefaultCountMode, Dtos.Kinds.CountModes);
        Dtos.Check.Num("avoid_recent_reports", AvoidRecentReports, 0, 20);
        Dtos.Check.Str("federal_state", FederalState, 0, 5);
        Dtos.Check.Num("school_entries_per_day", SchoolEntriesPerDay, 1, 10);
        Dtos.Check.Num("overdue_days", OverdueDays, 0, 365);
        Dtos.Check.OneOf("save_format", SaveFormat, Dtos.Kinds.SaveFormats);
    }
}
