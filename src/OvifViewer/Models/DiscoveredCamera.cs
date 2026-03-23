namespace OvifViewer.Models;

public class DiscoveredCamera
{
    public string DeviceServiceUrl { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string FirmwareVersion { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = [];

    public override string ToString() =>
        string.IsNullOrEmpty(DisplayName) ? DeviceServiceUrl : $"{DisplayName} ({DeviceServiceUrl})";
}
