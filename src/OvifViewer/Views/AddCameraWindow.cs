using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OvifViewer.Models;

namespace OvifViewer.Views;

/// <summary>Simple dialog to manually enter a camera URL.</summary>
public class AddCameraWindow : Window
{
    private readonly TextBox _nameBox;
    private readonly TextBox _urlBox;

    public CameraConfig? Result { get; private set; }

    public AddCameraWindow()
    {
        Title = "Add Camera Manually";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _nameBox = new TextBox();
        _urlBox = new TextBox { Watermark = "http://192.168.1.100/onvif/device_service" };

        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(120, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        AddRow(grid, 0, "Display Name:", _nameBox);
        AddRow(grid, 1, "Device URL:", _urlBox);

        var okBtn = new Button { Content = "Next →", Width = 80, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button { Content = "Cancel", Width = 80 };
        okBtn.Click += OnOkClick;
        cancelBtn.Click += (_, _) => Close(null);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);
        Grid.SetRow(btnRow, 2);
        Grid.SetColumnSpan(btnRow, 2);
        grid.Children.Add(btnRow);

        Content = grid;
    }

    private static void AddRow(Grid grid, int row, string label, Control ctrl)
    {
        var lbl = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(lbl, row);
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        Grid.SetRow(ctrl, row);
        Grid.SetColumn(ctrl, 1);
        grid.Children.Add(ctrl);
    }

    private void OnOkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var url = _urlBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(url))
        {
            // simple inline hint — no MessageBox needed for this short dialog
            _urlBox.Watermark = "⚠ URL required";
            return;
        }
        var name = _nameBox.Text?.Trim() ?? "";
        Result = new CameraConfig
        {
            DeviceServiceUrl = url,
            DisplayName = string.IsNullOrEmpty(name) ? url : name,
        };
        Close(Result);
    }
}
