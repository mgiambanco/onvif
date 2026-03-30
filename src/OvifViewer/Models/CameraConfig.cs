using System.Security.Cryptography;
using System.Text;

namespace OvifViewer.Models;

public class CameraConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>ONVIF device service endpoint, e.g. http://192.168.1.100/onvif/device_service</summary>
    public string DeviceServiceUrl { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted password. Format: "dpapi:base64" on Windows, "aes:base64" on other platforms.
    /// Legacy (no prefix): treated as DPAPI on Windows.
    /// </summary>
    public string EncryptedPassword { get; set; } = string.Empty;

    /// <summary>Selected ONVIF media profile token.</summary>
    public string SelectedProfileToken { get; set; } = string.Empty;

    /// <summary>Cached RTSP URI — re-fetched from ONVIF if empty.</summary>
    public string RtspUri { get; set; } = string.Empty;

    public bool PtzEnabled { get; set; }
    public bool AutoConnect { get; set; } = true;
    public bool Muted { get; set; } = true;

    // ── Credential helpers ────────────────────────────────────────────────────

    public void SetPassword(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) { EncryptedPassword = string.Empty; return; }

        if (OperatingSystem.IsWindows())
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            EncryptedPassword = "dpapi:" + Convert.ToBase64String(encrypted);
        }
        else
        {
            EncryptedPassword = "aes:" + AesEncrypt(plaintext);
        }
    }

    public string GetPassword()
    {
        if (string.IsNullOrEmpty(EncryptedPassword)) return string.Empty;

        if (EncryptedPassword.StartsWith("aes:"))
            return AesDecrypt(EncryptedPassword[4..]);

        // "dpapi:" prefix or legacy (no prefix) — Windows DPAPI
        var b64 = EncryptedPassword.StartsWith("dpapi:")
            ? EncryptedPassword[6..]
            : EncryptedPassword;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var encrypted = Convert.FromBase64String(b64);
                var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return string.Empty; }
        }

        return string.Empty; // DPAPI ciphertext is not portable to non-Windows
    }

    // ── Cross-platform AES-256-GCM ────────────────────────────────────────────
    // Key is derived from machine + user identity — not exportable across machines,
    // but avoids a separate key-management dependency.

    private static byte[] DeriveKey()
    {
        var raw = $"{Environment.MachineName}:{Environment.UserName}:OvifViewer-v1";
        return SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    }

    private static string AesEncrypt(string plaintext)
    {
        var key = DeriveKey();
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var ptBytes = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[ptBytes.Length];

        using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, ptBytes, ct, tag);

        var combined = new byte[nonce.Length + tag.Length + ct.Length];
        nonce.CopyTo(combined, 0);
        tag.CopyTo(combined, nonce.Length);
        ct.CopyTo(combined, nonce.Length + tag.Length);
        return Convert.ToBase64String(combined);
    }

    private static string AesDecrypt(string b64)
    {
        try
        {
            var combined = Convert.FromBase64String(b64);
            int ns = AesGcm.NonceByteSizes.MaxSize, ts = AesGcm.TagByteSizes.MaxSize;
            var nonce = combined[..ns];
            var tag = combined[ns..(ns + ts)];
            var ct = combined[(ns + ts)..];
            var pt = new byte[ct.Length];

            var key = DeriveKey();
            using var aes = new AesGcm(key, ts);
            aes.Decrypt(nonce, ct, tag, pt);
            return Encoding.UTF8.GetString(pt);
        }
        catch { return string.Empty; }
    }
}
