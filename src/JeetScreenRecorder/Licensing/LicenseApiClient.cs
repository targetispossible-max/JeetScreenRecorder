using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace JeetScreenRecorder.Licensing;

/// <summary>
/// Thin HTTP wrapper around the license server API.
/// All amounts, dates and pricing come from the server; this class just
/// serialises / deserialises and handles network errors.
/// </summary>
internal sealed class LicenseApiClient : IDisposable
{
    // -----------------------------------------------------------------------
    // REPLACE with your actual Hostinger subdomain after deploying the server.
    // -----------------------------------------------------------------------
    public const string ServerBase = "https://license.YOURDOMAIN.com";

    // App version reported to the server (used for admin info only, not gating)
    public static string AppVersion => "0.2.0";

    // -----------------------------------------------------------------------

    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", "JeetScreenRecorder/" + AppVersion } }
    };

    // -----------------------------------------------------------------------  API calls

    public async Task<JsonElement> TrialRegisterAsync(
        string installationId, string deviceId, string nonce,
        CancellationToken ct = default)
    {
        return await PostAsync("/api/trial/register", new
        {
            installation_id = installationId,
            device_id       = deviceId,
            app_version     = AppVersion,
            nonce           = nonce,
        }, ct);
    }

    public async Task<JsonElement> ActivateAsync(
        string licenseKey, string installationId, string deviceId,
        string deviceName, string nonce,
        CancellationToken ct = default)
    {
        return await PostAsync("/api/license/activate", new
        {
            license_key     = licenseKey,
            installation_id = installationId,
            device_id       = deviceId,
            device_name     = deviceName,
            app_version     = AppVersion,
            nonce           = nonce,
        }, ct);
    }

    public async Task<JsonElement> ValidateAsync(
        string activationToken, string deviceId, string nonce,
        CancellationToken ct = default)
    {
        return await PostAsync("/api/license/validate", new
        {
            activation_token = activationToken,
            device_id        = deviceId,
            app_version      = AppVersion,
            nonce            = nonce,
        }, ct);
    }

    public async Task<JsonElement> HeartbeatAsync(
        string activationToken, string deviceId, string nonce,
        CancellationToken ct = default)
    {
        return await PostAsync("/api/license/heartbeat", new
        {
            activation_token = activationToken,
            device_id        = deviceId,
            app_version      = AppVersion,
            nonce            = nonce,
        }, ct);
    }

    public async Task<JsonElement> DeactivateAsync(
        string activationToken, string deviceId, string nonce,
        CancellationToken ct = default)
    {
        return await PostAsync("/api/license/deactivate", new
        {
            activation_token = activationToken,
            device_id        = deviceId,
            app_version      = AppVersion,
            nonce            = nonce,
        }, ct);
    }

    public async Task<JsonElement> ValidateCouponAsync(
        string code, string? email,
        CancellationToken ct = default)
    {
        return await PostAsync("/api/coupon/validate", new
        {
            code  = code,
            email = email,
        }, ct);
    }

    public async Task<JsonElement> CreatePaymentAsync(
        string name, string email, string mobile, string method,
        string? couponCode,
        CancellationToken ct = default)
    {
        return await PostAsync("/api/payment/create", new
        {
            name         = name,
            email        = email,
            mobile       = mobile,
            method       = method,
            coupon_code  = couponCode,
        }, ct);
    }

    public async Task<JsonElement> GetPaymentStatusAsync(
        string reference, string accessToken,
        CancellationToken ct = default)
    {
        return await PostAsync("/api/payment/status", new
        {
            reference    = reference,
            access_token = accessToken,
        }, ct);
    }

    // -----------------------------------------------------------------------  low-level

    private static async Task<JsonElement> PostAsync(string path, object body, CancellationToken ct)
    {
        var json    = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync(ServerBase + path, content, ct);
        }
        catch (TaskCanceledException)   { throw new LicenseException("Connection timed out. Check your internet."); }
        catch (HttpRequestException ex) { throw new LicenseException("Cannot reach license server: " + ex.Message); }

        var text = await resp.Content.ReadAsStringAsync(ct);
        JsonElement root;
        try   { root = JsonSerializer.Deserialize<JsonElement>(text); }
        catch { throw new LicenseException("Server returned unexpected response."); }

        // surface server-level errors (non-200) as LicenseException
        if (!resp.IsSuccessStatusCode)
        {
            var msg = "Server error.";
            if (root.TryGetProperty("message", out var m)) msg = m.GetString() ?? msg;
            throw new LicenseException(msg);
        }

        return root;
    }

    public void Dispose() { }   // _http is static / shared
}
