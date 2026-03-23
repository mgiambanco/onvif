using System.Text.Json;
using OvifViewer.Models;
using Serilog;

namespace OvifViewer.Services;

public class SettingsService : ISettingsService, IDisposable
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OvifViewer");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly System.Threading.Timer _debounceTimer;
    private AppSettings _settings = new();

    public AppSettings Settings => _settings;

    public SettingsService()
    {
        _debounceTimer = new System.Threading.Timer(_ => Save(), null,
            Timeout.Infinite, Timeout.Infinite);
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                _settings = new AppSettings();
                return;
            }

            var json = File.ReadAllText(SettingsPath);
            _settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            Log.Information("Settings loaded from {Path}", SettingsPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load settings — using defaults");
            _settings = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(_settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
            Log.Debug("Settings saved to {Path}", SettingsPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings");
        }
    }

    /// <summary>Debounced save — fires 500 ms after the last call.</summary>
    public void SaveDebounced() =>
        _debounceTimer.Change(500, Timeout.Infinite);

    public void Dispose() => _debounceTimer.Dispose();
}
