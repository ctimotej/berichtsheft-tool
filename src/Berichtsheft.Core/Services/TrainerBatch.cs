using Berichtsheft.Core.Templating;

namespace Berichtsheft.Core.Services;

public sealed record TrainerFileResult(string Source, bool Ok, string Message, string? Output);

/// <summary>Ausbilder mode: signs dropped .pdf/.docx files. Results go into a new subfolder
/// "Berichtsheft-signiert-&lt;yyyy-MM-dd_HH-mm&gt;" next to each original; originals are never changed.
/// One broken file does not stop the others.</summary>
public static class TrainerBatch
{
    public const string OutputPrefix = "Berichtsheft-signiert-";
    private static readonly string[] Extensions = { ".pdf", ".docx" };

    private static bool Supported(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()) && !Path.GetFileName(path).StartsWith("~$");

    /// <summary>Files and the supported files directly inside dropped folders (not the result folders of earlier runs).</summary>
    public static List<string> Expand(IEnumerable<string> paths)
    {
        var outp = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                outp.AddRange(Directory.EnumerateFiles(p).Where(Supported).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            else if (File.Exists(p) && Supported(p)) outp.Add(p);
            else if (Supported(p)) outp.Add(p); // missing file: reported by Process
        }
        return outp.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<TrainerFileResult> Process(IEnumerable<string> paths, byte[] signaturePng, DateOnly date, DateTime now,
        bool alsoPdf = false, OfficeConverter? converter = null)
    {
        var results = new List<TrainerFileResult>();
        foreach (var file in Expand(paths))
        {
            try
            {
                var data = File.ReadAllBytes(file);
                var outDir = Path.Combine(Path.GetDirectoryName(file)!, OutputPrefix + now.ToString("yyyy-MM-dd_HH-mm"));
                var isPdf = Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
                var signed = isPdf ? TrainerSigner.SignPdf(data, signaturePng, date) : TrainerSigner.SignDocx(data, signaturePng, date);
                Directory.CreateDirectory(outDir);
                var target = Unique(Path.Combine(outDir, Path.GetFileName(file)));
                File.WriteAllBytes(target, signed);
                var msg = "unterschrieben";
                if (!isPdf && alsoPdf)
                {
                    if (converter is { PdfAvailable: true })
                    {
                        try
                        {
                            File.WriteAllBytes(Unique(Path.ChangeExtension(target, ".pdf")), converter.ToPdf(signed));
                            msg += ", zusätzlich als PDF";
                        }
                        catch (AppError exc) { msg += " (PDF nicht möglich: " + exc.Message + ")"; }
                    }
                    else msg += " (für PDF wird Word oder LibreOffice benötigt)";
                }
                results.Add(new TrainerFileResult(file, true, msg, target));
            }
            catch (Exception exc) when (exc is not OutOfMemoryException)
            {
                var msg = exc is AppError ? exc.Message : exc is IOException or UnauthorizedAccessException
                    ? "Datei kann nicht gelesen oder geschrieben werden: " + exc.Message
                    : "Datei konnte nicht verarbeitet werden: " + exc.Message;
                results.Add(new TrainerFileResult(file, false, msg, null));
            }
        }
        return results;
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(p)) return p;
        }
    }
}
