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

    /// <summary>
    /// Exports cameras to a portable JSON file with plaintext passwords
    /// so the file can be copied to another machine.
    /// </summary>
    public void ExportCameras(string path)
    {
        var portable = _settings.Cameras.Select(c => new PortableCameraEntry
        {
            Id                   = c.Id,
            DisplayName          = c.DisplayName,
            DeviceServiceUrl     = c.DeviceServiceUrl,
            Username             = c.Username,
            Password             = c.GetPassword(),
            SelectedProfileToken = c.SelectedProfileToken,
            RtspUri              = c.RtspUri,
            PtzEnabled           = c.PtzEnabled,
            AutoConnect          = c.AutoConnect,
        }).ToList();

        var json = JsonSerializer.Serialize(portable, JsonOptions);
        File.WriteAllText(path, json);
        Log.Information("Exported {Count} cameras to {Path}", portable.Count, path);
    }

    /// <summary>
    /// Reads a portable cameras JSON file and returns the camera configs
    /// (passwords re-encrypted with DPAPI for the current user).
    /// Cameras already present (by Id) are skipped.
    /// </summary>
    public IReadOnlyList<CameraConfig> ImportCameras(string path)
    {
        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<List<PortableCameraEntry>>(json, JsonOptions)
            ?? throw new InvalidDataException("File contained no camera data.");

        var existing = _settings.Cameras.Select(c => c.Id).ToHashSet();
        var added = new List<CameraConfig>();

        foreach (var e in entries)
        {
            if (existing.Contains(e.Id)) continue;

            var cam = new CameraConfig
            {
                Id                   = e.Id,
                DisplayName          = e.DisplayName,
                DeviceServiceUrl     = e.DeviceServiceUrl,
                Username             = e.Username,
                SelectedProfileToken = e.SelectedProfileToken,
                RtspUri              = e.RtspUri,
                PtzEnabled           = e.PtzEnabled,
                AutoConnect          = e.AutoConnect,
            };
            cam.SetPassword(e.Password);
            _settings.Cameras.Add(cam);
            added.Add(cam);
        }

        if (added.Count > 0) Save();
        Log.Information("Imported {Count} cameras from {Path}", added.Count, path);
        return added;
    }

    public void Dispose() => _debounceTimer.Dispose();
}

file record PortableCameraEntry
{
    public Guid   Id                   { get; init; }
    public string DisplayName          { get; init; } = "";
    public string DeviceServiceUrl     { get; init; } = "";
    public string Username             { get; init; } = "";
    public string Password             { get; init; } = "";
    public string SelectedProfileToken { get; init; } = "";
    public string RtspUri              { get; init; } = "";
    public bool   PtzEnabled           { get; init; }
    public bool   AutoConnect          { get; init; } = true;
}
