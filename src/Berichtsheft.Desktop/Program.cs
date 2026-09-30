using Berichtsheft.Core;
using Berichtsheft.Core.Config;
using Berichtsheft.Core.Hosting;
using Berichtsheft.Core.Services;
using Berichtsheft.Core.Templating;

namespace Berichtsheft.Desktop;

internal static class Program
{
    public const string Version = "2.0.0";
    private const string ShowEventName = @"Local\BerichtsheftGenerator.Show";

    /// <summary>Berichtsheft.exe                                      start the program
    /// Berichtsheft.exe --prepare-template in.docx out.docx   insert {{PLACEHOLDERS}} into the original form</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length > 0 && args[0] == "--prepare-template")
        {
            if (args.Length != 3)
            {
                MessageBox.Show("Aufruf: Berichtsheft.exe --prepare-template <Eingabe.docx> <Ausgabe.docx>", Ui.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 2;
            }
            TemplatePreparer.Prepare(args[1], args[2]);
            MessageBox.Show($"Vorlage geschrieben: {args[2]}", Ui.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        // one instance only: a second start just brings the running window to the front
        var restarted = args.Contains("--restarted");
        var startHidden = args.Contains("--hidden");
        using var mutex = new Mutex(false, @"Local\BerichtsheftGenerator");
        bool owned;
        try { owned = mutex.WaitOne(restarted ? 10000 : 0); } // after a restore/restart the old process may still be closing
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        AppHost host;
        try
        {
            var cfg = AppConfig.Load();
            if (BackupService.ApplyPendingRestore(cfg) is { } kept)
                MessageBox.Show($"Die Sicherung wurde wiederhergestellt.\n\nDie bisherigen Daten liegen noch hier:\n{kept}", Ui.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            host = new AppHost(cfg);
        }
        catch (Exception exc)
        {
            MessageBox.Show("Berichtsheft Generator konnte nicht gestartet werden:\n\n" + exc.Message, Ui.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        Application.ThreadException += (_, e) => Ui.ShowError(e.Exception);
        using (host)
        {
            var main = new MainForm(host, startHidden);
            var listener = new Thread(() =>
            {
                while (showEvent.WaitOne())
                {
                    try { main.BeginInvoke(main.Summon); } catch (InvalidOperationException) { return; } // window already gone
                }
            }) { IsBackground = true };
            listener.Start();
            Application.Run(main);
        }
        return 0;
    }
}
