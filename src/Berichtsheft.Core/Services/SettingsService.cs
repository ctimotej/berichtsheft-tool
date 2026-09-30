using System.Text.Json;
using Berichtsheft.Core.Data;
using Berichtsheft.Core.Models;
using Berichtsheft.Core.Signature;

namespace Berichtsheft.Core.Services;

public sealed class SettingsService
{
    private const string Key = "app_settings";
    private const string SigKey = "default_signature_png";
    private readonly BerichtsheftDbContext _db;

    public SettingsService(BerichtsheftDbContext db) => _db = db;

    public AppSettings Get()
    {
        var row = _db.Settings.Find(Key);
        if (row?.Value is { } json)
        {
            try { return JsonSerializer.Deserialize<AppSettings>(json, Json.Options) ?? new AppSettings(); }
            catch (JsonException) { /* fall through to defaults */ }
        }
        return new AppSettings();
    }

    public AppSettings Update(AppSettings data)
    {
        var row = _db.Settings.Find(Key);
        if (row == null) { row = new SettingEntity { Key = Key }; _db.Settings.Add(row); }
        row.Value = Json.Serialize(data);
        _db.SaveChanges();
        return data;
    }

    public JsonElement? GetFlagRaw(string key)
    {
        var row = _db.Settings.Find(key);
        if (row?.Value == null) return null;
        var el = JsonDocument.Parse(row.Value).RootElement.Clone();
        return el.ValueKind == JsonValueKind.Null ? null : el;
    }

    public bool GetFlag(string key) => GetFlagRaw(key) is { } el && el.ValueKind == JsonValueKind.True;

    public string? GetFlagString(string key) => GetFlagRaw(key) is { ValueKind: JsonValueKind.String } el ? el.GetString() : null;

    public void SetFlag(string key, object? value)
    {
        var row = _db.Settings.Find(key);
        if (row == null) { row = new SettingEntity { Key = key }; _db.Settings.Add(row); }
        row.Value = JsonSerializer.Serialize(value, Json.Options);
        _db.SaveChanges();
    }

    // ------------------------------------------------------------ signatures (encrypted at rest, see SignatureVault)
    private const string TrainerKey = "trainer_signature_png";
    private const string FailPrefix = "sigfail:";
    private static readonly HashSet<string> CountedThisRun = new();

    /// <summary>Forgets which failures were already counted in this program run (tests).</summary>
    public static void ResetAttemptCounting() { lock (CountedThisRun) CountedThisRun.Clear(); }

    /// <summary>Set by the last read of a signature that could not be decrypted (null if it worked). Read it right after the call.</summary>
    public SignatureProblem? LastSignatureProblem { get; private set; }

    public byte[]? GetDefaultSignature() => ReadSignature(SigKey);

    // the Ausbilder's own signature (Ausbilder mode)
    public byte[]? GetTrainerSignature() => ReadSignature(TrainerKey);

    public void SetTrainerSignature(byte[]? image) =>
        WriteSignature(TrainerKey, image is { Length: > 0 } ? Berichtsheft.Core.Signature.ImageSignature.NormalizeSignatureImage(image) : null);

    public void SetDefaultSignature(byte[]? png) => WriteSignature(SigKey, png is { Length: > 0 } ? png : null);

    private void WriteSignature(string key, byte[]? png)
    {
        SetFlag(key, png == null ? null : Convert.ToBase64String(SignatureVault.Protect(png)));
        ClearFailures(key);
    }

    private byte[]? ReadSignature(string key)
    {
        LastSignatureProblem = null;
        var v = GetFlagString(key);
        if (string.IsNullOrEmpty(v)) return null;
        byte[] stored;
        try { stored = Convert.FromBase64String(v); }
        catch (FormatException) { if (FailedRead(key, out _)) SetFlag(key, null); return null; }
        var png = OpenSignature(key, stored, out var deleted);
        if (deleted) SetFlag(key, null);
        return png;
    }

    /// <summary>Decrypts a stored signature image. If that fails, the failure is counted once per program start under
    /// <paramref name="counterKey"/>; after <see cref="SignatureVault.MaxAttempts"/> starts <paramref name="deleted"/> becomes true and
    /// the caller must remove the unreadable data (it can never be read again and is not kept). See <see cref="LastSignatureProblem"/>.</summary>
    public byte[]? OpenSignature(string counterKey, byte[] stored, out bool deleted)
    {
        deleted = false;
        LastSignatureProblem = null;
        try
        {
            var png = SignatureVault.Unprotect(stored);
            ClearFailures(counterKey);
            return png;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            deleted = FailedRead(counterKey, out _);
            return null;
        }
    }

    private bool FailedRead(string counterKey, out int attempts)
    {
        var row = _db.Settings.Find(FailPrefix + counterKey);
        attempts = row?.Value != null && int.TryParse(row.Value, out var n) ? n : 0;
        if (AddCounted(counterKey)) // several reads in one program run count as one attempt
        {
            attempts++;
            SetFlag(FailPrefix + counterKey, attempts);
        }
        var gone = attempts >= SignatureVault.MaxAttempts;
        if (gone) ClearFailures(counterKey);
        LastSignatureProblem = new SignatureProblem(counterKey, attempts, SignatureVault.MaxAttempts, gone);
        return gone;
    }

    private static bool AddCounted(string key) { lock (CountedThisRun) return CountedThisRun.Add(key); }

    private void ClearFailures(string counterKey)
    {
        var row = _db.Settings.Find(FailPrefix + counterKey);
        if (row != null) { _db.Settings.Remove(row); _db.SaveChanges(); }
    }

}

/// <summary>A stored signature could not be decrypted (e.g. data restored on another PC or Windows account).</summary>
public sealed record SignatureProblem(string Key, int Attempts, int MaxAttempts, bool Deleted)
{
    public string Message => Deleted
        ? "Die gespeicherte Unterschrift konnte nicht entschlüsselt werden und wurde gelöscht. Bitte neu festlegen."
        : $"Die gespeicherte Unterschrift kann nicht entschlüsselt werden (Versuch {Attempts} von {MaxAttempts}; sie stammt evtl. von einem anderen Windows-Konto oder PC). " +
          "Bitte neu festlegen – sonst wird sie nach dem letzten Versuch gelöscht.";
}
