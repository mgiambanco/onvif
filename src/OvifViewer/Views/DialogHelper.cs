using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OvifViewer.Views;

/// <summary>Cross-platform replacements for WinForms MessageBox.Show.</summary>
public static class DialogHelper
{
    public static async Task ShowMessageAsync(Window owner, string message, string title = "OvifViewer")
    {
        var dlg = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var msg = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 16, 16, 8),
        };
        var ok = new Button
        {
            Content = "OK",
            Width = 80,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16),
        };
        ok.Click += (_, _) => dlg.Close();

        var panel = new StackPanel();
        panel.Children.Add(msg);
        panel.Children.Add(ok);
        dlg.Content = panel;

        await dlg.ShowDialog(owner);
    }

    public static async Task<bool> ShowConfirmAsync(Window owner, string message, string title = "Confirm")
    {
        bool result = false;
        var dlg = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var msg = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 16, 16, 8),
        };

        var yes = new Button { Content = "Yes", Width = 80 };
        var no = new Button { Content = "No", Width = 80 };
        yes.Click += (_, _) => { result = true; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 12,
            Margin = new Thickness(0, 0, 0, 16),
        };
        btns.Children.Add(yes);
        btns.Children.Add(no);

        var panel = new StackPanel();
        panel.Children.Add(msg);
        panel.Children.Add(btns);
        dlg.Content = panel;

        await dlg.ShowDialog(owner);
        return result;
    }
}
