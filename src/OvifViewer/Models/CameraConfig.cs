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

    /// <summary>DPAPI-encrypted password, Base64-encoded.</summary>
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
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        EncryptedPassword = Convert.ToBase64String(encrypted);
    }

    public string GetPassword()
    {
        if (string.IsNullOrEmpty(EncryptedPassword))
            return string.Empty;

        var encrypted = Convert.FromBase64String(EncryptedPassword);
        var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
