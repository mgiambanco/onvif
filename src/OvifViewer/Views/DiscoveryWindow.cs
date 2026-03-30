using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OvifViewer.Models;
using OvifViewer.Services;
using Serilog;

namespace OvifViewer.Views;

public class DiscoveryWindow : Window
{
    private readonly IDiscoveryService _discovery;
    private readonly IOnvifService _onvif;
    private readonly ISettingsService _settings;

    private readonly ListBox _list;
    private readonly Button _scanBtn;
    private readonly Button _addBtn;
    private readonly TextBlock _statusLabel;
    private readonly ProgressBar _progress;
    private CancellationTokenSource? _cts;

    public List<CameraConfig> SelectedCameras { get; } = [];

    public DiscoveryWindow(IDiscoveryService discovery, IOnvifService onvif, ISettingsService settings)
    {
        _discovery = discovery;
        _onvif = onvif;
        _settings = settings;

        Title = "Discover ONVIF Cameras";
        Width = 700;
        Height = 480;
        MinWidth = 600;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list = new ListBox { SelectionMode = SelectionMode.Multiple };

        _progress = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 6,
            IsVisible = false,
            Margin = new Thickness(0, 0, 0, 2),
        };
        _statusLabel = new TextBlock
        {
            Text = "Press Scan to discover cameras on your network.",
            Margin = new Thickness(0, 4),
        };

        _scanBtn = new Button { Content = "Scan Network", Width = 120 };
        _addBtn = new Button { Content = "Add Selected", Width = 120, IsEnabled = false };
        var cancelBtn = new Button { Content = "Cancel", Width = 80 };

        _scanBtn.Click += OnScanClick;
        _addBtn.Click += OnAddClick;
        cancelBtn.Click += (_, _) => Close();

        _list.SelectionChanged += (_, _) =>
            _addBtn.IsEnabled = _list.SelectedItems?.Count > 0;

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(_addBtn);
        btnRow.Children.Add(_scanBtn);

        var bottom = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        bottom.Children.Add(_progress);
        bottom.Children.Add(_statusLabel);
        bottom.Children.Add(btnRow);

        var root = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = _list });

        Content = root;
    }

    private async void OnScanClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _list.Items.Clear();
        _scanBtn.IsEnabled = false;
        _progress.IsVisible = true;
        _statusLabel.Text = "Scanning…";
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        var progress = new Progress<DiscoveredCamera>(cam =>
            Dispatcher.UIThread.Post(() => AddToList(cam)));

        try
        {
            var cameras = await _discovery.DiscoverAsync(3000, progress, _cts.Token);

            _statusLabel.Text = $"Found {cameras.Count} device(s). Fetching details…";
            foreach (var cam in cameras)
            {
                if (!string.IsNullOrEmpty(cam.Manufacturer)) continue;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var info = await _onvif.GetDeviceInfoAsync(cam.DeviceServiceUrl, "", "");
                        cam.Manufacturer = info.Manufacturer;
                        cam.Model = info.Model;
                        cam.FirmwareVersion = info.FirmwareVersion;
                        if (string.IsNullOrEmpty(cam.DisplayName)) cam.DisplayName = info.DisplayName;
                        Dispatcher.UIThread.Post(() => RefreshItem(cam));
                    }
                    catch { /* credentials required — skip enrichment */ }
                });
            }
            _statusLabel.Text = $"Scan complete — {cameras.Count} camera(s) found.";
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Discovery scan failed");
            _statusLabel.Text = $"Scan failed: {ex.Message}";
        }
        finally
        {
            _scanBtn.IsEnabled = true;
            _progress.IsVisible = false;
        }
    }

    private void AddToList(DiscoveredCamera cam)
    {
        _list.Items.Add(new DiscoveryListItem(cam));
    }

    private void RefreshItem(DiscoveredCamera cam)
    {
        foreach (var item in _list.Items.OfType<DiscoveryListItem>())
        {
            if (item.Camera.DeviceServiceUrl == cam.DeviceServiceUrl)
            {
                item.Refresh(cam);
                break;
            }
        }
    }

    private async void OnAddClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_list.SelectedItems == null) return;

        foreach (var selected in _list.SelectedItems.OfType<DiscoveryListItem>().ToList())
        {
            var discovered = selected.Camera;

            if (_settings.Settings.Cameras.Any(c =>
                c.DeviceServiceUrl.Equals(discovered.DeviceServiceUrl, StringComparison.OrdinalIgnoreCase)))
                continue;

            var win = new CameraSettingsWindow(new CameraConfig
            {
                DeviceServiceUrl = discovered.DeviceServiceUrl,
                DisplayName = string.IsNullOrEmpty(discovered.DisplayName)
                    ? discovered.DeviceServiceUrl : discovered.DisplayName,
            }, _onvif);

            await win.ShowDialog(this);
            if (win.Result == null) continue;

            _settings.Settings.Cameras.Add(win.Result);
            SelectedCameras.Add(win.Result);
        }

        if (SelectedCameras.Count > 0)
        {
            _settings.Save();
            Close(SelectedCameras);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _cts?.Cancel();
        base.OnClosing(e);
    }
}

file class DiscoveryListItem : ListBoxItem
{
    public DiscoveredCamera Camera { get; }

    private readonly TextBlock _nameBlock;
    private readonly TextBlock _mfrBlock;
    private readonly TextBlock _modelBlock;
    private readonly TextBlock _fwBlock;

    public DiscoveryListItem(DiscoveredCamera cam)
    {
        Camera = cam;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(280, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(130, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(130, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        _nameBlock = new TextBlock { Text = cam.DeviceServiceUrl, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _mfrBlock  = new TextBlock { Text = cam.Manufacturer, VerticalAlignment = VerticalAlignment.Center };
        _modelBlock = new TextBlock { Text = cam.Model, VerticalAlignment = VerticalAlignment.Center };
        _fwBlock   = new TextBlock { Text = cam.FirmwareVersion, VerticalAlignment = VerticalAlignment.Center };

        Grid.SetColumn(_nameBlock,  0); grid.Children.Add(_nameBlock);
        Grid.SetColumn(_mfrBlock,   1); grid.Children.Add(_mfrBlock);
        Grid.SetColumn(_modelBlock, 2); grid.Children.Add(_modelBlock);
        Grid.SetColumn(_fwBlock,    3); grid.Children.Add(_fwBlock);

        Content = grid;
        Padding = new Thickness(4, 2);
    }

    public void Refresh(DiscoveredCamera cam)
    {
        _mfrBlock.Text   = cam.Manufacturer;
        _modelBlock.Text = cam.Model;
        _fwBlock.Text    = cam.FirmwareVersion;
    }
}
