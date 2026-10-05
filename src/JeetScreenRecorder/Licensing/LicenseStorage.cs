using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JeetScreenRecorder.Licensing;

/// <summary>
/// Reads, writes and verifies the license cache stored in
/// %APPDATA%\JeetScreenRecorder\license.dat.
///
/// The file is encrypted with Windows DPAPI (CurrentUser scope) so no other
/// Windows account can read it, and the raw key never sits on disk.
///
/// If the file is missing, corrupted, or the machine changes, the cache is
/// simply discarded and the app falls back to online verification.
/// </summary>
internal static class LicenseStorage
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JeetScreenRecorder",
        "license.dat");

    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("jsr-license-v1-" + Environment.MachineName);

    // ------------------------------------------------------------------ write

    public static void Save(LicenseCache cache)
    {
        try
        {
            var json  = JsonSerializer.Serialize(cache);
            var plain = Encoding.UTF8.GetBytes(json);
            var enc   = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllBytes(FilePath, enc);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LicenseStorage: could not save license cache: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ read

    /// <summary>Returns null if missing, corrupted, or decryption failed.</summary>
    public static LicenseCache? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var enc   = File.ReadAllBytes(FilePath);
            var plain = ProtectedData.Unprotect(enc, Entropy, DataProtectionScope.CurrentUser);
            var json  = Encoding.UTF8.GetString(plain);
            return JsonSerializer.Deserialize<LicenseCache>(json);
        }
        catch
        {
            // Corrupted / wrong machine / tampering - treat as no license
            return null;
        }
    }

    // ------------------------------------------------------------------ delete

    public static void Delete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { /* non-fatal */ }
    }
}

// ---------------------------------------------------------------------------

/// <summary>
/// In-memory and on-disk representation of what we know about the current license.
/// This is the data the app caches between server calls.
/// </summary>
internal sealed class LicenseCache
{
    // --- state ---
    public string  Status          { get; set; } = LicenseStatus.Unknown;
    public string? ActivationToken { get; set; }  // server token (kept secret on disk, DPAPI-encrypted)
    public string? ExpiresOn       { get; set; }  // "YYYY-MM-DD" or null for trial
    public string? LicenseType     { get; set; }  // "paid" | "complimentary" | null for trial
    public int     DaysRemaining   { get; set; }

    // --- trial fields ---
    public bool    TrialActive       { get; set; }
    public int     TrialDaysRemaining { get; set; }

    // --- offline support ---
    /// <summary>UTC of the last successful server verification.</summary>
    public DateTimeOffset LastVerifiedUtc { get; set; } = DateTimeOffset.MinValue;
    /// <summary>Server time reported at last verification (to detect local clock rollback).</summary>
    public DateTimeOffset LastServerTimeUtc { get; set; } = DateTimeOffset.MinValue;

    // --- RSA-signed blob from server (to re-verify without network) ---
    public string? SignedBlob  { get; set; }
    public string? BlobSig     { get; set; }

    // --- payment flow ---
    public string? PendingPaymentRef   { get; set; }
    public string? PendingPaymentToken { get; set; }
}

/// <summary>Well-known status strings (mirrors server values).</summary>
internal static class LicenseStatus
{
    public const string Unknown        = "unknown";
    public const string TrialActive    = "trial_active";
    public const string TrialExpired   = "trial_expired";
    public const string Licensed       = "licensed";
    public const string Expired        = "license_expired";
    public const string Disabled       = "license_disabled";
    public const string NoInternet     = "no_internet";
    public const string Offline        = "offline_grace";
}
