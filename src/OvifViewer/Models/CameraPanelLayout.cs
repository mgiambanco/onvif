namespace OvifViewer.Models;

public class CameraPanelLayout
{
    public Guid CameraId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 640;
    public int Height { get; set; } = 400;
    public int ZOrder { get; set; }
}
