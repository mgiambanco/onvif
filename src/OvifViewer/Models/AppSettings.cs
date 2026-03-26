namespace OvifViewer.Models;

public class AppSettings
{
    public List<CameraConfig> Cameras { get; set; } = [];
    public List<CameraPanelLayout> PanelLayouts { get; set; } = [];
    public int GridPresetCols { get; set; } = 0;
    public string Theme { get; set; } = "Dark";
    public int MainWindowX { get; set; } = 100;
    public int MainWindowY { get; set; } = 100;
    public int MainWindowWidth { get; set; } = 1280;
    public int MainWindowHeight { get; set; } = 800;
    public bool MainWindowMaximized { get; set; }
}
