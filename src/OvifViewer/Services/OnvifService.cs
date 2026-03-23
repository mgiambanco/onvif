using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OvifViewer.Models;
using Serilog;

namespace OvifViewer.Services;

public class OnvifService : IOnvifService
{
    private static readonly XNamespace TDS = "http://www.onvif.org/ver10/device/wsdl";
    private static readonly XNamespace TRT = "http://www.onvif.org/ver10/media/wsdl";
    private static readonly XNamespace TT  = "http://www.onvif.org/ver10/schema";
    private static readonly XNamespace PTZ = "http://www.onvif.org/ver20/ptz/wsdl";

    public async Task<DiscoveredCamera> GetDeviceInfoAsync(
        string deviceServiceUrl, string username, string password)
    {
        var body = await OnvifSoap.PostAsync(deviceServiceUrl,
            "http://www.onvif.org/ver10/device/wsdl/GetDeviceInformation",
            "<tds:GetDeviceInformation xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\"/>",
            username, password).ConfigureAwait(false);

        return new DiscoveredCamera
        {
            DeviceServiceUrl = deviceServiceUrl,
            DisplayName      = $"{body.Element(TDS + "Manufacturer")?.Value} {body.Element(TDS + "Model")?.Value}".Trim(),
            Manufacturer     = body.Element(TDS + "Manufacturer")?.Value ?? "",
            Model            = body.Element(TDS + "Model")?.Value ?? "",
            FirmwareVersion  = body.Element(TDS + "FirmwareVersion")?.Value ?? "",
        };
    }

    public async Task<IReadOnlyList<CameraProfile>> GetProfilesAsync(CameraConfig camera)
    {
        Log.Debug("GetProfilesAsync: {Url} user={User}", camera.DeviceServiceUrl, camera.Username);
        try
        {
            var mediaUrl = await GetServiceUriAsync(camera, "http://www.onvif.org/ver10/media/wsdl")
                .ConfigureAwait(false);

            var body = await OnvifSoap.PostAsync(mediaUrl,
                "http://www.onvif.org/ver10/media/wsdl/GetProfiles",
                "<trt:GetProfiles xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\"/>",
                camera.Username, camera.GetPassword(),
                deviceUrl: camera.DeviceServiceUrl).ConfigureAwait(false);

            return body.Elements(TRT + "Profiles").Select(p => new CameraProfile
            {
                Token    = p.Attribute("token")?.Value ?? "",
                Name     = p.Element(TT + "Name")?.Value ?? "",
                Width    = int.TryParse(p.Element(TT + "VideoEncoderConfiguration")
                               ?.Element(TT + "Resolution")?.Element(TT + "Width")?.Value, out var w) ? w : 0,
                Height   = int.TryParse(p.Element(TT + "VideoEncoderConfiguration")
                               ?.Element(TT + "Resolution")?.Element(TT + "Height")?.Value, out var h) ? h : 0,
                Encoding = p.Element(TT + "VideoEncoderConfiguration")
                               ?.Element(TT + "Encoding")?.Value ?? "",
            }).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GetProfilesAsync failed for {Url}", camera.DeviceServiceUrl);
            throw;
        }
    }

    public async Task<string> GetStreamUriAsync(CameraConfig camera)
    {
        try
        {
            var mediaUrl = await GetServiceUriAsync(camera, "http://www.onvif.org/ver10/media/wsdl")
                .ConfigureAwait(false);

            var body = await OnvifSoap.PostAsync(mediaUrl,
                "http://www.onvif.org/ver10/media/wsdl/GetStreamUri",
                $"""
                <trt:GetStreamUri xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                                  xmlns:tt="http://www.onvif.org/ver10/schema">
                  <trt:StreamSetup>
                    <tt:Stream>RTP-Unicast</tt:Stream>
                    <tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport>
                  </trt:StreamSetup>
                  <trt:ProfileToken>{SecurityElement.Escape(camera.SelectedProfileToken)}</trt:ProfileToken>
                </trt:GetStreamUri>
                """,
                camera.Username, camera.GetPassword(),
                deviceUrl: camera.DeviceServiceUrl).ConfigureAwait(false);

            return body.Element(TRT + "MediaUri")?.Element(TT + "Uri")?.Value
                ?? body.Descendants(TT + "Uri").FirstOrDefault()?.Value
                ?? throw new Exception("No URI in GetStreamUri response");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GetStreamUriAsync failed for {Url}", camera.DeviceServiceUrl);
            throw;
        }
    }

    public async Task<bool> HasPtzAsync(CameraConfig camera)
    {
        try
        {
            var services = await GetServicesAsync(camera).ConfigureAwait(false);
            return services.ContainsKey("http://www.onvif.org/ver20/ptz/wsdl");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "PTZ check failed for {Url}", camera.DeviceServiceUrl);
            return false;
        }
    }

    public async Task PtzMoveAsync(CameraConfig camera, float panSpeed, float tiltSpeed, float zoomSpeed)
    {
        var ptzUrl = await GetServiceUriAsync(camera, "http://www.onvif.org/ver20/ptz/wsdl")
            .ConfigureAwait(false);

        await OnvifSoap.PostAsync(ptzUrl,
            "http://www.onvif.org/ver20/ptz/wsdl/ContinuousMove",
            $"""
            <tptz:ContinuousMove xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl"
                                 xmlns:tt="http://www.onvif.org/ver10/schema">
              <tptz:ProfileToken>{SecurityElement.Escape(camera.SelectedProfileToken)}</tptz:ProfileToken>
              <tptz:Velocity>
                <tt:PanTilt x="{panSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                            y="{tiltSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture)}"/>
                <tt:Zoom x="{zoomSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture)}"/>
              </tptz:Velocity>
            </tptz:ContinuousMove>
            """,
            camera.Username, camera.GetPassword()).ConfigureAwait(false);
    }

    public async Task PtzStopAsync(CameraConfig camera)
    {
        var ptzUrl = await GetServiceUriAsync(camera, "http://www.onvif.org/ver20/ptz/wsdl")
            .ConfigureAwait(false);

        await OnvifSoap.PostAsync(ptzUrl,
            "http://www.onvif.org/ver20/ptz/wsdl/Stop",
            $"""
            <tptz:Stop xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl">
              <tptz:ProfileToken>{SecurityElement.Escape(camera.SelectedProfileToken)}</tptz:ProfileToken>
              <tptz:PanTilt>true</tptz:PanTilt>
              <tptz:Zoom>true</tptz:Zoom>
            </tptz:Stop>
            """,
            camera.Username, camera.GetPassword()).ConfigureAwait(false);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<Dictionary<string, string>> GetServicesAsync(CameraConfig camera)
    {
        var body = await OnvifSoap.PostAsync(
            camera.DeviceServiceUrl,
            "http://www.onvif.org/ver10/device/wsdl/GetServices",
            "<tds:GetServices xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\"><tds:IncludeCapability>false</tds:IncludeCapability></tds:GetServices>",
            camera.Username, camera.GetPassword(),
            deviceUrl: camera.DeviceServiceUrl).ConfigureAwait(false);

        return body.Elements(TDS + "Service")
            .GroupBy(s => s.Element(TDS + "Namespace")?.Value ?? "")
            .ToDictionary(g => g.Key, g => g.First().Element(TDS + "XAddr")?.Value ?? "",
                StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<string> GetServiceUriAsync(CameraConfig camera, string ns)
    {
        var services = await GetServicesAsync(camera).ConfigureAwait(false);
        return services.TryGetValue(ns, out var url) ? url
            : throw new NotSupportedException($"Camera does not support {ns}");
    }
}

// ── Raw SOAP over HttpClient ──────────────────────────────────────────────────

/// <summary>
/// Sends SOAP 1.1 requests and parses responses without WCF, so SOAP version
/// mismatches between request and response are handled gracefully.
/// Syncs to the camera's clock before building WS-UsernameToken to avoid
/// "time check failed" rejections when the PC clock differs from the camera.
/// </summary>
internal static class OnvifSoap
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Cache clock offset per camera host so we only sync once per session
    private static readonly Dictionary<string, TimeSpan> _clockOffsets = new();

    public static async Task<XElement> PostAsync(
        string url, string soapAction, string bodyXml, string username, string password,
        string? deviceUrl = null)
    {
        var offset = await GetClockOffsetAsync(deviceUrl ?? url).ConfigureAwait(false);
        var envelope = BuildEnvelope(bodyXml, username, password, offset);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml"),
        };
        req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{soapAction}\"");

        using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        var xml = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { throw new Exception($"Non-XML response ({(int)resp.StatusCode}): {xml[..Math.Min(200, xml.Length)]}"); }

        // Surface SOAP faults
        var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
        if (fault != null)
            throw new Exception($"SOAP Fault: {fault}");

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)resp.StatusCode}: {xml[..Math.Min(200, xml.Length)]}");

        var bodyEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Body")
            ?? throw new Exception("No SOAP Body in response");

        return bodyEl.Elements().FirstOrDefault()
            ?? throw new Exception("Empty SOAP Body in response");
    }

    private static async Task<TimeSpan> GetClockOffsetAsync(string url)
    {
        var host = new Uri(url).GetLeftPart(UriPartial.Authority);
        lock (_clockOffsets)
            if (_clockOffsets.TryGetValue(host, out var cached)) return cached;

        try
        {
            // GetSystemDateAndTime requires no authentication per ONVIF spec
            var req = """
                <?xml version="1.0" encoding="utf-8"?>
                <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
                  <s:Header/><s:Body>
                    <tds:GetSystemDateAndTime xmlns:tds="http://www.onvif.org/ver10/device/wsdl"/>
                  </s:Body>
                </s:Envelope>
                """;
            using var msg = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(req, Encoding.UTF8, "text/xml"),
            };
            msg.Headers.TryAddWithoutValidation("SOAPAction",
                "\"http://www.onvif.org/ver10/device/wsdl/GetSystemDateAndTime\"");

            using var resp = await _http.SendAsync(msg).ConfigureAwait(false);
            var xml = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var doc = XDocument.Parse(xml);

            XNamespace tt = "http://www.onvif.org/ver10/schema";
            var utc = doc.Descendants(tt + "UTCDateTime").FirstOrDefault();
            if (utc != null)
            {
                var d = utc.Element(tt + "Date")!;
                var t = utc.Element(tt + "Time")!;
                var cameraUtc = new DateTime(
                    int.Parse(d.Element(tt + "Year")!.Value),
                    int.Parse(d.Element(tt + "Month")!.Value),
                    int.Parse(d.Element(tt + "Day")!.Value),
                    int.Parse(t.Element(tt + "Hour")!.Value),
                    int.Parse(t.Element(tt + "Minute")!.Value),
                    int.Parse(t.Element(tt + "Second")!.Value),
                    DateTimeKind.Utc);

                var offset = cameraUtc - DateTime.UtcNow;
                lock (_clockOffsets) _clockOffsets[host] = offset;
                return offset;
            }
        }
        catch { /* fall through — use local time */ }

        lock (_clockOffsets) _clockOffsets[new Uri(url).GetLeftPart(UriPartial.Authority)] = TimeSpan.Zero;
        return TimeSpan.Zero;
    }

    private static string BuildEnvelope(string bodyXml, string username, string password, TimeSpan clockOffset)
    {
        var nonce    = Guid.NewGuid().ToByteArray();
        var created  = (DateTime.UtcNow + clockOffset).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var digest   = SHA1.HashData([..nonce, ..Encoding.UTF8.GetBytes(created), ..Encoding.UTF8.GetBytes(password)]);

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Header>
                <wsse:Security xmlns:wsse="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd"
                               xmlns:wsu="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd">
                  <wsse:UsernameToken>
                    <wsse:Username>{SecurityElement.Escape(username)}</wsse:Username>
                    <wsse:Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest">{Convert.ToBase64String(digest)}</wsse:Password>
                    <wsse:Nonce EncodingType="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary">{Convert.ToBase64String(nonce)}</wsse:Nonce>
                    <wsu:Created>{created}</wsu:Created>
                  </wsse:UsernameToken>
                </wsse:Security>
              </s:Header>
              <s:Body>
                {bodyXml}
              </s:Body>
            </s:Envelope>
            """;
    }
}
