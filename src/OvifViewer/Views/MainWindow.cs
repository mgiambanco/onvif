using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OvifViewer.Controls;
using OvifViewer.Models;
using OvifViewer.Services;
using Serilog;

namespace OvifViewer.Views;

public class MainWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly IRtspStreamService _rtspService;
    private readonly IOnvifService _onvifService;
    private readonly IDiscoveryService _discoveryService;

    // Layout controls
    private Canvas _canvas = null!;
    private Panel _sidebar = null!;
    private Grid _mainGrid = null!;
    private ColumnDefinition _sidebarCol = null!;
    private bool _sidebarCollapsed;
    private Panel _expandStrip = null!;

    // Camera list
    private readonly StackPanel _cameraListPanel = new() { Orientation = Orientation.Vertical };
    private readonly Dictionary<Guid, CameraListItem> _listItems = [];

    // Camera panels on Canvas
    private readonly Dictionary<Guid, CameraPanel> _panels = [];

    // Grid preset
    private int _presetCols;
    private Button[] _presetButtons = [];

    // Full-screen
    private CameraPanel? _fullScreenPanel;
    private bool _hasManualLayout;

    // Status bar
    private TextBlock _statusLabel = null!;

    // Zoom panel
    private CameraZoomWindow? _zoomWindow;

    // Debounce layout save
    private readonly DispatcherTimer _layoutSaveTimer;

    public MainWindow(
        ISettingsService settings,
        IRtspStreamService rtspService,
        IOnvifService onvifService,
        IDiscoveryService discoveryService)
    {
        _settings = settings;
        _rtspService = rtspService;
        _onvifService = onvifService;
        _discoveryService = discoveryService;

        Title = "OvifViewer";
        MinWidth = 800;
        MinHeight = 600;
        Background = new SolidColorBrush(Color.FromRgb(15, 15, 15));

        var s = _settings.Settings;
        Width = s.MainWindowWidth > 0 ? s.MainWindowWidth : 1280;
        Height = s.MainWindowHeight > 0 ? s.MainWindowHeight : 800;
        if (s.MainWindowX >= 0 && s.MainWindowY >= 0)
            Position = new PixelPoint(s.MainWindowX, s.MainWindowY);
        if (s.MainWindowMaximized) WindowState = WindowState.Maximized;

        _layoutSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _layoutSaveTimer.Tick += (_, _) => { _layoutSaveTimer.Stop(); _settings.Save(); };

        Content = BuildContent();
        KeyDown += OnKeyDown;

        Opened += async (_, _) =>
        {
            _zoomWindow = new CameraZoomWindow(_rtspService, _onvifService);
            await RestoreCameraPanelsAsync();
        };
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    private Control BuildContent()
    {
        var menu = BuildMenu();
        var toolbar = BuildGridToolbar();
        _statusLabel = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0),
            FontSize = 20,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
            Text = "No cameras",
        };
        var statusBar = new Border
        {
            Height = 48,
            Background = new SolidColorBrush(Color.FromRgb(26, 26, 26)),
            Child = _statusLabel,
        };

        _canvas = new Canvas { Background = new SolidColorBrush(Color.FromRgb(15, 15, 15)) };
        _canvas.SizeChanged += OnCanvasSizeChanged;

        _mainGrid = new Grid();
        _sidebarCol = new ColumnDefinition(182, GridUnitType.Pixel);
        _mainGrid.ColumnDefinitions.Add(_sidebarCol);
        _mainGrid.ColumnDefinitions.Add(new ColumnDefinition(3, GridUnitType.Pixel));
        _mainGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        _sidebar = BuildSidebar();
        Grid.SetColumn(_sidebar, 0);

        var splitter = new GridSplitter
        {
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
            ResizeDirection = GridResizeDirection.Columns,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetColumn(splitter, 1);

        var canvasContainer = new Border { Child = _canvas };
        Grid.SetColumn(canvasContainer, 2);

        _expandStrip = new Panel
        {
            Width = 40,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            IsVisible = false,
        };
        var expandBtn = new Button { Content = "▶", Padding = new Thickness(0), Background = Brushes.Transparent };
        expandBtn.Click += (_, _) => ToggleSidebar();
        _expandStrip.Children.Add(expandBtn);
        // expandStrip replaces column 0 when collapsed
        Grid.SetColumn(_expandStrip, 0);

        _mainGrid.Children.Add(_sidebar);
        _mainGrid.Children.Add(splitter);
        _mainGrid.Children.Add(canvasContainer);
        _mainGrid.Children.Add(_expandStrip);

        var root = new DockPanel();
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(menu);
        root.Children.Add(toolbar);
        root.Children.Add(statusBar);
        root.Children.Add(_mainGrid);

        return root;
    }

    private Menu BuildMenu()
    {
        var cameraMenu = new MenuItem { Header = "Cameras" };
        cameraMenu.Items.Add(new MenuItem { Header = "Discover Cameras…",     Command = new RelayCommand(async () => await OnDiscoverClick()) });
        cameraMenu.Items.Add(new MenuItem { Header = "Add Camera Manually…",  Command = new RelayCommand(async () => await OnAddManualClick()) });
        cameraMenu.Items.Add(new Separator());
        cameraMenu.Items.Add(new MenuItem { Header = "Manage Cameras…",       Command = new RelayCommand(async () => await OnManageCamerasClick()) });
        cameraMenu.Items.Add(new Separator());
        cameraMenu.Items.Add(new MenuItem { Header = "Export Camera Config…", Command = new RelayCommand(async () => await OnExportCamerasClick()) });
        cameraMenu.Items.Add(new MenuItem { Header = "Import Camera Config…", Command = new RelayCommand(async () => await OnImportCamerasClick()) });

        var viewMenu = new MenuItem { Header = "View" };
        viewMenu.Items.Add(new MenuItem { Header = "Reconnect All",  Command = new RelayCommand(OnReconnectAll) });
        viewMenu.Items.Add(new MenuItem { Header = "Reset Layout",   Command = new RelayCommand(() => ApplyGridPreset(0)) });

        var menu = new Menu { Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)) };
        menu.Items.Add(cameraMenu);
        menu.Items.Add(viewMenu);
        return menu;
    }

    private Panel BuildGridToolbar()
    {
        (string Text, int Cols)[] presets = [("Auto", 0), ("1×1", 1), ("2×2", 2), ("3×3", 3), ("4×4", 4)];
        var toolbar = new Panel
        {
            Height = 60,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
        };
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Spacing = 4,
        };

        _presetCols = _settings.Settings.GridPresetCols;
        var btns = new Button[presets.Length];
        for (int i = 0; i < presets.Length; i++)
        {
            var (text, cols) = presets[i];
            var btn = new Button
            {
                Content = text,
                Width = 88, Height = 48,
                FontSize = 16,
                Padding = new Thickness(0),
                Background = cols == _presetCols
                    ? new SolidColorBrush(Color.FromRgb(60, 80, 120))
                    : new SolidColorBrush(Color.FromRgb(45, 45, 45)),
            };
            int c = cols;
            btn.Click += (_, _) => ApplyGridPreset(c);
            stack.Children.Add(btn);
            btns[i] = btn;
        }
        _presetButtons = btns;
        toolbar.Children.Add(stack);
        return toolbar;
    }

    private Panel BuildSidebar()
    {
        var collapseBtn = new Button
        {
            Content = "◀",
            HorizontalAlignment = HorizontalAlignment.Right,
            Width = 48, Height = 48,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 14,
        };
        collapseBtn.Click += (_, _) => ToggleSidebar();

        var headerLabel = new TextBlock
        {
            Text = "CAMERAS",
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(130, 130, 130)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var sidebarHeader = new Grid
        {
            Height = 48,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
        };
        sidebarHeader.Children.Add(headerLabel);
        sidebarHeader.Children.Add(collapseBtn);

        var scrollViewer = new ScrollViewer { Content = _cameraListPanel };

        var sidebar = new DockPanel
        {
            Background = new SolidColorBrush(Color.FromRgb(22, 22, 22)),
        };
        DockPanel.SetDock(sidebarHeader, Dock.Top);
        sidebar.Children.Add(sidebarHeader);
        sidebar.Children.Add(scrollViewer);

        return sidebar;
    }

    // ── Camera panels ─────────────────────────────────────────────────────────

    private async Task RestoreCameraPanelsAsync()
    {
        var cameras = _settings.Settings.Cameras.Where(c => c.AutoConnect).ToList();

        await Task.WhenAll(cameras
            .Where(c => string.IsNullOrEmpty(c.RtspUri))
            .Select(async c =>
            {
                try { c.RtspUri = await _onvifService.GetStreamUriAsync(c); }
                catch (Exception ex) { Log.Warning(ex, "Could not fetch RTSP URI for {Name}", c.DisplayName); }
            }));

        _settings.Save();

        foreach (var cam in cameras)
            await AddCameraPanelAsync(cam);
    }

    public async Task AddCameraPanelAsync(CameraConfig camera)
    {
        if (_panels.ContainsKey(camera.Id)) return;

        if (string.IsNullOrEmpty(camera.RtspUri))
        {
            try
            {
                camera.RtspUri = await _onvifService.GetStreamUriAsync(camera);
                _settings.Save();
            }
            catch (Exception ex) { Log.Warning(ex, "Could not fetch RTSP URI for {Name}", camera.DisplayName); }
        }

        var panel = new CameraPanel(camera, _rtspService, _onvifService);
        panel.PanelRemoveRequested    += (_, _) => RemoveCameraPanel(camera.Id);
        panel.ToggleFullSizeRequested += (_, _) => ToggleFullScreen(panel);
        panel.ZoomRequested           += (_, _) => ShowInZoomPanel(camera);
        panel.StatusChanged           += (text, state) => { UpdateCameraListItem(camera.Id, text, state); UpdateStatusBar(); };
        panel.LayoutChanged           += (_, _) => SavePanelLayout(panel);
        panel.MuteChanged             += (_, _) => _settings.Save();
        panel.ProfileChangeRequested  += async token =>
        {
            panel.StopStream();
            camera.SelectedProfileToken = token;
            camera.RtspUri = string.Empty;
            try { camera.RtspUri = await _onvifService.GetStreamUriAsync(camera); }
            catch (Exception ex) { Log.Warning(ex, "Profile switch failed for {Name}", camera.DisplayName); }
            _settings.Save();
            if (_panels.ContainsKey(camera.Id)) panel.StartStream();
        };

        _canvas.Children.Add(panel);
        _panels[camera.Id] = panel;

        AddCameraToList(camera);

        var saved = _settings.Settings.PanelLayouts.FirstOrDefault(l => l.CameraId == camera.Id);
        if (saved != null)
        {
            Canvas.SetLeft(panel, saved.X);
            Canvas.SetTop(panel, saved.Y);
            panel.Width = saved.Width;
            panel.Height = saved.Height;
            _hasManualLayout = true;
        }

        panel.StartStream();

        if (!_hasManualLayout)
            TileAll();
        else if (saved == null)
        {
            Canvas.SetLeft(panel, 20);
            Canvas.SetTop(panel, 20);
            panel.Width = 320;
            panel.Height = 240;
        }

        UpdateStatusBar();
    }

    public void RemoveCameraPanel(Guid cameraId)
    {
        if (!_panels.TryGetValue(cameraId, out var panel)) return;
        if (_fullScreenPanel == panel) ExitFullScreen();

        panel.StopStream();
        _canvas.Children.Remove(panel);
        _panels.Remove(cameraId);

        RemoveCameraFromList(cameraId);

        _settings.Settings.PanelLayouts.RemoveAll(l => l.CameraId == cameraId);
        _settings.Save();
        TileAll();
        UpdateStatusBar();
    }

    private void SavePanelLayout(CameraPanel panel)
    {
        _hasManualLayout = true;
        var layouts = _settings.Settings.PanelLayouts;
        var existing = layouts.FirstOrDefault(l => l.CameraId == panel.Camera.Id);
        if (existing == null)
        {
            existing = new CameraPanelLayout { CameraId = panel.Camera.Id };
            layouts.Add(existing);
        }
        existing.X = (int)Canvas.GetLeft(panel);
        existing.Y = (int)Canvas.GetTop(panel);
        existing.Width = (int)panel.Width;
        existing.Height = (int)panel.Height;
        existing.ZOrder = _canvas.Children.IndexOf(panel);

        _layoutSaveTimer.Stop();
        _layoutSaveTimer.Start();
    }

    // ── Grid / full-screen ────────────────────────────────────────────────────

    private void TileAll()
    {
        var panels = _panels.Values.ToList();
        if (panels.Count == 0) return;
        double cw = _canvas.Bounds.Width, ch = _canvas.Bounds.Height;
        if (cw <= 0 || ch <= 0) return;

        int cols = _presetCols > 0 ? _presetCols : (int)Math.Ceiling(Math.Sqrt(panels.Count));
        int rows = (int)Math.Ceiling(panels.Count / (double)cols);
        double w = cw / cols, h = ch / rows;

        for (int i = 0; i < panels.Count; i++)
        {
            panels[i].IsVisible = true;
            Canvas.SetLeft(panels[i], (i % cols) * w);
            Canvas.SetTop(panels[i], (i / cols) * h);
            panels[i].Width = w;
            panels[i].Height = h;
        }
    }

    private void ToggleFullScreen(CameraPanel panel)
    {
        if (_fullScreenPanel == panel) ExitFullScreen();
        else EnterFullScreen(panel);
    }

    private void EnterFullScreen(CameraPanel panel)
    {
        _fullScreenPanel = panel;
        foreach (var p in _panels.Values)
            p.IsVisible = (p == panel);
        Canvas.SetLeft(panel, 0);
        Canvas.SetTop(panel, 0);
        panel.Width = _canvas.Bounds.Width;
        panel.Height = _canvas.Bounds.Height;
        panel.ZIndex = 100;
    }

    private void ExitFullScreen()
    {
        if (_fullScreenPanel != null)
            _fullScreenPanel.ZIndex = 0;
        _fullScreenPanel = null;

        if (_hasManualLayout)
        {
            foreach (var p in _panels.Values)
            {
                p.IsVisible = true;
                var saved = _settings.Settings.PanelLayouts.FirstOrDefault(l => l.CameraId == p.Camera.Id);
                if (saved != null)
                {
                    Canvas.SetLeft(p, saved.X);
                    Canvas.SetTop(p, saved.Y);
                    p.Width = saved.Width;
                    p.Height = saved.Height;
                }
            }
        }
        else
        {
            TileAll();
        }
    }

    private void ApplyGridPreset(int cols)
    {
        _presetCols = cols;
        _hasManualLayout = false;
        _settings.Settings.PanelLayouts.Clear();
        _settings.Settings.GridPresetCols = cols;

        foreach (var b in _presetButtons)
        {
            bool active = b.Tag is int bc && bc == cols;
            // We stored cols in Tag at build time — instead compare Content string
        }
        // Highlight active button
        (string Text, int Cols)[] presets = [("Auto", 0), ("1×1", 1), ("2×2", 2), ("3×3", 3), ("4×4", 4)];
        for (int i = 0; i < _presetButtons.Length && i < presets.Length; i++)
        {
            _presetButtons[i].Background = presets[i].Cols == cols
                ? new SolidColorBrush(Color.FromRgb(60, 80, 120))
                : new SolidColorBrush(Color.FromRgb(45, 45, 45));
        }

        _layoutSaveTimer.Stop();
        _layoutSaveTimer.Start();
        TileAll();
    }

    // ── Canvas resize ─────────────────────────────────────────────────────────

    private void OnCanvasSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_fullScreenPanel != null)
        {
            Canvas.SetLeft(_fullScreenPanel, 0);
            Canvas.SetTop(_fullScreenPanel, 0);
            _fullScreenPanel.Width = e.NewSize.Width;
            _fullScreenPanel.Height = e.NewSize.Height;
        }
        else if (!_hasManualLayout)
            TileAll();
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _fullScreenPanel != null)
        {
            ExitFullScreen();
            e.Handled = true;
        }
    }

    // ── Sidebar ───────────────────────────────────────────────────────────────

    private void ToggleSidebar()
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        _sidebar.IsVisible = !_sidebarCollapsed;
        _expandStrip.IsVisible = _sidebarCollapsed;
        _sidebarCol.Width = _sidebarCollapsed
            ? new GridLength(20, GridUnitType.Pixel)
            : new GridLength(182, GridUnitType.Pixel);
    }

    // ── Camera list (sidebar) ─────────────────────────────────────────────────

    private void AddCameraToList(CameraConfig camera)
    {
        string ip;
        try { ip = new Uri(camera.DeviceServiceUrl).Host; }
        catch { ip = camera.DeviceServiceUrl; }

        var name = string.IsNullOrWhiteSpace(camera.DisplayName) ? ip : camera.DisplayName;
        var item = new CameraListItem(camera.Id, name, ip, this);
        _cameraListPanel.Children.Add(item);
        _listItems[camera.Id] = item;
    }

    private void RemoveCameraFromList(Guid cameraId)
    {
        if (!_listItems.TryGetValue(cameraId, out var item)) return;
        _cameraListPanel.Children.Remove(item);
        _listItems.Remove(cameraId);
    }

    private void UpdateCameraListItem(Guid cameraId, string text, StatusState state)
    {
        if (!_listItems.TryGetValue(cameraId, out var item)) return;
        if (_panels.TryGetValue(cameraId, out var panel) && !string.IsNullOrWhiteSpace(panel.Camera.DisplayName))
            item.SetName(panel.Camera.DisplayName);

        var color = state switch
        {
            StatusState.Live       => Color.FromRgb(50, 205, 50),
            StatusState.Connecting => Colors.Orange,
            StatusState.Error      => Color.FromRgb(220, 80, 80),
            _                      => Color.FromRgb(120, 120, 120),
        };
        item.SetStatusColor(color);
    }

    // Called from CameraListItem when clicked
    internal void OnCameraListItemClick(Guid cameraId)
    {
        if (_panels.TryGetValue(cameraId, out var panel))
            ToggleFullScreen(panel);
    }

    internal void OnCameraListItemRightClick(Guid cameraId, Control anchor)
    {
        if (!_panels.TryGetValue(cameraId, out var panel)) return;
        var camera = panel.Camera;

        var menu = new ContextMenu();
        menu.ItemsSource = new object[]
        {
            new MenuItem { Header = "Edit…",     Command = new RelayCommand(async () => await EditCamera(camera)) },
            new MenuItem { Header = "Reconnect", Command = new RelayCommand(() => { panel.StopStream(); panel.StartStream(); }) },
            new Separator(),
            new MenuItem { Header = "Remove",    Command = new RelayCommand(() => RemoveCameraPanel(cameraId)) },
        };
        anchor.ContextMenu = menu;
        menu.Open(anchor);
    }

    // ── Zoom panel ────────────────────────────────────────────────────────────

    private void ShowInZoomPanel(CameraConfig camera)
    {
        if (_zoomWindow == null) return;
        if (!_zoomWindow.IsVisible)
        {
            // Position next to the main window
            var screen = Screens.ScreenFromVisual(this);
            var wa = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            int pw = (int)_zoomWindow.Width, ph = (int)_zoomWindow.Height;
            int x = Position.X + (int)Width + 8;
            if (x + pw > wa.Right) x = Math.Max(wa.X, Position.X - pw - 8);
            int y = Position.Y + ((int)Height - ph) / 2;
            y = Math.Max(wa.Y, Math.Min(wa.Bottom - ph, y));
            _zoomWindow.Position = new PixelPoint(x, y);
        }
        _zoomWindow.Show(this);
        _ = _zoomWindow.LoadCameraAsync(camera);
    }

    // ── Menu actions ──────────────────────────────────────────────────────────

    private async Task OnDiscoverClick()
    {
        var win = new DiscoveryWindow(_discoveryService, _onvifService, _settings);
        await win.ShowDialog(this);
        foreach (var cam in win.SelectedCameras)
            await AddCameraPanelAsync(cam);
    }

    private async Task OnAddManualClick()
    {
        var win = new AddCameraWindow();
        await win.ShowDialog(this);
        if (win.Result == null) return;
        _settings.Settings.Cameras.Add(win.Result);
        _settings.Save();
        await AddCameraPanelAsync(win.Result);
    }

    private async Task OnManageCamerasClick()
    {
        var win = new ManageCamerasWindow(_settings, _onvifService);
        await win.ShowDialog(this);
    }

    private async Task OnExportCamerasClick()
    {
        var path = await PickSaveFileAsync("Export Camera Config", "cameras.json");
        if (path == null) return;
        try
        {
            _settings.ExportCameras(path);
            await DialogHelper.ShowMessageAsync(this,
                $"Exported {_settings.Settings.Cameras.Count} camera(s) to:\n{path}\n\nNote: passwords are stored in plaintext.",
                "Export Complete");
        }
        catch (Exception ex)
        {
            await DialogHelper.ShowMessageAsync(this, $"Export failed: {ex.Message}", "Error");
        }
    }

    private async Task OnImportCamerasClick()
    {
        var path = await PickOpenFileAsync("Import Camera Config");
        if (path == null) return;
        try
        {
            var added = _settings.ImportCameras(path);
            foreach (var cam in added)
                await AddCameraPanelAsync(cam);
            await DialogHelper.ShowMessageAsync(this,
                added.Count > 0
                    ? $"Imported {added.Count} new camera(s)."
                    : "No new cameras found (all IDs already exist).",
                "Import Complete");
        }
        catch (Exception ex)
        {
            await DialogHelper.ShowMessageAsync(this, $"Import failed: {ex.Message}", "Error");
        }
    }

    private void OnReconnectAll()
    {
        foreach (var panel in _panels.Values)
        {
            panel.StopStream();
            panel.StartStream();
        }
    }

    private async Task EditCamera(CameraConfig camera)
    {
        var win = new CameraSettingsWindow(camera, _onvifService);
        await win.ShowDialog(this);
        if (win.Result == null) return;

        var r = win.Result;
        camera.DisplayName = r.DisplayName;
        camera.Username = r.Username;
        camera.EncryptedPassword = r.EncryptedPassword;
        camera.SelectedProfileToken = r.SelectedProfileToken;
        camera.PtzEnabled = r.PtzEnabled;
        camera.AutoConnect = r.AutoConnect;
        camera.RtspUri = string.Empty;
        _settings.Save();

        if (_listItems.TryGetValue(camera.Id, out var item))
            item.SetName(string.IsNullOrWhiteSpace(camera.DisplayName) ? camera.DeviceServiceUrl : camera.DisplayName);

        if (_panels.TryGetValue(camera.Id, out var panel))
        {
            panel.StopStream();
            _ = ReconnectPanelAsync(panel, camera);
        }
    }

    private async Task ReconnectPanelAsync(CameraPanel panel, CameraConfig camera)
    {
        try
        {
            camera.RtspUri = await _onvifService.GetStreamUriAsync(camera);
            _settings.Save();
        }
        catch (Exception ex) { Log.Warning(ex, "Could not fetch RTSP URI for {Name} after edit", camera.DisplayName); }
        if (_panels.ContainsKey(camera.Id)) panel.StartStream();
    }

    // ── File dialogs ──────────────────────────────────────────────────────────

    private async Task<string?> PickSaveFileAsync(string title, string suggestedName)
    {
        var result = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = [new FilePickerFileType("JSON files") { Patterns = ["*.json"] }],
            DefaultExtension = "json",
        });
        return result?.TryGetLocalPath();
    }

    private async Task<string?> PickOpenFileAsync(string title)
    {
        var results = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON files") { Patterns = ["*.json"] }],
        });
        return results.Count > 0 ? results[0].TryGetLocalPath() : null;
    }

    // ── Status bar ────────────────────────────────────────────────────────────

    private void UpdateStatusBar()
    {
        int total = _panels.Count;
        int live = _panels.Values.Count(p => p.IsStreaming);
        _statusLabel.Text = total == 0
            ? "No cameras — use Cameras menu to add"
            : $"{live}/{total} camera{(total == 1 ? "" : "s")} live";
    }

    // ── Window state ──────────────────────────────────────────────────────────

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _layoutSaveTimer.Stop();
        _zoomWindow?.Clear();
        _zoomWindow?.Close();

        var s = _settings.Settings;
        if (WindowState == WindowState.Normal)
        {
            s.MainWindowX = Position.X;
            s.MainWindowY = Position.Y;
            s.MainWindowWidth = (int)Width;
            s.MainWindowHeight = (int)Height;
        }
        s.MainWindowMaximized = WindowState == WindowState.Maximized;
        _settings.Save();

        foreach (var panel in _panels.Values)
            panel.StopStream();

        base.OnClosing(e);
    }
}

// ── Sidebar camera list item ──────────────────────────────────────────────────

class CameraListItem : Border
{
    private readonly Guid _cameraId;
    private readonly MainWindow _owner;
    private readonly TextBlock _nameLabel;
    private readonly TextBlock _ipLabel;
    private readonly Border _statusDot;

    public CameraListItem(Guid cameraId, string name, string ip, MainWindow owner)
    {
        _cameraId = cameraId;
        _owner = owner;

        Background = new SolidColorBrush(Color.FromRgb(22, 22, 22));
        Padding = new Thickness(4, 3);
        Cursor = new Cursor(StandardCursorType.Hand);

        _nameLabel = new TextBlock
        {
            Text = name,
            FontSize = 17,
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _ipLabel = new TextBlock
        {
            Text = ip,
            FontSize = 16,
            Foreground = new SolidColorBrush(Colors.Orange),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _statusDot = new Border
        {
            Width = 16, Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Colors.Orange),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };

        var textStack = new StackPanel();
        textStack.Children.Add(_nameLabel);
        textStack.Children.Add(_ipLabel);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(_statusDot);
        row.Children.Add(textStack);

        Child = row;

        PointerPressed += OnPointerPressed;
        PointerEntered += (_, _) => Background = new SolidColorBrush(Color.FromRgb(35, 60, 100));
        PointerExited  += (_, _) => Background = new SolidColorBrush(Color.FromRgb(22, 22, 22));
    }

    public void SetName(string name) => _nameLabel.Text = name;

    public void SetStatusColor(Color c)
    {
        _statusDot.Background = new SolidColorBrush(c);
        _ipLabel.Foreground = new SolidColorBrush(c);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            _owner.OnCameraListItemClick(_cameraId);
        else if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            _owner.OnCameraListItemRightClick(_cameraId, this);
    }
}

// ── Minimal ICommand ──────────────────────────────────────────────────────────

file sealed class RelayCommand(Action execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => execute();
}

file sealed class RelayCommand<T>(Func<T> execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => execute();
}
