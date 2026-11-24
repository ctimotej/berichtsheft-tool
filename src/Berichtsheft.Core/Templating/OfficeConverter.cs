using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Berichtsheft.Core.Templating;

/// <summary>Converts Word documents with LibreOffice (headless) or, on Windows, with an installed Microsoft Word.
/// Both are optional: without them <see cref="PdfAvailable"/> is false and PDF features say so.</summary>
public sealed class OfficeConverter
{
    private readonly string _configuredPath;
    private string? _soffice;
    private bool _searched;
    private bool? _word;

    public OfficeConverter(string? libreOfficePath = null) => _configuredPath = libreOfficePath ?? "";

    public string? LibreOfficePath
    {
        get
        {
            if (!_searched) { _soffice = FindLibreOffice(_configuredPath); _searched = true; }
            return _soffice;
        }
    }

    public bool WordAvailable => _word ??= OperatingSystem.IsWindows() && Type.GetTypeFromProgID("Word.Application") != null;
    public bool PdfAvailable => LibreOfficePath != null || WordAvailable;

    private const string Missing = "Für PDF wird Microsoft Word oder LibreOffice (kostenlos: libreoffice.org) benötigt – beides wurde auf diesem PC nicht gefunden.";

    public byte[] ToPdf(byte[] docx)
    {
        if (!PdfAvailable) throw new FeatureUnavailableError(Missing);
        var dir = Path.Combine(Path.GetTempPath(), "bh-pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "bericht.docx");
            File.WriteAllBytes(src, docx);
            var outFiles = ConvertManyToPdf(new List<string> { src }, dir, 90);
            return File.ReadAllBytes(outFiles[0]);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    /// <summary>Converts all files in one office start; returns the PDF paths in the same order.</summary>
    public List<string> ConvertManyToPdf(IReadOnlyList<string> inputs, string outDir, int timeoutSeconds) =>
        ConvertMany(inputs, outDir, "pdf", timeoutSeconds);

    /// <summary>Old .doc → .docx (template upload).</summary>
    public byte[] DocToDocx(byte[] doc)
    {
        if (!PdfAvailable) throw new FeatureUnavailableError("Alte .doc-Dateien können nur mit Microsoft Word oder LibreOffice umgewandelt werden. Bitte die Datei in Word als .docx speichern.");
        var dir = Path.Combine(Path.GetTempPath(), "bh-doc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "vorlage.doc");
            File.WriteAllBytes(src, doc);
            return File.ReadAllBytes(ConvertMany(new List<string> { src }, dir, "docx", 90)[0]);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    private List<string> ConvertMany(IReadOnlyList<string> inputs, string outDir, string format, int timeoutSeconds)
    {
        if (inputs.Count == 0) return new();
        Directory.CreateDirectory(outDir);
        var expected = inputs.Select(i => Path.Combine(outDir, Path.GetFileNameWithoutExtension(i) + "." + format)).ToList();
        if (LibreOfficePath is { } soffice) RunLibreOffice(soffice, inputs, outDir, format, timeoutSeconds);
        else if (WordAvailable) RunWord(inputs, expected, format);
        else throw new FeatureUnavailableError(Missing);
        var missing = expected.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0) throw new ExportError($"Die Umwandlung in {format.ToUpperInvariant()} ist fehlgeschlagen ({missing.Count} von {expected.Count} Dateien).");
        return expected;
    }

    private static void RunLibreOffice(string soffice, IReadOnlyList<string> inputs, string outDir, string format, int timeoutSeconds)
    {
        // own profile folder: works while the user has LibreOffice open, and avoids first-start dialogs
        var profile = Path.Combine(Path.GetTempPath(), "bh-lo-profile-" + Guid.NewGuid().ToString("N"));
        var psi = new ProcessStartInfo(soffice)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "--headless", "--norestore", "--nologo", "--nodefault", $"-env:UserInstallation={new Uri(profile).AbsoluteUri}",
                     "--convert-to", format, "--outdir", outDir }) psi.ArgumentList.Add(a);
        foreach (var i in inputs) psi.ArgumentList.Add(i);
        try
        {
            using var p = Process.Start(psi) ?? throw new ExportError("LibreOffice konnte nicht gestartet werden.");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(Math.Max(30, timeoutSeconds) * 1000))
            {
                try { p.Kill(true); } catch { /* already gone */ }
                throw new ExportError("LibreOffice hat zu lange gebraucht – die Umwandlung wurde abgebrochen.");
            }
            Task.WaitAll(new Task[] { stdout, stderr }, 5000);
        }
        catch (System.ComponentModel.Win32Exception exc)
        {
            throw new ExportError("LibreOffice konnte nicht gestartet werden: " + exc.Message, exc);
        }
        finally { try { Directory.Delete(profile, true); } catch { /* best effort */ } }
    }

    /// <summary>Late-bound COM automation of Word (no interop assembly needed).</summary>
    private static void RunWord(IReadOnlyList<string> inputs, List<string> outputs, string format)
    {
        if (!OperatingSystem.IsWindows()) throw new FeatureUnavailableError(Missing);
        var type = Type.GetTypeFromProgID("Word.Application") ?? throw new FeatureUnavailableError(Missing);
        dynamic? word = null;
        try
        {
            word = Activator.CreateInstance(type)!;
            word.Visible = false;
            word.DisplayAlerts = 0;
            for (var i = 0; i < inputs.Count; i++)
            {
                dynamic doc = word.Documents.Open(Path.GetFullPath(inputs[i]), false, true, false);
                try { doc.SaveAs2(Path.GetFullPath(outputs[i]), format == "pdf" ? 17 : 16); }
                finally { doc.Close(false); Marshal.FinalReleaseComObject(doc); }
            }
        }
        catch (Exception exc) when (exc is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        {
            throw new ExportError("Microsoft Word konnte das Dokument nicht umwandeln: " + exc.Message, exc);
        }
        finally
        {
            if (word != null)
            {
                try { word.Quit(false); } catch { /* ignore */ }
                try { Marshal.FinalReleaseComObject(word); } catch { /* ignore */ }
            }
        }
    }

    private static string? FindLibreOffice(string configured)
    {
        var candidates = new List<string>();
        if (configured.Length > 0) candidates.Add(configured);
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                if (root.Length > 0) candidates.Add(Path.Combine(root, "LibreOffice", "program", "soffice.exe"));
        }
        else if (OperatingSystem.IsMacOS()) candidates.Add("/Applications/LibreOffice.app/Contents/MacOS/soffice");
        var exe = OperatingSystem.IsWindows() ? "soffice.exe" : "soffice";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir, exe));
            if (!OperatingSystem.IsWindows()) candidates.Add(Path.Combine(dir, "libreoffice"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}
