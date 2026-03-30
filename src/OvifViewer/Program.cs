using Avalonia;
using LibVLCSharp.Shared;
using Microsoft.Extensions.DependencyInjection;
using OvifViewer.Services;
using Serilog;

namespace OvifViewer;

internal static class Program
{
    public static ServiceProvider Services { get; private set; } = null!;

    public static void Main(string[] args)
    {
        // ── Logging ───────────────────────────────────────────────────────────
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OvifViewer", "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(logDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        Log.Information("OvifViewer starting");

        // ── LibVLC ────────────────────────────────────────────────────────────
        Core.Initialize();
        var libVlc = new LibVLC(enableDebugLogs: false);

        // ── DI container ──────────────────────────────────────────────────────
        var services = new ServiceCollection();
        services.AddSingleton(libVlc);
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IRtspStreamService>(sp =>
            new RtspStreamService(sp.GetRequiredService<LibVLC>()));
        services.AddSingleton<IOnvifService, OnvifService>();
        services.AddSingleton<IDiscoveryService, WsDiscoveryService>();
        Services = services.BuildServiceProvider();

        // ── Load settings ─────────────────────────────────────────────────────
        var settings = Services.GetRequiredService<ISettingsService>();
        settings.Load();

        // ── Run Avalonia ──────────────────────────────────────────────────────
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            settings.Save();
            libVlc.Dispose();
            Log.Information("OvifViewer exiting");
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
