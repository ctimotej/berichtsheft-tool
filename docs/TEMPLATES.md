# Templates

The application never creates a document from scratch. It loads a `.docx` template and replaces
placeholders of the form `{{NAME}}` (upper case, digits, underscore; spaces inside the braces are
tolerated). Everything else – layout, fonts, tables, logos, headers – stays exactly as in the template.

## Placeholder reference

| Placeholder | Content |
|---|---|
| `{{NAME}}` | trainee name |
| `{{NR}}` | number of the Ausbildungsnachweis |
| `{{KW}}` / `{{YEAR}}` | calendar week / its year |
| `{{START_DATE}}` / `{{END_DATE}}` | period (DD.MM.YYYY) |
| `{{PERIOD}}` | “DD.MM.YYYY – DD.MM.YYYY” |
| `{{DATE}}` | date of document creation |
| `{{DEPARTMENT}}` | department(s) of the week |
| `{{TRAINING_YEAR}}` | Ausbildungsjahr |
| `{{COMPANY}}` / `{{PROFESSION}}` | from settings |
| `{{REMARKS}}` | Bemerkungen |
| `{{TOTAL_HOURS}}` | sum of all hours |
| `{{ENTRIES}}` | all entries of the week as a list – the paragraph containing it is duplicated once per entry (for free-form templates) |
| `{{SIGNATURE}}` | signature image (height ≈ 1.1 cm, max. width ≈ 3.2 cm) |
| `{{SIGNATURE_DATE}}` / `{{SIGNER_NAME}}` | only filled when signed |
| `{{TRAINER_SIGNATURE}}` | signature image of the trainer (filled after approval) |
| `{{TRAINER_SIGNATURE_DATE}}` / `{{TRAINER_NAME}}` | only filled when approved by the trainer |
| `{{MO_1}}` … `{{MO_n}}` | line n of Monday (also `DI`, `MI`, `DO`, `FR`, `SA`, `SO`) |
| `{{MO_1_H}}` … | hours of that line |
| `{{MO_DATE}}`, `{{MO_HOURS}}`, `{{MO_TYPE}}` | date, hour sum, day type (Betrieb, Berufsschule, …) |

The highest `n` used for a weekday defines how many lines that day has (the editor shows “x von n
Zeilen”). If a day has more entries than lines, the surplus is joined into the last line with “; ” and
a warning is shown. Unused lines are left blank. Placeholders without a value are removed (and logged),
so a template never shows raw `{{…}}`. The *Vorlagen* page lists unknown placeholders after upload.

## The prepared standard template

`seed/templates/Standard_Tagesbericht.docx` is your uploaded *Vorlage Tagesbericht Gewerblich-Technische
Berufe*, converted from `.doc` to `.docx` and enriched with placeholders by
`Berichtsheft --prepare-template <in.docx> <out.docx>` (reproducible, the original layout is untouched):

- header: `{{NR}}` (Ausbildungsnachweis Nr.), `{{NAME}}`, `{{START_DATE}}` / `{{END_DATE}}`
  (Woche vom / bis), `{{DEPARTMENT}}` (Ausbildungsabteilung), `{{TRAINING_YEAR}}`
- body: 6 lines per weekday `{{MO_1}}`…`{{FR_6}}` with hours `{{MO_1_H}}`… in *Einzelstunden* (9 pt)
- signature cell *Auszubildende/r*: `{{SIGNATURE}}` and `{{SIGNATURE_DATE}}`
- signature cell *Ausbildender bzw. Ausbilder*: `{{TRAINER_SIGNATURE}}` and `{{TRAINER_SIGNATURE_DATE}}`  (optional; stays empty – the trainer signs the printout)
- *Bemerkungen*: `{{REMARKS}}`

School topics appear in the normal day lines as „LF 3: Topic“; no extra placeholders are needed.

The rows of this form have a **fixed height of one line**; Word cuts off text that does not fit. The app
therefore **wraps long entries onto the following lines** of the same day (at word boundaries; the hours
stand next to the first line). The editor shows „2 Zeilen“ next to such entries and counts the lines a day
really needs („5 von 6 Zeilen“). If a day needs more lines than the form has, the rest is put into the
last line and a warning is shown.

The width is an approximation in characters (proportional fonts differ), set per template on the
*Vorlagen* page, column *Zeichen pro Zeile* (default 95). If text is still cut off, lower the value; if lines break too early, raise it. Templates with rows
that grow in height (normal Word tables) can use a large value, e.g. 400, to switch wrapping off.

## Using your own template

1. Open your form in Word (or LibreOffice) and save it as **Word document (.docx)**.
2. Type the placeholders where the content should appear, e.g. `{{NAME}}` in the name cell, `{{MO_1}}`,
   `{{MO_2}}`, … in the Monday lines and `{{MO_1_H}}` … in the hours column, `{{SIGNATURE}}` in the
   signature field. Format the placeholder text the way the result should look (font, size, alignment) –
   the replacement inherits the formatting of the first character of the placeholder.
3. Type each placeholder in one go. If Word splits it internally (spell check, later edits) that is
   handled automatically, but a placeholder spread over two *paragraphs* or cells is not recognised.
4. In the app: *Vorlagen → Vorlage hochladen*, then *Aktivieren*. The app shows the detected placeholders,
   the number of lines per day and unknown names (typos). Several templates can be stored; the active one
   is used for new exports. Already exported reports are re-rendered with the active template when exported
   again.

Tips: put `{{SIGNATURE}}` and `{{TRAINER_SIGNATURE}}` in their own paragraphs with some space; if your form has no per-day structure,
use a single paragraph `{{ENTRIES}}` – it becomes one paragraph per entry with the same list/bullet style.

## Limitations

- **`.doc`** (Word 97–2003) is a binary format that cannot be edited reliably without Word. Uploads are
  converted to `.docx` automatically if LibreOffice is installed; otherwise save as `.docx` in Word.
  Check the converted layout once.
- Placeholders inside text boxes/shapes, content controls, form fields, footnotes and comments are not
  processed. Use normal paragraphs or table cells.
- Only the text of a placeholder is replaced; conditional sections or loops over table rows are not
  supported (use the fixed `{{MO_n}}` lines instead).
- Field codes (e.g. page numbers) are kept but not updated until Word recalculates them.
- The PDF preview is produced by LibreOffice and can differ slightly from Word's rendering.
