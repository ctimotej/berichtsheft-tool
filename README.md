# Berichtsheft Generator

Windows desktop program that creates weekly *Berichtshefte* (Ausbildungsnachweise) from **your own Word
template** and a configurable collection of departments, Lernfelder and text snippets, and signs them
with your signature. A plain Windows Forms application – one self-contained `Berichtsheft.exe`, no
server, no browser, no cloud service, no AI required.

Main window: the list of reports. *Neuer Bericht* → pick the week (Abteilung, Themen and school days come
from the plan) → edit the lines → *Unterschreiben* → *In Word öffnen* / *Speichern unter*.

## Features

- **Abteilungen and Themen:** the Abteilung (e.g. „Global Operations“) goes into the report header; the
  Themen (e.g. IT-Systemadministration, Netzwerkverwaltung) describe the work and hold the text snippets. Each
  Abteilung suggests its typical Themen, which can be changed per week.
- Themen, subjects and snippets stored in a local SQLite database, editable in the program
  (add/rename/delete, enable/disable, search, filter, category/difficulty/tags), JSON/CSV import & export
- Generation from several Themen with even distribution, duplicate prevention, adaptation to small
  Themen, avoidance of snippets used in the last reports, manual selection instead of random
- Weekly periods with calendar week, report number and training year computed automatically,
  German public holidays (per federal state) pre-marked, school/vacation/sick days per weekday
- **Planung:** rotation (Abteilung per period), Berufsschule block weeks and vacation as date ranges;
  new reports take day types, Abteilung and Themen from it
- **Berufsschule:** subjects/Lernfelder with lesson topics. Choose the subjects per school day (preset from
  the school block); the day is filled with topics of these subjects, e.g. „LF 3: IPv4-Adressierung“.
  In the editor, „Fächer ✎“ swaps a day's topics for other subjects.
- **Line wrapping:** entries longer than a form line continue on the next line (width per template,
  default 95 characters); the editor shows how many lines each day really needs
- **Wochenübersicht:** every week of the training as a grid – missing, draft, signed,
  overdue; one click creates a missing week or a whole gap. Warning on the dashboard and in the menu.
- **Prüfungsexport:** all reports of the training (or one Ausbildungsjahr, or a custom period) as one PDF
  with cover sheet (list of all weeks, signatures, missing weeks) or as a ZIP of Word files; can sign all
  reports that are not signed yet with the default signature
- **Mehrere Wochen:** generate a whole range of weeks at once (preview first, existing weeks are
  skipped), then export as ZIP, sign or submit them together from the report list
- Editor per week: edit, add, delete, move lines up/down (also across days), regenerate a single line or
  the whole week, hours per line, day type per day
- DOCX output from your template via `{{PLACEHOLDER}}`s – formatting of the template is preserved
- Visual signature (draw with mouse/pen or load an image, optionally saved as default; several reports can be signed at once)
- Optional PDF preview when LibreOffice is installed
- Pluggable providers: storage (local → SharePoint), generation (local → AI), signature (image → e-signature)
- Logging, input validation, upgrade of existing databases on start
- **Systray:** with the setting *Im Infobereich (Systray) laufen* (Einstellungen) the program keeps running in the
  notification area; closing the window only hides it, **Strg+Umschalt+B** brings it back from anywhere.
  Switch it off in the settings for a normal window. *Beenden* (Datei menu or tray menu) quits for real.
- **PDF or Word:** *Speichern unter* offers PDF (default) or Word for one report, or a ZIP of PDFs/Word files for
  several. PDF needs Microsoft Word or LibreOffice on the PC (LibreOffice is free: libreoffice.org); without either, the program offers to save the Word document instead.
- **One instance, autostart, reminders:** starting the program a second time just brings the running window
  forward. Settings: start with Windows (hidden in the tray) and a tray reminder (at most once a day) when a week
  has no report – on Fridays for the current week, and for earlier missing weeks.
- **Backup:** *Datei > Datensicherung erstellen* zips database, reports and templates; *wiederherstellen* replaces
  the current data with such a ZIP after a restart (the old data stays in a `data.vor-wiederherstellung-…` folder).
  The ZIP is checked first (size limit, intact database of this program, not from a newer version).
  Saved signatures are encrypted with Windows (DPAPI, current user): they are not readable in the database or in backups, but also not
  on another PC or Windows account. If a signature cannot be decrypted the program asks for a new one and deletes the unreadable
  value after 3 program starts.
- **Import of your own weeks:** *Datei > Eigene Berichte importieren* takes weeks you wrote by hand – filled-in Word files
  of the usual Ausbildungsnachweis form (several at once) or typed in (one line per entry, optional `| Stunden`).
  A list shows what will happen per week and lets you correct it first; existing weeks are skipped. Imported reports
  behave like generated ones (signing, PDF/Word, coverage, bulk export) – only "regenerate" is disabled for them.
- **Sign and export a period:** *Datei > Berichte unterschreiben und exportieren* signs all unsigned reports in a
  date range in one step and saves them as one ZIP of PDFs (or Word files).
- **Ausbilder mode:** the program always starts in the normal (Azubi) mode. Tick *Ausbilder-Modus anbieten* in the settings to get
  *Ansicht > Ausbilder-Modus*, which turns the window into a
  signing desk: drag finished Berichtshefte (**.pdf** or **.docx**, single files or whole folders) onto it. The
  Ausbilder's saved signature and the date are placed in the field "Ausbildender bzw. Ausbilder", and the results land in a
  new subfolder `Berichtsheft-signiert-<yyyy-MM-dd>_<HH-mm>` next to the originals (which stay untouched). Optionally
  Word files are also saved as PDF. Files without that field, scans, protected or already signed files are reported per
  file and skipped. Report writing, catalog, planning, import and backup are hidden in this mode.
- **Hours check:** the report list shows the hours per week (⚠ when a day does not add up to the working hours from
  the settings); in the editor, *Stunden prüfen…* lists the days and offers to spread the expected hours over their entries.

## Setup

Requirement: Windows 10/11. None for the release package (`Berichtsheft.exe` is one self-contained file, see
[docs/INSTALL-EXE.md](docs/INSTALL-EXE.md)). Building needs the **.NET 8 SDK**.
Optional: **LibreOffice** – enables uploading old `.doc` templates and the PDF exam export.

Start `Berichtsheft.exe` (no installer needed – copy it to a folder you can write to; pin it to the taskbar or use the autostart setting). All data lives in the `data` folder next
to the program (database, templates, generated reports, logs) – back up this folder. Deleting it resets the
program to the example data.

From source: `dotnet run --project src/Berichtsheft.Desktop` (Windows), or `tools/build-exe.sh` (Linux/macOS) / `tools\build-exe.ps1` (PowerShell) for the `.exe`.

## First steps

1. Start the program. On the first start the **Einstellungen** open: name, company, profession, start and end of
   training (report number, training year, missing weeks), hours, federal state (holidays), default signature.
2. **Extras > Themen und Textbausteine:** adjust the example data (see `seed/example_snippets.json`) or import
   your own collection. Lesson topics are the snippets of a subject (Fach).
3. **Extras > Vorlagen:** add your Word template (see [docs/TEMPLATES.md](docs/TEMPLATES.md)).
4. **Extras > Planung:** enter your rotation (Abteilung per period), your Berufsschule blocks and vacation.
5. **Datei > Neuer Bericht** for one week, **Mehrere Wochen erstellen** to catch up, **Extras > Wochenübersicht**
   to find gaps.
6. Before the exam: **Datei > Prüfungsexport**.

Generated documents are saved as `data/reports/<year>/Berichtsheft_<year>_KW<kw>_Nr<n>_<Name>.docx`.

## Configuration

Settings that concern the content (name, hours, holidays …) are edited in the program. Technical settings
all have defaults; if ever needed they can be overridden with `BH_*` environment variables (e.g. `BH_DATA_DIR`,
`BH_LIBREOFFICE_PATH`). There is no config file.

## Tests

```bash
dotnet test
```

## Documentation

- [docs/INSTALL-EXE.md](docs/INSTALL-EXE.md) – build `Berichtsheft.exe` and hand it out
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) – stack, layers, data model, extension points, roadmap
- [docs/TEMPLATES.md](docs/TEMPLATES.md) – placeholder reference, adding your own template, line wrapping

## About the signature

The built-in signature places an **image** of your handwritten signature into the document. This is a
visual signature, comparable to a scanned signature. It is **not** a qualified electronic signature
(QES) under eIDAS and does not prove identity or protect the document against later changes. Whether
it is accepted is decided by your training company / chamber (IHK/HWK); many chambers accept
electronically kept Ausbildungsnachweise signed this way, but check first. A real e-signature provider can be added behind the `SignatureProvider` interface.

---

© 2026 Tim Kraemer. All rights reserved.
