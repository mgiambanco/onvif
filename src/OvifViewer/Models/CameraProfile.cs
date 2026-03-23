namespace OvifViewer.Models;

public class CameraProfile
{
    public string Token { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string Encoding { get; set; } = string.Empty;  // H264, H265, JPEG…
}
