# Build `Berichtsheft.exe` and hand it out

`Berichtsheft.exe` is one self-contained file: no installer, no .NET, no Python on the target PC.

## Build

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and internet on the first build.

```bash
tools/build-exe.sh                  # dist/win-x64/Berichtsheft.exe
tools/build-exe.sh win-x64 --zip    # + dist/Berichtsheft-win-x64.zip
tools/build-exe.sh all              # win-x64 and win-arm64
```

On Windows use PowerShell instead:

```powershell
.\tools\build-exe.ps1                # dist\win-x64\Berichtsheft.exe
.\tools\build-exe.ps1 win-x64 -Zip   # + dist\Berichtsheft-win-x64.zip
.\tools\build-exe.ps1 all            # win-x64 and win-arm64
# if scripts are blocked: powershell -ExecutionPolicy Bypass -File .\tools\build-exe.ps1
```

The bash script runs on Linux, macOS and (e.g. in Git Bash / WSL) Windows and produces Windows executables.

## Use

1. Copy `Berichtsheft.exe` to a folder you can write to (for example `C:\Users\<you>\Berichtsheft`). The `data` folder
   (database, templates, reports, logs) is created next to it – so not in `Program Files`.
2. Double-click it. If SmartScreen warns ("Der Computer wurde durch Windows geschützt"): *More info* → *Run anyway* (the exe is unsigned).
3. Optional, in the settings: run in the notification area with **Strg+Umschalt+B**, start with Windows, reminders.

**Update:** replace `Berichtsheft.exe` with the new one; the `data` folder stays. **Uninstall:** untick *Mit Windows starten*
in the settings (removes the autostart entry), then delete the folder. **Backup:** *Datei > Datensicherung erstellen*.

PDF export needs Microsoft Word or [LibreOffice](https://www.libreoffice.org) on the PC; without either, reports can only be saved as Word files.

## Technical overrides

There is no config file. If ever needed, `BH_*` environment variables override the defaults, for example
`BH_DATA_DIR` (other data folder), `BH_LIBREOFFICE_PATH`, `BH_LOG_LEVEL`.

## Troubleshooting

| Symptom | Fix |
|---|---|
| The program "does nothing" after closing the window | With the tray enabled, closing only hides it – use the tray icon or Strg+Umschalt+B; *Beenden* quits. |
| Strg+Umschalt+B does not react | Another program uses that key combination; use the tray icon. |
| Logs | `data\logs` next to the `.exe` |
