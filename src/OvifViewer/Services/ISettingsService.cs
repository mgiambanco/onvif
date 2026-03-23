using OvifViewer.Models;

namespace OvifViewer.Services;

public interface ISettingsService
{
    AppSettings Settings { get; }
    void Load();
    void Save();
    void SaveDebounced();
}
