using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OvifViewer.Models;
using OvifViewer.Services;

namespace OvifViewer.Views;

/// <summary>Configure credentials, stream profile, and PTZ for a single camera.</summary>
public class CameraSettingsWindow : Window
{
    private readonly IOnvifService _onvif;
    private readonly CameraConfig _camera;

    private readonly TextBox _nameBox;
    private readonly TextBox _userBox;
    private readonly TextBox _passBox;
    private readonly ComboBox _profileBox;
    private readonly CheckBox _ptzCheck;
    private readonly CheckBox _autoConnectCheck;
    private readonly Button _testBtn;
    private readonly TextBlock _testLabel;

    public CameraConfig? Result { get; private set; }

    public CameraSettingsWindow(CameraConfig camera, IOnvifService onvif)
    {
        _camera = camera;
        _onvif = onvif;

        Title = $"Camera Settings — {camera.DisplayName}";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _nameBox = new TextBox { Text = camera.DisplayName };
        _userBox = new TextBox { Text = camera.Username };
        _passBox = new TextBox
        {
            PasswordChar = '•',
            Text = string.IsNullOrEmpty(camera.EncryptedPassword) ? "" : camera.GetPassword(),
        };
        _profileBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _ptzCheck = new CheckBox { Content = "Enable PTZ controls", IsChecked = camera.PtzEnabled };
        _autoConnectCheck = new CheckBox { Content = "Auto-connect on startup", IsChecked = camera.AutoConnect };

        _testBtn = new Button { Content = "Test & Load Profiles" };
        _testLabel = new TextBlock
        {
            Text = "Enter credentials then click Test.",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        _testBtn.Click += OnTestClick;

        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(130, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        int row = 0;
        void Row(string label, Control ctrl)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0); grid.Children.Add(lbl);
            Grid.SetRow(ctrl, row); Grid.SetColumn(ctrl, 1); grid.Children.Add(ctrl);
            row++;
        }

        Row("Display Name:", _nameBox);
        Row("URL:", new TextBlock { Text = camera.DeviceServiceUrl, VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        Row("Username:", _userBox);
        Row("Password:", _passBox);
        Row("Stream Profile:", _profileBox);

        // PTZ checkbox (column 1 only)
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(_ptzCheck, row); Grid.SetColumn(_ptzCheck, 1); grid.Children.Add(_ptzCheck); row++;

        // Auto-connect checkbox
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(_autoConnectCheck, row); Grid.SetColumn(_autoConnectCheck, 1); grid.Children.Add(_autoConnectCheck); row++;

        // Test row
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var testRow = new Grid();
        testRow.ColumnDefinitions.Add(new ColumnDefinition(160, GridUnitType.Pixel));
        testRow.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        Grid.SetColumn(_testBtn, 0); testRow.Children.Add(_testBtn);
        Grid.SetColumn(_testLabel, 1); testRow.Children.Add(_testLabel);
        Grid.SetRow(testRow, row); Grid.SetColumnSpan(testRow, 2); grid.Children.Add(testRow); row++;

        // Buttons row
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var saveBtn = new Button { Content = "Save", Width = 80 };
        var cancelBtn = new Button { Content = "Cancel", Width = 80 };
        saveBtn.Click += OnSaveClick;
        cancelBtn.Click += (_, _) => Close(null);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(saveBtn);
        Grid.SetRow(btnRow, row); Grid.SetColumnSpan(btnRow, 2); grid.Children.Add(btnRow);

        Content = grid;
    }

    private async void OnTestClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _testBtn.IsEnabled = false;
        _testLabel.Text = "Connecting…";
        _testLabel.Foreground = Avalonia.Media.Brushes.Gray;
        _profileBox.Items.Clear();

        var temp = BuildTempConfig();
        try
        {
            var profiles = await _onvif.GetProfilesAsync(temp);
            foreach (var p in profiles)
                _profileBox.Items.Add(p);

            if (!string.IsNullOrEmpty(_camera.SelectedProfileToken))
            {
                for (int i = 0; i < _profileBox.Items.Count; i++)
                {
                    if (_profileBox.Items[i] is CameraProfile cp && cp.Token == _camera.SelectedProfileToken)
                    {
                        _profileBox.SelectedIndex = i;
                        break;
                    }
                }
            }
            if (_profileBox.SelectedIndex < 0 && _profileBox.Items.Count > 0)
                _profileBox.SelectedIndex = 0;

            bool hasPtz = await _onvif.HasPtzAsync(temp);
            _ptzCheck.IsEnabled = hasPtz;
            if (!hasPtz) _ptzCheck.IsChecked = false;

            _testLabel.Foreground = new SolidColorBrush(Colors.Green);
            _testLabel.Text = $"OK — {profiles.Count} profile(s) found.";
        }
        catch (Exception ex)
        {
            _testLabel.Foreground = new SolidColorBrush(Color.FromRgb(220, 80, 80));
            var inner = ex.InnerException?.Message ?? string.Empty;
            _testLabel.Text = string.IsNullOrEmpty(inner) ? $"Failed: {ex.Message}" : $"Failed: {ex.Message} → {inner}";
        }
        finally
        {
            _testBtn.IsEnabled = true;
        }
    }

    private async void OnSaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var profile = _profileBox.SelectedItem as CameraProfile;
        if (profile == null && _profileBox.Items.Count > 0)
        {
            await DialogHelper.ShowMessageAsync(this, "Please test the connection and select a stream profile.", "Validation");
            return;
        }

        var cam = BuildTempConfig();
        cam.SelectedProfileToken = profile?.Token ?? _camera.SelectedProfileToken;
        cam.PtzEnabled = _ptzCheck.IsChecked == true;
        cam.AutoConnect = _autoConnectCheck.IsChecked == true;
        cam.Id = _camera.Id;
        cam.Muted = _camera.Muted;

        Result = cam;
        Close(Result);
    }

    private CameraConfig BuildTempConfig()
    {
        var c = new CameraConfig
        {
            DeviceServiceUrl = _camera.DeviceServiceUrl,
            DisplayName = _nameBox.Text?.Trim() ?? "",
            Username = _userBox.Text?.Trim() ?? "",
        };
        c.SetPassword(_passBox.Text ?? "");
        return c;
    }
}
