using System.Text.Json;
using JeetScreenRecorder.Utils;

namespace JeetScreenRecorder.Licensing;

/// <summary>
/// The single source of truth for license state inside the app.
///
/// State machine (simplified):
///
///   App launch
///     ├─ No network first time → show "connect to internet" and block recording
///     ├─ Trial active          → allow recording, show days remaining
///     ├─ Trial expired         → block recording, show upgrade options
///     ├─ Licensed              → allow recording, show expiry date
///     ├─ License expired       → block recording, show renew option
///     └─ License disabled      → block recording, show contact support
///
/// Online verification happens:
///   • At startup (always attempted)
///   • Every 4 hours via heartbeat (if licensed)
///   • When the user enters a key
///
/// Offline grace: if the server is unreachable and the cached status is
/// "licensed", the app continues to work for <offlineGraceDays> days before
/// requiring a connection.
///
/// Clock-rollback protection: if local time is earlier than the last known
/// server time, we treat this as suspicious and force an online check.
/// </summary>
public sealed class LicenseService
{
    // -----------------------------------------------------------------------
    //  Constants
    // -----------------------------------------------------------------------
    private const int OfflineGraceDays      = 7;
    private const int HeartbeatIntervalHrs  = 4;
    private const int HeartbeatIntervalMs   = HeartbeatIntervalHrs * 60 * 60 * 1000;

    // -----------------------------------------------------------------------
    //  Fields
    // -----------------------------------------------------------------------
    private LicenseCache _cache;
    private readonly LicenseApiClient _api = new();
    private readonly System.Timers.Timer _heartbeatTimer;
    private bool _initialised;

    // -----------------------------------------------------------------------
    //  Singleton-ish access (registered in DI as singleton)
    // -----------------------------------------------------------------------
    public LicenseService()
    {
        _cache = LicenseStorage.Load() ?? new LicenseCache { Status = LicenseStatus.Unknown };

        _heartbeatTimer = new System.Timers.Timer(HeartbeatIntervalMs) { AutoReset = true };
        _heartbeatTimer.Elapsed += async (_, _) =>
        {
            if (_cache.Status == LicenseStatus.Licensed)
                await HeartbeatAsync(CancellationToken.None);
            else if (_cache.Status is LicenseStatus.TrialActive or LicenseStatus.TrialExpired)
                await RegisterOrVerifyTrialAsync(CancellationToken.None);   // keeps trial days / extensions / unblock in sync
        };
        _heartbeatTimer.Start();
    }

    // -----------------------------------------------------------------------
    //  Public state (read by ViewModel via ILicenseService)
    // -----------------------------------------------------------------------

    /// <summary>Current license status string (LicenseStatus constants).</summary>
    public string Status => EffectiveStatus;

    /// <summary>
    /// The cached status, except that an active trial turns into "trial expired" the moment its end time
    /// passes - even while the app is open or offline - so recording locks exactly when the trial ends.
    /// </summary>
    private string EffectiveStatus =>
        _cache.Status == LicenseStatus.TrialActive &&
        _cache.TrialExpiresUtc != DateTimeOffset.MinValue &&
        DateTimeOffset.UtcNow >= _cache.TrialExpiresUtc
            ? LicenseStatus.TrialExpired
            : _cache.Status;

    /// <summary>True if the user may press Start Recording.</summary>
    public bool CanRecord =>
        EffectiveStatus == LicenseStatus.Licensed ||
        EffectiveStatus == LicenseStatus.TrialActive ||
        EffectiveStatus == LicenseStatus.Offline;

    public bool IsLicensed  => EffectiveStatus == LicenseStatus.Licensed;
    public bool IsTrialActive => EffectiveStatus == LicenseStatus.TrialActive;
    public bool IsExpired   => EffectiveStatus is LicenseStatus.Expired or LicenseStatus.TrialExpired;

    /// <summary>True once the first startup check (success or failure) has finished.</summary>
    public bool CheckFinished { get; private set; }

    /// <summary>Last refusal text from the server (why a trial was blocked), if any.</summary>
    public string? ServerMessage => _cache.ServerMessage;

    public string? ExpiresOn      => _cache.ExpiresOn;
    public string? LicenseType    => _cache.LicenseType;
    public int     DaysRemaining  => _cache.DaysRemaining;

    /// <summary>Whole days left in the trial (counts down live from the server's trial end time).</summary>
    public int TrialDaysLeft =>
        _cache.TrialExpiresUtc == DateTimeOffset.MinValue
            ? _cache.TrialDaysRemaining
            : Math.Max(0, (int)Math.Ceiling((_cache.TrialExpiresUtc - DateTimeOffset.UtcNow).TotalDays));

    // Friendly one-line summary for the UI badge
    public string StatusLabel => EffectiveStatus switch
    {
        LicenseStatus.Licensed     => _cache.LicenseType == "complimentary"
                                        ? $"✔ Complimentary  •  Expires {FormatDate(_cache.ExpiresOn)}"
                                        : $"✔ Licensed  •  Expires {FormatDate(_cache.ExpiresOn)}",
        LicenseStatus.TrialActive  => $"⏳ Free Trial  •  {TrialDaysLeft} day(s) left",
        LicenseStatus.TrialExpired => "Free trial ended – Buy License",
        LicenseStatus.Expired      => "License expired – Buy License",
        LicenseStatus.Disabled     => "License Disabled",
        LicenseStatus.Offline      => $"✔ Licensed (offline)  •  Expires {FormatDate(_cache.ExpiresOn)}",
        LicenseStatus.NoInternet   => "No internet connection",
        _                          => CheckFinished ? "License not verified" : "Checking…"
    };

    // Payment reference kept during buy flow
    public (string Ref, string Token)? PendingPayment =>
        _cache.PendingPaymentRef != null && _cache.PendingPaymentToken != null
            ? (_cache.PendingPaymentRef, _cache.PendingPaymentToken)
            : null;

    // -----------------------------------------------------------------------
    //  Initialise (call once at startup from App.xaml.cs / ViewModel)
    // -----------------------------------------------------------------------
    public async Task InitialiseAsync(CancellationToken ct = default)
    {
        if (_initialised) return;
        _initialised = true;
        try   { await InitialiseCoreAsync(ct); }
        catch (Exception ex) { AppLogger.Error("License initialise failed", ex); }
        finally
        {
            CheckFinished = true;
            AppLogger.Info($"License check finished: status={Status}");
        }
    }

    private async Task InitialiseCoreAsync(CancellationToken ct)
    {
        // Clock-rollback check
        if (IsClockSuspicious())
        {
            _cache.Status = LicenseStatus.Unknown;
            LicenseStorage.Save(_cache);
        }

        // Already licensed: try online validate, fall back to offline grace
        if (_cache.Status == LicenseStatus.Licensed && _cache.ActivationToken != null)
        {
            await ValidateCachedAsync(ct);
            return;
        }

        // Trial or first run: register/verify trial
        await RegisterOrVerifyTrialAsync(ct);
    }

    // -----------------------------------------------------------------------
    //  Trial
    // -----------------------------------------------------------------------
    private async Task RegisterOrVerifyTrialAsync(CancellationToken ct)
    {
        try
        {
            var nonce = NewNonce();
            var devId = DeviceIdProvider.GetDeviceId();
            var instId = InstallationIdProvider.GetInstallationId();

            var resp = await _api.TrialRegisterAsync(instId, devId, nonce, ct);

            if (!resp.TryGetProperty("signed",    out var signedEl) ||
                !resp.TryGetProperty("signature", out var sigEl))
            {
                // Unsigned answer: the server is refusing (e.g. {"status":"trial_blocked","message":"..."}).
                var plainStatus = resp.ValueKind == JsonValueKind.Object ? (TryStr(resp, "status")  ?? "") : "";
                var plainMsg    = resp.ValueKind == JsonValueKind.Object ?  TryStr(resp, "message")        : null;
                AppLogger.Warn($"Trial reply had no signature: status='{plainStatus}', message='{plainMsg}'");
                if (MapTrialStatus(plainStatus) == LicenseStatus.TrialExpired)
                {
                    LockTrial(plainMsg);
                    return;
                }
                throw new LicenseException("Trial response missing signature.");
            }

            var payload = SignatureVerifier.VerifyAndParse(
                signedEl.GetString()!,
                sigEl.GetString()!,
                nonce,
                devId);

            var rawStatus = payload.TryGetValue("status", out var stEl) && stEl.ValueKind == JsonValueKind.String
                                ? stEl.GetString() ?? ""
                                : "";
            var status = MapTrialStatus(rawStatus);
            AppLogger.Info($"Trial status from server: '{rawStatus}' -> {status}");

            _cache.Status             = status;
            _cache.TrialActive        = status == LicenseStatus.TrialActive;
            _cache.ServerMessage      = status == LicenseStatus.TrialActive ? null : TryStrFromPayload(payload, "message");
            _cache.TrialDaysRemaining = payload.TryGetValue("days_remaining", out var dr) && dr.ValueKind == JsonValueKind.Number ? dr.GetInt32() : 0;
            _cache.TrialExpiresUtc    = payload.TryGetValue("trial_expires_at", out var te) &&
                                        te.ValueKind == JsonValueKind.String &&
                                        DateTimeOffset.TryParse(te.GetString(), out var teDto)
                                            ? teDto
                                            : DateTimeOffset.MinValue;
            _cache.LastVerifiedUtc    = DateTimeOffset.UtcNow;
            _cache.LastServerTimeUtc  = ExtractServerTime(payload);
            LicenseStorage.Save(_cache);
        }
        catch (LicenseException ex)
        {
            AppLogger.Warn($"Trial register failed (HTTP {ex.HttpStatus}): {ex.Message}");

            // The server answered and refused (blocked / forbidden) -> lock the trial, even if it was
            // active in the cache. This is what makes the admin "Block" button actually work.
            if (ex.IsServerRefusal)
            {
                LockTrial(ex.Message);
                return;
            }

            // Network problem: if we already had a cached trial status, keep it
            if (_cache.Status is LicenseStatus.TrialActive or LicenseStatus.TrialExpired) return;
            _cache.Status = LicenseStatus.NoInternet;
            // Do NOT save - let next launch retry
        }
        catch (Exception ex)
        {
            AppLogger.Error("Trial register unexpected error", ex);
            if (_cache.Status is LicenseStatus.TrialActive or LicenseStatus.TrialExpired) return;
            _cache.Status = LicenseStatus.NoInternet;
        }
    }

    /// <summary>Server trial status -> app status. Any "trial_*" other than trial_active (blocked, ended ...) locks recording.</summary>
    private static string MapTrialStatus(string raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        if (s == "trial_active") return LicenseStatus.TrialActive;
        if (s == "trial_expired" || s == "expired" || s == "blocked" || s == "disabled" ||
            s.Contains("block") || (s.StartsWith("trial_") && s != "trial_active"))
            return LicenseStatus.TrialExpired;
        return LicenseStatus.Unknown;
    }

    private void LockTrial(string? serverMessage)
    {
        _cache.Status        = LicenseStatus.TrialExpired;
        _cache.TrialActive   = false;
        _cache.ServerMessage = serverMessage;
        _cache.LastVerifiedUtc = DateTimeOffset.UtcNow;
        LicenseStorage.Save(_cache);
        AppLogger.Info("Trial locked by server (blocked or ended).");
    }

    // -----------------------------------------------------------------------
    //  License validation (cached token)
    // -----------------------------------------------------------------------
    private async Task ValidateCachedAsync(CancellationToken ct)
    {
        try
        {
            var nonce = NewNonce();
            var devId = DeviceIdProvider.GetDeviceId();
            var resp  = await _api.ValidateAsync(_cache.ActivationToken!, devId, nonce, ct);

            UpdateFromLicenseResponse(resp, nonce, devId);
        }
        catch (LicenseException)
        {
            // Network/server error → try offline grace
            ApplyOfflineGrace();
        }
        catch (Exception ex)
        {
            AppLogger.Error("License validate error", ex);
            ApplyOfflineGrace();
        }
    }

    // -----------------------------------------------------------------------
    //  Heartbeat (every 4 hours while app is open)
    // -----------------------------------------------------------------------
    private async Task HeartbeatAsync(CancellationToken ct)
    {
        if (_cache.ActivationToken == null) return;
        try
        {
            var nonce = NewNonce();
            var devId = DeviceIdProvider.GetDeviceId();
            var resp  = await _api.HeartbeatAsync(_cache.ActivationToken, devId, nonce, ct);
            UpdateFromLicenseResponse(resp, nonce, devId);
        }
        catch { /* silent - next heartbeat will retry */ }
    }

    // -----------------------------------------------------------------------
    //  Activate (user entered a key)
    // -----------------------------------------------------------------------
    public async Task<(bool Ok, string Message)> ActivateAsync(string rawKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return (false, "Please enter a license key.");

        // Normalise: strip spaces, uppercase
        var key = rawKey.Trim().ToUpperInvariant().Replace(" ", "-");

        try
        {
            var nonce  = NewNonce();
            var devId  = DeviceIdProvider.GetDeviceId();
            var instId = InstallationIdProvider.GetInstallationId();
            var name   = Environment.MachineName;

            var resp = await _api.ActivateAsync(key, instId, devId, name, nonce, ct);
            if (!resp.TryGetProperty("success", out var ok) || !ok.GetBoolean())
            {
                var msg = resp.TryGetProperty("message", out var m) ? m.GetString() : "Activation failed.";
                return (false, msg ?? "Activation failed.");
            }

            UpdateFromLicenseResponse(resp, nonce, devId);
            return (true, "License activated successfully! You can now use all features.");
        }
        catch (LicenseException ex) { return (false, ex.Message); }
        catch (Exception ex)
        {
            AppLogger.Error("Activate unexpected error", ex);
            return (false, "An unexpected error occurred. Please try again.");
        }
    }

    // -----------------------------------------------------------------------
    //  Deactivate (user releases this device)
    // -----------------------------------------------------------------------
    public async Task<(bool Ok, string Message)> DeactivateAsync(CancellationToken ct = default)
    {
        if (_cache.ActivationToken == null)
            return (false, "No active license found on this device.");

        try
        {
            var nonce = NewNonce();
            var devId = DeviceIdProvider.GetDeviceId();
            await _api.DeactivateAsync(_cache.ActivationToken, devId, nonce, ct);

            // Wipe local license, revert to trial check
            _cache.Status          = LicenseStatus.Unknown;
            _cache.ActivationToken = null;
            _cache.ExpiresOn       = null;
            _cache.LicenseType     = null;
            LicenseStorage.Save(_cache);

            // Re-run trial check so status is correct
            await RegisterOrVerifyTrialAsync(ct);
            return (true, "Device deactivated. You can now activate on another PC.");
        }
        catch (LicenseException ex) { return (false, ex.Message); }
        catch (Exception ex)
        {
            AppLogger.Error("Deactivate error", ex);
            return (false, "Deactivation failed. Check your internet connection.");
        }
    }

    // -----------------------------------------------------------------------
    //  Payment flow helpers
    // -----------------------------------------------------------------------

    /// <summary>Ask server to create a payment order. Returns the checkout URL.</summary>
    public async Task<(bool Ok, string UrlOrError, string? Ref, string? Token)> StartPaymentAsync(
        string name, string email, string mobile, string method, string? coupon,
        CancellationToken ct = default)
    {
        try
        {
            var resp = await _api.CreatePaymentAsync(name, email, mobile, method, coupon, ct);

            // Server returns reference, access_token, checkout_url (Razorpay) or upi details
            var reference   = TryStr(resp, "reference")    ?? TryStr(resp, "order_id") ?? "";
            var accessToken = TryStr(resp, "access_token") ?? "";
            // Razorpay -> "checkout_url"; UPI -> nested "upi": { "page_url": ... }
            var url         = TryStr(resp, "checkout_url") ?? "";
            if (string.IsNullOrEmpty(url) &&
                resp.TryGetProperty("upi", out var upi) && upi.ValueKind == JsonValueKind.Object)
                url = TryStr(upi, "page_url") ?? "";

            if (string.IsNullOrEmpty(url))
                return (false, "Server did not return a checkout URL.", null, null);

            // Cache for polling
            _cache.PendingPaymentRef   = reference;
            _cache.PendingPaymentToken = accessToken;
            LicenseStorage.Save(_cache);

            return (true, url, reference, accessToken);
        }
        catch (LicenseException ex) { return (false, ex.Message, null, null); }
        catch (Exception ex)
        {
            AppLogger.Error("StartPayment error", ex);
            return (false, "Could not start payment. Check your internet.", null, null);
        }
    }

    /// <summary>Poll payment status. On success, stores the license automatically.</summary>
    public async Task<(string Status, string Message, bool LicenseGranted)> PollPaymentStatusAsync(
        string reference, string accessToken, CancellationToken ct = default)
    {
        try
        {
            var resp   = await _api.GetPaymentStatusAsync(reference, accessToken, ct);
            var status = TryStr(resp, "status") ?? "";
            var msg    = TryStr(resp, "message") ?? status;

            if (status == "payment_paid")
            {
                // Server may return license_key for auto-activation
                var key = TryStr(resp, "license_key");
                if (!string.IsNullOrEmpty(key))
                {
                    var (ok, activateMsg) = await ActivateAsync(key, ct);
                    if (ok)
                    {
                        _cache.PendingPaymentRef   = null;
                        _cache.PendingPaymentToken = null;
                        LicenseStorage.Save(_cache);
                        return ("paid", "Payment successful! License activated.", true);
                    }
                }
                return ("paid", "Payment confirmed! Check your email for the license key.", false);
            }

            return (status.Replace("payment_", ""), msg, false);
        }
        catch (LicenseException ex) { return ("error", ex.Message, false); }
        catch (Exception ex)
        {
            AppLogger.Error("PollPayment error", ex);
            return ("error", "Could not check payment status.", false);
        }
    }

    // -----------------------------------------------------------------------
    //  Offline grace logic
    // -----------------------------------------------------------------------
    private void ApplyOfflineGrace()
    {
        if (_cache.Status != LicenseStatus.Licensed) return;

        var daysSinceVerify = (DateTimeOffset.UtcNow - _cache.LastVerifiedUtc).TotalDays;
        if (daysSinceVerify <= OfflineGraceDays)
        {
            _cache.Status = LicenseStatus.Offline;
            // Do NOT persist (so next online check restores to Licensed)
        }
        else
        {
            // Grace expired → require online check, block recording
            _cache.Status = LicenseStatus.Unknown;
            LicenseStorage.Save(_cache);
        }
    }

    // -----------------------------------------------------------------------
    //  Clock rollback detection
    // -----------------------------------------------------------------------
    private bool IsClockSuspicious()
    {
        if (_cache.LastServerTimeUtc == DateTimeOffset.MinValue) return false;
        // If current local clock is more than 2 minutes BEFORE last server time → suspicious
        return DateTimeOffset.UtcNow < _cache.LastServerTimeUtc.AddMinutes(-2);
    }

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------
    private void UpdateFromLicenseResponse(JsonElement resp, string nonce, string devId)
    {
        if (!resp.TryGetProperty("signed",    out var signedEl) ||
            !resp.TryGetProperty("signature", out var sigEl))
            throw new LicenseException("Server response missing signature.");

        var payload = SignatureVerifier.VerifyAndParse(
            signedEl.GetString()!,
            sigEl.GetString()!,
            nonce,
            devId);

        var status = MapServerStatus(payload["status"].GetString() ?? "");
        _cache.Status          = status;
        _cache.ActivationToken = TryStrFromPayload(payload, "activation_token") ?? _cache.ActivationToken;
        _cache.ExpiresOn       = TryStrFromPayload(payload, "expires_on");
        _cache.LicenseType     = TryStrFromPayload(payload, "license_type");
        _cache.DaysRemaining   = payload.TryGetValue("days_remaining", out var dr) ? dr.GetInt32() : 0;
        _cache.SignedBlob      = signedEl.GetString();
        _cache.BlobSig         = sigEl.GetString();
        _cache.LastVerifiedUtc  = DateTimeOffset.UtcNow;
        _cache.LastServerTimeUtc = ExtractServerTime(payload);

        LicenseStorage.Save(_cache);
    }

    /// <summary>
    /// The server names license states active / expired / disabled / not_activated / deactivated;
    /// the app uses its own LicenseStatus constants. Without this mapping a successful
    /// activation ("active") would never be recognised as Licensed.
    /// </summary>
    private static string MapServerStatus(string serverStatus) => serverStatus switch
    {
        "active"        => LicenseStatus.Licensed,
        "expired"       => LicenseStatus.Expired,
        "disabled"      => LicenseStatus.Disabled,
        "trial_active"  => LicenseStatus.TrialActive,
        "trial_expired" => LicenseStatus.TrialExpired,
        _               => LicenseStatus.Unknown,   // not_activated, deactivated, anything new
    };

    private static string NewNonce() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string? TryStr(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) ? v.GetString() : null;

    private static string? TryStrFromPayload(Dictionary<string, JsonElement> d, string key) =>
        d.TryGetValue(key, out var v) ? v.GetString() : null;

    // server_time lives inside the SIGNED payload (the plain top-level fields carry no time).
    private static DateTimeOffset ExtractServerTime(Dictionary<string, JsonElement> payload)
    {
        if (payload.TryGetValue("server_time", out var st) &&
            st.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(st.GetString(), out var dto))
            return dto;
        return DateTimeOffset.UtcNow;
    }

    private static string FormatDate(string? iso)
    {
        if (iso == null) return "";
        if (DateOnly.TryParse(iso, out var d))
            return d.ToString("dd-MM-yyyy");
        return iso;
    }
}
