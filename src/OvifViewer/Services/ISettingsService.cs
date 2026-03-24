using OvifViewer.Models;

namespace OvifViewer.Services;

public interface ISettingsService
{
    AppSettings Settings { get; }
    void Load();
    void Save();
    void SaveDebounced();
    void ExportCameras(string path);
    IReadOnlyList<CameraConfig> ImportCameras(string path);
}
