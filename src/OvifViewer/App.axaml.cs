using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using OvifViewer.Services;
using OvifViewer.Views;

namespace OvifViewer;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(
                Program.Services.GetRequiredService<ISettingsService>(),
                Program.Services.GetRequiredService<IRtspStreamService>(),
                Program.Services.GetRequiredService<IOnvifService>(),
                Program.Services.GetRequiredService<IDiscoveryService>());
        }
        base.OnFrameworkInitializationCompleted();
    }
}
