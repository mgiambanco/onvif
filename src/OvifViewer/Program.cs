using LibVLCSharp.Shared;
using Microsoft.Extensions.DependencyInjection;
using OvifViewer.Forms;
using OvifViewer.Services;
using Serilog;

namespace OvifViewer;

internal static class Program
{
    [STAThread]
    static void Main()
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

        // ── LibVLC (must init before Application.Run) ─────────────────────────
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
        services.AddSingleton<MainForm>();

        var provider = services.BuildServiceProvider();

        // ── Load settings ─────────────────────────────────────────────────────
        var settings = provider.GetRequiredService<ISettingsService>();
        settings.Load();

        // ── Run ───────────────────────────────────────────────────────────────
        ApplicationConfiguration.Initialize();

        try
        {
            Application.Run(provider.GetRequiredService<MainForm>());
        }
        finally
        {
            settings.Save();
            libVlc.Dispose();
            Log.Information("OvifViewer exiting");
            Log.CloseAndFlush();
        }
    }
}
