using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OvifViewer.Models;
using OvifViewer.Services;

namespace OvifViewer.Views;

/// <summary>Lists all configured cameras and allows editing or removing them.</summary>
public class ManageCamerasWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly IOnvifService _onvif;
    private readonly ListBox _list;

    public ManageCamerasWindow(ISettingsService settings, IOnvifService onvif)
    {
        _settings = settings;
        _onvif = onvif;

        Title = "Manage Cameras";
        Width = 620;
        Height = 420;
        MinWidth = 500;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list = new ListBox { SelectionMode = SelectionMode.Single };

        var editBtn = new Button { Content = "Edit…", Width = 80 };
        var removeBtn = new Button { Content = "Remove", Width = 80 };
        var closeBtn = new Button { Content = "Close", Width = 80 };

        editBtn.Click += OnEditClick;
        removeBtn.Click += OnRemoveClick;
        closeBtn.Click += (_, _) => Close();

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
        };
        btnRow.Children.Add(editBtn);
        btnRow.Children.Add(removeBtn);
        btnRow.Children.Add(closeBtn);

        var root = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(btnRow, Dock.Bottom);
        root.Children.Add(btnRow);
        root.Children.Add(new ScrollViewer { Content = _list });

        Content = root;
        RefreshList();
    }

    private void RefreshList()
    {
        _list.Items.Clear();
        foreach (var cam in _settings.Settings.Cameras)
        {
            _list.Items.Add(new CameraListViewItem(cam));
        }
    }

    private async void OnEditClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_list.SelectedItem is not CameraListViewItem item) return;
        var cam = item.Camera;

        var win = new CameraSettingsWindow(cam, _onvif);
        await win.ShowDialog(this);
        if (win.Result == null) return;

        var r = win.Result;
        cam.DisplayName = r.DisplayName;
        cam.Username = r.Username;
        cam.EncryptedPassword = r.EncryptedPassword;
        cam.SelectedProfileToken = r.SelectedProfileToken;
        cam.PtzEnabled = r.PtzEnabled;
        cam.AutoConnect = r.AutoConnect;
        cam.RtspUri = string.Empty;

        _settings.Save();
        RefreshList();
    }

    private async void OnRemoveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_list.SelectedItem is not CameraListViewItem item) return;
        var cam = item.Camera;

        if (!await DialogHelper.ShowConfirmAsync(this, $"Remove \"{cam.DisplayName}\"?", "Confirm Remove"))
            return;

        _settings.Settings.Cameras.Remove(cam);
        _settings.Settings.PanelLayouts.RemoveAll(l => l.CameraId == cam.Id);
        _settings.Save();
        RefreshList();
    }
}

file class CameraListViewItem : ListBoxItem
{
    public CameraConfig Camera { get; }

    public CameraListViewItem(CameraConfig camera)
    {
        Camera = camera;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(200, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var name = new TextBlock { Text = camera.DisplayName, VerticalAlignment = VerticalAlignment.Center };
        var url = new TextBlock { Text = camera.DeviceServiceUrl, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var auto = new TextBlock { Text = camera.AutoConnect ? "Yes" : "No", VerticalAlignment = VerticalAlignment.Center };

        Grid.SetColumn(name, 0); grid.Children.Add(name);
        Grid.SetColumn(url, 1); grid.Children.Add(url);
        Grid.SetColumn(auto, 2); grid.Children.Add(auto);

        Content = grid;
        Padding = new Thickness(4, 2);
    }
}
