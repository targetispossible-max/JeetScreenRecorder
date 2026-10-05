using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JeetScreenRecorder.Licensing;

/// <summary>
/// Verifies the RSA-SHA256 signature that the server attaches to every
/// license/trial answer.  The PUBLIC key is embedded in the binary;
/// the private key never leaves the server.
///
/// Verification guarantees:
///   1. The response really came from our server (signed with the private key).
///   2. It was issued for THIS device (device_id inside the signed JSON matches).
///   3. It was a fresh response (nonce inside the signed JSON matches what we sent).
/// </summary>
internal static class SignatureVerifier
{
    // -----------------------------------------------------------------------
    // REPLACE THIS with your actual RSA-2048 PUBLIC KEY from
    //   php tools/generate_keys.php  (printed to console as PEM)
    // -----------------------------------------------------------------------
    private const string PublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "REPLACE_WITH_YOUR_ACTUAL_PUBLIC_KEY_PEM_HERE\n" +
        "-----END PUBLIC KEY-----";

    // -----------------------------------------------------------------------

    /// <summary>
    /// Verify the signed JSON blob + base64 signature returned by the server.
    /// Returns the inner payload dictionary, or throws if verification fails.
    /// </summary>
    public static Dictionary<string, JsonElement> VerifyAndParse(
        string signedJson,
        string signatureBase64,
        string expectedNonce,
        string expectedDeviceId)
    {
        // 1. Check RSA signature
        if (!RsaVerify(signedJson, signatureBase64))
            throw new LicenseException("Server response signature is invalid.");

        // 2. Parse inner JSON
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(signedJson)
                  ?? throw new LicenseException("Server response payload is empty.");

        // 3. Nonce check (replay protection)
        if (!doc.TryGetValue("nonce", out var nonceEl) || nonceEl.GetString() != expectedNonce)
            throw new LicenseException("Server response nonce mismatch.");

        // 4. Device-id check (response is tied to this PC)
        if (!doc.TryGetValue("device_id", out var devEl) || devEl.GetString() != expectedDeviceId)
            throw new LicenseException("Server response device mismatch.");

        return doc;
    }

    // -----------------------------------------------------------------------

    private static bool RsaVerify(string data, string signatureBase64)
    {
        try
        {
            var sig = Convert.FromBase64String(signatureBase64);
            var dataBytes = Encoding.UTF8.GetBytes(data);

            using var rsa = RSA.Create();
            rsa.ImportFromPem(PublicKeyPem.AsSpan());
            return rsa.VerifyData(dataBytes, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Thrown when the license server returns an error or verification fails.</summary>
internal sealed class LicenseException : Exception
{
    public LicenseException(string message) : base(message) { }
    public LicenseException(string message, Exception inner) : base(message, inner) { }
}
