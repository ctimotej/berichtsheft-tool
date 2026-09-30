using Microsoft.Win32;

namespace Berichtsheft.Desktop;

/// <summary>"Start with Windows": a value in HKCU\...\Run (no admin rights needed) that starts the program hidden in the tray.</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Berichtsheft Generator";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe) key.SetValue(ValueName, $"\"{exe}\" --hidden"); // also fixes the path after moving the .exe
            else key.DeleteValue(ValueName, false);
        }
        catch (Exception exc) when (exc is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Ui.Info("Der Autostart konnte nicht geändert werden:\n\n" + exc.Message);
        }
    }
}
