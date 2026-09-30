# Architecture

## Stack and reasoning

| Concern | Choice | Why |
|---|---|---|
| Runtime | C# / .NET 8 | one self-contained `Berichtsheft.exe`, no runtime to install on the target PC |
| UI | Windows Forms (.NET 8) | native, plain look; no browser or server; forms are built in code, no designer files |
| Database | SQLite via EF Core 8 | zero installation, single file, transactional; EF Core allows moving to PostgreSQL/SQL Server for multi-user (phase 2) |
| DOCX | Open XML SDK + own placeholder engine | fills *your* template instead of generating a new document; preserves formatting |
| Signature image | SkiaSharp | validation, cropping, normalisation |
| PDF | PDFsharp (merge), LibreOffice (conversion, optional) | |
| Tests | xUnit against the services with a temp data folder (`Hosting/AppHost`) | |

**Desktop app instead of web app:** the first versions were a local web application (ASP.NET Core + browser UI).
It was replaced by a Windows Forms program: one process, no port, no browser. Business logic lives in
`Berichtsheft.Core` and does not know the UI, so it was reused unchanged.

## Layers

```
src/Berichtsheft.Desktop/  Windows Forms UI: MainForm (report list + menu), EditorForm, NewReportForm, SignForm,
│                          SettingsForm, CatalogForm, PlanForm, CoverageForm, BulkForm, ExamForm, TemplatesForm
src/Berichtsheft.Core/
├── Hosting/               AppHost (composition root: config, providers, DB upgrade, seeding; one Session per action)
├── Dtos/                  request/response shapes and input validation
├── Services/              business logic: catalog (Themen/Fächer + snippets), units (Abteilungen), reports
│                          (incl. bulk), plan, coverage (missing weeks), exam export, templates, settings,
│                          holidays, render context, line wrapping
├── Generation/            generation algorithm behind ISnippetGenerationProvider (+ scheduling helpers)
├── Templating/            DOCX engine (placeholders), template preparer, optional LibreOffice converter
├── Signature/             ISignatureProvider + image implementation
├── Storage/               IDocumentStorageProvider + local (SharePoint skeleton: Services/BerichtsheftContext.cs)
├── Data/                  EF Core entities, DbContext, migrations, seeding
├── Config/                Defaults + optional BH_* environment variables (no config file)
└── Errors.cs              domain errors → user-friendly messages
test/Berichtsheft.Tests/   xUnit tests
```

Rules: the UI calls services only through an `AppHost.Session` (one short-lived DB context per user action).
Services own transactions and use the
providers only through their interfaces. Generation, DOCX engine, signature and storage modules do not
know the database – they are pure and unit-testable.

## Data model

- **Unit** (UI: *Abteilung*) – id, name (unique), description, active, sort_order, `topic_ids` (default
  Themen). The organisational department printed in the report header.
- **Department** (table name kept from 1.0; UI: *Thema* or *Fach*) – id, name (unique), `kind`
  (`topic` | `school`), `code` (e.g. „LF 3“), description, active, sort_order. A snippet group: Themen
  describe work areas, school subjects hold lesson topics (rendered as „code: topic“).
- **Snippet** – id, department_id, text, category, difficulty, tags (JSON), active, timestamps
- **Template** – id, name, original/stored filename (UUID on disk), detected placeholders, is_active
- **Report** – id, period_type (`weekly`), start/end date, calendar_week, year, report_number,
  trainee_name, training_year, department_label, department_ids, generation_options,
  `content` (JSON: days {date, type, subject_ids} → entries {text, hours, snippet_id, department_id}), remarks,
  `unit_id` + `department_label` (header text, normally the unit name), `department_ids` (= topic ids),
  status (`draft` | `exported` | `signed`), template_id,
  document_key, exported_at, timestamps
- **Signature** – id, report_id, role (`trainee` | `trainer`), signer_name, provider, signature_data (PNG), signed_at
- **PlanPeriod** – kind (`rotation` | `school` | `vacation`), start/end date, `unit_id` (rotation),
  department_ids (Themen – empty = the unit's defaults – or school subjects), note. Periods of the same
  kind must not overlap.
- **Template** – … plus `line_chars` (approx. characters per form line, used for wrapping).

- **Setting** – key/value (JSON) for app settings and the default signature

Entries are stored as JSON inside the report: they are always edited and saved as a whole week, and
entries are copies (editing a snippet later must not change old reports). `snippet_id` is kept for
“avoid recently used” logic. Schema changes are EF Core migrations (`Data/Migrations`). The database is created by `Database.Migrate()` at start.

## Flow of a report

1. *Neuer Bericht* → `ReportService.Generate`: builds the days (explicit day type > holiday > plan:
   vacation > school block > work). School days get `school_entries_per_day` topics from the Lernfelder of
   the covering school block (or all active ones), generated with the same algorithm. For work days it asks the
   `SnippetGenerationProvider` for N entries from the pool of the selected Themen, distributes them
   over the work days, distributes hours in 0.5 steps, saves a **draft**.
2. The editor saves with `ReportService.Update`. Content changes to a signed report
   remove the signature (status back to draft) – a signature always refers to the exact content.
3. `Export` renders the active template with the report data and stores the file
   through the `DocumentStorageProvider`. The document is always rendered fresh from template + data, never
   edited incrementally.
4. `Sign` asks the `SignatureProvider`, stores the signature and re-exports.

Statuses: `draft` → `exported` → `signed`.

**School days** get `school_entries_per_day` topics from the subjects chosen for that day
(`day_subjects`), otherwise from the subjects of the school block, otherwise from all active subjects.
Days with the same subject set share one pool, so topics are not repeated within a week.
`ReportService.SchoolDayEntries` produces new topics for one day (editor: switching a day to *Berufsschule*).

**Line wrapping** (`Services/LineWrap.cs`, same greedy algorithm in `ui.js: wrapText`): when rendering,
every entry is split at word boundaries into form lines of `line_chars`; hours go next to the first line.
If a day needs more lines than the template has, the rest is joined into the last line and a warning
is returned. Character counts are an approximation – the width is adjustable per template.

**Missing weeks** (`Services/CoverageService.cs`, `ReportService.Coverage`): every week from the start
(or first report) to the current week / end of training, with the report status or `missing`. The current
week is `current`, never `missing`. `overdue`: not signed `overdue_days` after the week's Friday.

**Exam export** (`Services/ExamService.cs`, `Preview` / `Export`): reports of a period in
order; with `auto_sign` every report without trainee signature that is still editable is signed with
the default signature (same path as manual signing, so the document is re-rendered); missing documents
are exported. A cover sheet (Open XML: personal data, table of all reports with trainee/trainer
signature state, list of missing weeks) goes first. PDF: all files are converted in **one** LibreOffice
run and merged with PDFsharp; without LibreOffice a numbered ZIP of the Word files is produced. The trainer
signature is never added automatically.

**Bulk generation** (`ReportService.Bulk` (dry run and real)) walks the range week by week in
chronological order and calls the same `generate()` – so “avoid recently used” also works across the new
weeks. Departments per week come from the rotation plan (fallback departments otherwise); weeks with an
existing report are skipped; weeks where the pool is smaller than the requested count are reported as
`too_few` in the preview instead of failing silently.

## Generation algorithm (`Generation/LocalSnippetGenerator.cs`)

1. Deduplicate the pool by text, drop empty departments (with a warning).
2. Quotas: *even* – water-filling: each department gets `count / n`; departments with too few snippets
   give their remainder to the others. *weighted* – proportional to pool size.
3. Per department pick snippets randomly, preferring snippets **not** used in the last X reports.
4. Interleave departments and ensure no identical snippet appears twice in a row (relevant when
   repetition is allowed).
5. If `avoid_duplicates` and the pool is too small → `NotEnoughSnippetsError` with a helpful message.

To add an AI provider, implement `SnippetGenerationProvider.generate/generate_one` (e.g. reword the
selected snippets via an LLM, using the pool as examples) and register it in `Generation/Registry.cs`;
select it with `BH_GENERATION_PROVIDER`. If it fails or is not configured, the local generator is used.

## DOCX processing

See [TEMPLATES.md](TEMPLATES.md). Key points: Word often splits `{{NAME}}` into several *runs* (because of
spell checking, formatting or editing history). The engine concatenates the runs of a paragraph, finds
placeholders, and rewrites only the affected runs – the first run keeps its formatting. It processes body,
tables (nested), headers and footers. Images are inserted as inline pictures; list values (`{{ENTRIES}}`)
clone the paragraph once per item. Legacy `.doc` is a binary format that the Open XML SDK cannot read – uploads
are converted with LibreOffice if installed.

## Storage and SharePoint (phase 3)

`DocumentStorageProvider` has `upload_document`, `download_document`, `list_documents`,
`delete_document`; keys are relative paths like `2026/Berichtsheft_2026_KW40_Nr62_Muster.docx`.
`LocalStorageProvider` writes atomically below `data/reports` and rejects path traversal.

`SharePointStorageProvider` (skeleton in `Services/BerichtsheftContext.cs`) is planned as:

- Authentication with **MSAL** against Microsoft Entra ID. Single-user: delegated flow (device code or
  interactive) with token cache; service scenario: client credentials with a certificate or secret.
- Permissions: preferably **`Sites.Selected`** (access only to the one site) instead of `Sites.ReadWrite.All`.
- Graph calls: `PUT /sites/{site-id}/drives/{drive-id}/root:/{folder}/{name}:/content` (upload < 4 MB,
  upload session for larger), `GET …/content`, `GET …/children`, `DELETE /drives/{drive-id}/items/{id}`.
- Configuration only via environment (`BH_SP_*`), never in code or the database; no passwords are stored.
- Enable with `BH_STORAGE_PROVIDER=sharepoint`. Nothing else in the app changes. A later option is
  “local + upload” (keep a local copy, mirror to SharePoint) by composing two providers.

## Security

The program runs as the current Windows user and opens no network port. All data stays in the `data` folder.
Other measures: input validation in the DTOs, upload size limits, file type validation of templates.
If a multi-user or hosted version is needed later: user accounts / Entra ID login, `owner_id` on the data,
authorization in the services.

## Error handling and logging

Services raise `AppError` subclasses with German, user-facing messages; `Ui.Try` shows them in a message box.
Unexpected exceptions become a generic message while the stack trace is written to `data/logs/app.log` (rotating).

## Roadmap

| Phase | Content | Where it plugs in |
|---|---|---|
| 1.0 ✅ | local app, departments, snippets, DOCX, local storage, visual signature | – |
| 1.1 ✅ | Azubi/Ausbilder modes, exchange folder, bulk generation, planning, Berufsschule/Lernfelder | – |
| 1.2 ✅ | Abteilungen + Themen, subjects per school day, line wrapping, missing weeks, exam export, several trainers, Lehrjahr grouping | – |
| 2.0 ✅ | Windows Forms desktop app replaces the web UI; exchange folder removed | |
| later | user accounts, central hosting, multi-user | models (`owner_id`), login, EF Core migrations, PostgreSQL/SQL Server |
| 3 | SharePoint document storage, Microsoft login | `Storage/`, MSAL |
| 4 | AI wording/variation, smarter distribution based on history | `Generation/` provider, recent-usage data already stored |
| – | daily/monthly periods | `period_type` exists; add period builders in `report_service.build_days` + template variants |
