using OvifViewer.Controls;
using OvifViewer.Models;
using OvifViewer.Services;
using Serilog;

namespace OvifViewer.Forms;

public partial class MainForm : Form
{
    private readonly ISettingsService _settings;
    private readonly IRtspStreamService _rtspService;
    private readonly IOnvifService _onvifService;
    private readonly IDiscoveryService _discoveryService;

    private readonly Panel _canvas;
    private readonly Panel _sidebar;
    private readonly ListView _cameraList;
    private readonly StatusStrip _statusBar;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly Dictionary<Guid, CameraPanel> _panels = [];
    private readonly Dictionary<Guid, ListViewItem> _listItems = [];
    private MouseHook? _mouseHook;
    private CameraPanel? _fullScreenPanel;
    private bool _hasManualLayout;
    private readonly System.Windows.Forms.Timer _layoutSaveTimer;
    private Splitter _splitter = null!;
    private Panel _expandStrip = null!;
    private bool _sidebarCollapsed;
    private CameraZoomPanel _zoomPanel = null!;
    private int _presetCols;
    private Button[] _presetButtons = [];

    public MainForm(
        ISettingsService settings,
        IRtspStreamService rtspService,
        IOnvifService onvifService,
        IDiscoveryService discoveryService)
    {
        _settings = settings;
        _rtspService = rtspService;
        _onvifService = onvifService;
        _discoveryService = discoveryService;

        InitializeComponent();

        Text = "OvifViewer";
        MinimumSize = new Size(800, 600);

        var s = _settings.Settings;
        Bounds = new Rectangle(s.MainWindowX, s.MainWindowY, s.MainWindowWidth, s.MainWindowHeight);
        if (s.MainWindowMaximized) WindowState = FormWindowState.Maximized;

        // ── Canvas ────────────────────────────────────────────────────────────
        _canvas = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(15, 15, 15),
        };
        _canvas.Resize += (_, _) =>
        {
            if (_fullScreenPanel != null)
                _fullScreenPanel.Bounds = new Rectangle(Point.Empty, _canvas.ClientSize);
            else if (!_hasManualLayout)
                TileAll();
        };

        // ── Status bar ────────────────────────────────────────────────────────
        _statusLabel = new ToolStripStatusLabel
        {
            Text = "No cameras",
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _statusBar = new StatusStrip { SizingGrip = false };
        _statusBar.Items.Add(_statusLabel);

        // ── Sidebar ───────────────────────────────────────────────────────────
        _cameraList = new ListView
        {
            Dock      = DockStyle.Fill,
            View      = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BackColor  = Color.FromArgb(22, 22, 22),
            ForeColor  = Color.FromArgb(210, 210, 210),
            BorderStyle = BorderStyle.None,
            Font       = new Font("Segoe UI", 8.5f),
            OwnerDraw  = true,
        };
        _cameraList.Columns.Add("Camera", 96);
        _cameraList.Columns.Add("IP", 82);
        _cameraList.DrawColumnHeader += (_, e) =>
        {
            e.Graphics.FillRectangle(new SolidBrush(Color.FromArgb(30, 30, 30)), e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header!.Text, _cameraList.Font,
                e.Bounds, Color.FromArgb(150, 150, 150),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        };
        _cameraList.DrawItem += (_, e) => e.DrawDefault = true;
        _cameraList.DrawSubItem += (_, e) =>
        {
            var item = e.Item!;
            bool selected = item.Selected;
            var bg = selected ? Color.FromArgb(45, 80, 130) : Color.FromArgb(22, 22, 22);
            e.Graphics.FillRectangle(new SolidBrush(bg), e.Bounds);
            var fg = e.ColumnIndex == 1
                ? (Color)(item.Tag ?? Color.FromArgb(210, 210, 210))
                : Color.FromArgb(210, 210, 210);
            TextRenderer.DrawText(e.Graphics, e.SubItem!.Text, _cameraList.Font,
                e.Bounds, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };

        var collapseBtn = new Button
        {
            Text      = "◀",
            Dock      = DockStyle.Right,
            Width     = 24,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(130, 130, 130),
            BackColor = Color.Transparent,
            Font      = new Font("Segoe UI", 7f),
            Cursor    = Cursors.Hand,
        };
        collapseBtn.FlatAppearance.BorderSize = 0;
        collapseBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(50, 50, 50);
        collapseBtn.Click += (_, _) => ToggleSidebar();

        var sidebarHeader = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 24,
            BackColor = Color.FromArgb(30, 30, 30),
        };
        var headerLabel = new Label
        {
            Text      = "CAMERAS",
            Dock      = DockStyle.Fill,
            ForeColor = Color.FromArgb(130, 130, 130),
            BackColor = Color.Transparent,
            Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(8, 0, 0, 0),
        };
        sidebarHeader.Controls.Add(headerLabel);
        sidebarHeader.Controls.Add(collapseBtn);

        _sidebar = new Panel
        {
            Dock      = DockStyle.Left,
            Width     = 182,
            BackColor = Color.FromArgb(22, 22, 22),
        };
        _sidebar.Controls.Add(_cameraList);
        _sidebar.Controls.Add(sidebarHeader);

        _splitter = new Splitter
        {
            Dock      = DockStyle.Left,
            Width     = 3,
            BackColor = Color.FromArgb(45, 45, 45),
            MinSize   = 120,
            MinExtra  = 300,
        };

        var expandBtn = new Button
        {
            Text      = "▶",
            Dock      = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(130, 130, 130),
            BackColor = Color.Transparent,
            Font      = new Font("Segoe UI", 7f),
            Cursor    = Cursors.Hand,
        };
        expandBtn.FlatAppearance.BorderSize = 0;
        expandBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(50, 50, 50);
        expandBtn.Click += (_, _) => ToggleSidebar();

        _expandStrip = new Panel
        {
            Dock      = DockStyle.Left,
            Width     = 20,
            BackColor = Color.FromArgb(30, 30, 30),
            Visible   = false,
        };
        _expandStrip.Controls.Add(expandBtn);

        _zoomPanel = new CameraZoomPanel(_rtspService, _onvifService);

        _cameraList.SelectedIndexChanged += OnCameraListSelectionChanged;
        _cameraList.MouseClick         += OnCameraListMouseClick;

        // ── Grid preset toolbar ───────────────────────────────────────────────
        var gridToolbar = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 30,
            BackColor = Color.FromArgb(30, 30, 30),
        };

        (string Text, int Cols)[] presets =
        [
            ("Auto", 0), ("1×1", 1), ("2×2", 2), ("3×3", 3), ("4×4", 4),
        ];

        var presetBtns = new Button[presets.Length];
        int bx = 8;
        for (int i = 0; i < presets.Length; i++)
        {
            var (text, cols) = presets[i];
            var btn = new Button
            {
                Text      = text,
                Left      = bx,
                Top       = 3,
                Width     = 44,
                Height    = 24,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(200, 200, 200),
                BackColor = cols == 0 ? Color.FromArgb(60, 80, 120) : Color.FromArgb(45, 45, 45),
                Font      = new Font("Segoe UI", 8f),
                Cursor    = Cursors.Hand,
                Tag       = cols,
            };
            btn.FlatAppearance.BorderColor       = Color.FromArgb(65, 65, 65);
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(65, 65, 65);
            int c = cols;
            btn.Click += (_, _) => ApplyGridPreset(c);
            gridToolbar.Controls.Add(btn);
            presetBtns[i] = btn;
            bx += btn.Width + 4;
        }
        _presetButtons = presetBtns;

        var contentPanel = new Panel { Dock = DockStyle.Fill };
        contentPanel.Controls.Add(_canvas);
        contentPanel.Controls.Add(gridToolbar);

        Controls.Add(contentPanel);
        Controls.Add(_splitter);
        Controls.Add(_expandStrip);
        Controls.Add(_sidebar);
        Controls.Add(_statusBar);

        _layoutSaveTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _layoutSaveTimer.Tick += (_, _) => { _layoutSaveTimer.Stop(); _settings.Save(); };

        BuildMenu();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _zoomPanel.Owner = this;  // set once — Owner requires handle to exist
        _mouseHook = new MouseHook(_panels, () => _fullScreenPanel, ToggleFullScreen, ExitFullScreen);
        await RestoreCameraPanelsAsync();
    }

    // ── Camera panels ─────────────────────────────────────────────────────────

    private async Task RestoreCameraPanelsAsync()
    {
        var cameras = _settings.Settings.Cameras.Where(c => c.AutoConnect).ToList();

        // Fetch all missing RTSP URIs in parallel — the only slow part
        await Task.WhenAll(cameras
            .Where(c => string.IsNullOrEmpty(c.RtspUri))
            .Select(async c =>
            {
                try   { c.RtspUri = await _onvifService.GetStreamUriAsync(c); }
                catch (Exception ex) { Log.Warning(ex, "Could not fetch RTSP URI for {Name}", c.DisplayName); }
            }));

        _settings.Save();

        // Add panels on the UI thread (fast — URIs already resolved above)
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
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not fetch RTSP URI for {Name}", camera.DisplayName);
            }
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

        _canvas.Controls.Add(panel);
        _panels[camera.Id] = panel;

        AddCameraToList(camera);

        // Restore saved bounds if any; otherwise fall through to TileAll
        var saved = _settings.Settings.PanelLayouts.FirstOrDefault(l => l.CameraId == camera.Id);
        if (saved != null)
        {
            panel.SetBounds(saved.X, saved.Y, saved.Width, saved.Height);
            _hasManualLayout = true;
        }

        panel.StartStream();

        if (!_hasManualLayout)
            TileAll();
        else if (saved == null)
            panel.SetBounds(20, 20, 320, 240);  // new camera in manual mode — place at default

        UpdateStatusBar();
    }

    public void RemoveCameraPanel(Guid cameraId)
    {
        if (!_panels.TryGetValue(cameraId, out var panel)) return;

        if (_fullScreenPanel == panel) ExitFullScreen();

        panel.StopStream();
        _canvas.Controls.Remove(panel);
        panel.Dispose();
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
        existing.X      = panel.Left;
        existing.Y      = panel.Top;
        existing.Width  = panel.Width;
        existing.Height = panel.Height;
        existing.ZOrder = _canvas.Controls.GetChildIndex(panel);

        _layoutSaveTimer.Stop();
        _layoutSaveTimer.Start();
    }

    // ── Grid / full-screen ────────────────────────────────────────────────────

    private void TileAll()
    {
        var panels = _panels.Values.ToList();
        if (panels.Count == 0) return;

        int cols = _presetCols > 0 ? _presetCols : (int)Math.Ceiling(Math.Sqrt(panels.Count));
        int rows = (int)Math.Ceiling(panels.Count / (double)cols);
        int w = Math.Max(1, _canvas.ClientSize.Width / cols);
        int h = Math.Max(1, _canvas.ClientSize.Height / rows);

        for (int i = 0; i < panels.Count; i++)
        {
            panels[i].Visible = true;
            panels[i].SetBounds(i % cols * w, i / cols * h, w, h);
        }
    }

    private void ToggleFullScreen(CameraPanel panel)
    {
        if (_fullScreenPanel == panel)
            ExitFullScreen();
        else
            EnterFullScreen(panel);
    }

    private void EnterFullScreen(CameraPanel panel)
    {
        _fullScreenPanel = panel;
        foreach (var p in _panels.Values)
            p.Visible = (p == panel);
        panel.Bounds = new Rectangle(Point.Empty, _canvas.ClientSize);
        panel.BringToFront();
    }

    private void ExitFullScreen()
    {
        _fullScreenPanel = null;
        if (_hasManualLayout)
        {
            foreach (var p in _panels.Values)
            {
                p.Visible = true;
                var saved = _settings.Settings.PanelLayouts.FirstOrDefault(l => l.CameraId == p.Camera.Id);
                if (saved != null)
                    p.SetBounds(saved.X, saved.Y, saved.Width, saved.Height);
            }
        }
        else
        {
            TileAll();
        }
    }

    // ── Menu ──────────────────────────────────────────────────────────────────

    private void BuildMenu()
    {
        var menu = new MenuStrip();

        var cameraMenu = new ToolStripMenuItem("Cameras");
        cameraMenu.DropDownItems.Add("Discover Cameras…", null, OnDiscoverClick);
        cameraMenu.DropDownItems.Add("Add Camera Manually…", null, OnAddManualClick);
        cameraMenu.DropDownItems.Add(new ToolStripSeparator());
        cameraMenu.DropDownItems.Add("Manage Cameras…", null, OnManageCamerasClick);
        cameraMenu.DropDownItems.Add(new ToolStripSeparator());
        cameraMenu.DropDownItems.Add("Export Camera Config…", null, OnExportCamerasClick);
        cameraMenu.DropDownItems.Add("Import Camera Config…", null, OnImportCamerasClick);

        var viewMenu = new ToolStripMenuItem("View");
        viewMenu.DropDownItems.Add("Reconnect All", null, OnReconnectAllClick);
        viewMenu.DropDownItems.Add("Reset Layout", null, OnResetLayoutClick);

        menu.Items.Add(cameraMenu);
        menu.Items.Add(viewMenu);
        Controls.Add(menu);
        MainMenuStrip = menu;
    }

    private void OnDiscoverClick(object? sender, EventArgs e)
    {
        using var form = new DiscoveryForm(_discoveryService, _onvifService, _settings);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            foreach (var cam in form.SelectedCameras)
                _ = AddCameraPanelAsync(cam);
        }
    }

    private void OnAddManualClick(object? sender, EventArgs e)
    {
        using var form = new AddCameraForm();
        if (form.ShowDialog(this) == DialogResult.OK && form.Result != null)
        {
            _settings.Settings.Cameras.Add(form.Result);
            _settings.Save();
            _ = AddCameraPanelAsync(form.Result);
        }
    }

    private void OnManageCamerasClick(object? sender, EventArgs e)
    {
        using var form = new ManageCamerasForm(_settings, _onvifService);
        form.ShowDialog(this);
    }

    private void OnExportCamerasClick(object? sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog
        {
            Title      = "Export Camera Config",
            Filter     = "JSON files (*.json)|*.json",
            FileName   = "cameras.json",
            DefaultExt = "json",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _settings.ExportCameras(dlg.FileName);
            MessageBox.Show(this,
                $"Exported {_settings.Settings.Cameras.Count} camera(s) to:\n{dlg.FileName}\n\nNote: passwords are stored in plaintext in this file.",
                "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnImportCamerasClick(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title  = "Import Camera Config",
            Filter = "JSON files (*.json)|*.json",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var added = _settings.ImportCameras(dlg.FileName);
            foreach (var cam in added)
                _ = AddCameraPanelAsync(cam);

            MessageBox.Show(this,
                added.Count > 0
                    ? $"Imported {added.Count} new camera(s)."
                    : "No new cameras found (all IDs already exist).",
                "Import Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Import failed: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnReconnectAllClick(object? sender, EventArgs e)
    {
        foreach (var panel in _panels.Values)
        {
            panel.StopStream();
            panel.StartStream();
        }
    }

    private void ToggleSidebar()
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        _sidebar.Visible     = !_sidebarCollapsed;
        _splitter.Visible    = !_sidebarCollapsed;
        _expandStrip.Visible = _sidebarCollapsed;
    }

    private void ShowInZoomPanel(CameraConfig camera)
    {
        if (!_zoomPanel.Visible)
        {
            var screen = Screen.FromControl(this).WorkingArea;
            int pw = _zoomPanel.Width;
            int ph = _zoomPanel.Height;

            // Prefer right of main window; fall back to left if it would go off-screen
            int x = Right + 8;
            if (x + pw > screen.Right)
                x = Math.Max(screen.Left, Left - pw - 8);

            int y = Top + (Height - ph) / 2;
            y = Math.Max(screen.Top, Math.Min(screen.Bottom - ph, y));

            _zoomPanel.Location = new Point(x, y);
        }

        _zoomPanel.Visible = true;
        _zoomPanel.Activate();
        _ = _zoomPanel.LoadCameraAsync(camera);
    }

    private void HideZoomPanel()
    {
        _zoomPanel.Clear();
        _zoomPanel.Hide();
        _cameraList.SelectedItems.Clear();
    }

    private void OnCameraListSelectionChanged(object? sender, EventArgs e)
    {
        if (_cameraList.SelectedItems.Count == 0) return;
        var item = _cameraList.SelectedItems[0];
        if (!Guid.TryParse(item.Name, out var id)) return;
        if (!_panels.TryGetValue(id, out var panel)) return;
        _cameraList.SelectedItems.Clear();   // deselect so repeated clicks work
        ToggleFullScreen(panel);
    }

    private void OnCameraListMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var item = _cameraList.GetItemAt(e.X, e.Y);
        if (item == null || !Guid.TryParse(item.Name, out var id)) return;
        if (!_panels.TryGetValue(id, out var panel)) return;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Edit…",     null, (_, _) => EditCamera(panel.Camera));
        menu.Items.Add("Reconnect", null, (_, _) => { panel.StopStream(); panel.StartStream(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Remove",    null, (_, _) => RemoveCameraPanel(id));
        menu.Show(_cameraList, e.Location);
    }

    private void EditCamera(CameraConfig camera)
    {
        using var form = new CameraSettingsForm(camera, _onvifService);
        if (form.ShowDialog(this) != DialogResult.OK || form.Result == null) return;

        var r = form.Result;
        // Mutate in-place so the live CameraPanel reference stays valid
        camera.DisplayName          = r.DisplayName;
        camera.Username             = r.Username;
        camera.EncryptedPassword    = r.EncryptedPassword;
        camera.SelectedProfileToken = r.SelectedProfileToken;
        camera.PtzEnabled           = r.PtzEnabled;
        camera.AutoConnect          = r.AutoConnect;
        camera.RtspUri              = string.Empty;   // clear cache — re-fetched on connect

        _settings.Save();

        if (_listItems.TryGetValue(camera.Id, out var item))
        {
            item.Text = string.IsNullOrWhiteSpace(camera.DisplayName)
                ? camera.DeviceServiceUrl : camera.DisplayName;
            _cameraList.Invalidate();
        }

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
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not fetch RTSP URI for {Name} after edit", camera.DisplayName);
        }
        if (_panels.ContainsKey(camera.Id))
            panel.StartStream();
    }

    private void ApplyGridPreset(int cols)
    {
        _presetCols      = cols;
        _hasManualLayout = false;

        foreach (var b in _presetButtons)
            b.BackColor = b.Tag is int bc && bc == cols
                ? Color.FromArgb(60, 80, 120)
                : Color.FromArgb(45, 45, 45);

        TileAll();
    }

    private void OnResetLayoutClick(object? sender, EventArgs e)
    {
        _settings.Settings.PanelLayouts.Clear();
        _settings.Save();
        ApplyGridPreset(0);
    }

    // ── Window state / close ──────────────────────────────────────────────────

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        SaveWindowState();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _mouseHook?.Dispose();
        _layoutSaveTimer.Stop();
        _layoutSaveTimer.Dispose();
        _zoomPanel.Clear();
        SaveWindowState();
        _settings.Save();

        foreach (var panel in _panels.Values)
            panel.StopStream();

        base.OnFormClosing(e);
    }

    // ── Sidebar helpers ───────────────────────────────────────────────────────

    private void AddCameraToList(CameraConfig camera)
    {
        string ip;
        try { ip = new Uri(camera.DeviceServiceUrl).Host; }
        catch { ip = camera.DeviceServiceUrl; }

        var name = string.IsNullOrWhiteSpace(camera.DisplayName) ? ip : camera.DisplayName;
        var item = new ListViewItem(name) { Tag = Color.FromArgb(255, 165, 0), Name = camera.Id.ToString() };
        item.SubItems.Add(ip);
        _cameraList.Items.Add(item);
        _listItems[camera.Id] = item;
    }

    private void RemoveCameraFromList(Guid cameraId)
    {
        if (_listItems.TryGetValue(cameraId, out var item))
        {
            _cameraList.Items.Remove(item);
            _listItems.Remove(cameraId);
        }
    }

    private void UpdateCameraListItem(Guid cameraId, string text, StatusState state)
    {
        if (!_listItems.TryGetValue(cameraId, out var item)) return;

        // Refresh display name in case it was populated after initial add
        if (_panels.TryGetValue(cameraId, out var panel) &&
            !string.IsNullOrWhiteSpace(panel.Camera.DisplayName))
            item.Text = panel.Camera.DisplayName;
        var color = state switch
        {
            StatusState.Live       => Color.FromArgb(50, 205, 50),
            StatusState.Connecting => Color.FromArgb(255, 165, 0),
            StatusState.Error      => Color.FromArgb(220, 80, 80),
            _                      => Color.FromArgb(120, 120, 120),
        };
        item.Tag = color;
        _cameraList.Invalidate();
    }

    private void UpdateStatusBar()
    {
        int total = _panels.Count;
        int live = _panels.Values.Count(p => p.IsStreaming);
        _statusLabel.Text = total == 0
            ? "No cameras — use Cameras menu to add"
            : $"{live}/{total} camera{(total == 1 ? "" : "s")} live";
    }

    private void SaveWindowState()
    {
        var s = _settings.Settings;
        if (WindowState == FormWindowState.Normal)
        {
            s.MainWindowX = Left;
            s.MainWindowY = Top;
            s.MainWindowWidth = Width;
            s.MainWindowHeight = Height;
        }
        s.MainWindowMaximized = WindowState == FormWindowState.Maximized;
    }
}

/// <summary>
/// WH_MOUSE_LL + WH_KEYBOARD_LL hooks. Detects double-clicks on camera panels
/// and Escape key to exit full-screen, bypassing LibVLC's HWND.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    private const int WH_MOUSE_LL    = 14;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int VK_ESCAPE      = 0x1B;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public System.Drawing.Point pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly HookProc _mouseProc;
    private readonly HookProc _keyProc;
    private readonly IntPtr _mouseHook;
    private readonly IntPtr _keyHook;
    private readonly Dictionary<Guid, CameraPanel> _panels;
    private readonly Func<CameraPanel?> _getFullScreen;
    private readonly Action<CameraPanel> _onToggle;
    private readonly Action _onEscape;

    private long _lastClickTick;
    private System.Drawing.Point _lastClickPt;

    public MouseHook(
        Dictionary<Guid, CameraPanel> panels,
        Func<CameraPanel?> getFullScreen,
        Action<CameraPanel> onToggle,
        Action onEscape)
    {
        _panels = panels;
        _getFullScreen = getFullScreen;
        _onToggle = onToggle;
        _onEscape = onEscape;
        _mouseProc = MouseCallback;
        _keyProc   = KeyCallback;
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL,    _mouseProc, IntPtr.Zero, 0);
        _keyHook   = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc,   IntPtr.Zero, 0);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == WM_LBUTTONDOWN)
        {
            var s = System.Runtime.InteropServices.Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var pt = s.pt;
            long now = Environment.TickCount64;

            bool isDoubleClick =
                (now - _lastClickTick) <= SystemInformation.DoubleClickTime &&
                Math.Abs(pt.X - _lastClickPt.X) <= SystemInformation.DoubleClickSize.Width &&
                Math.Abs(pt.Y - _lastClickPt.Y) <= SystemInformation.DoubleClickSize.Height;

            _lastClickTick = isDoubleClick ? 0 : now;
            _lastClickPt   = pt;

            if (isDoubleClick)
            {
                var fullPanel = _getFullScreen();

                // Check full-screen panel first — it covers all other bounds
                var ordered = _panels.Values
                    .Where(p => p.IsHandleCreated && !p.IsDisposed && p.Visible)
                    .OrderByDescending(p => p == fullPanel);

                foreach (var panel in ordered)
                {
                    if (panel.RectangleToScreen(panel.ClientRectangle).Contains(pt))
                    {
                        var captured = panel;
                        panel.BeginInvoke((Action)(() => _onToggle(captured)));
                        return (IntPtr)1;
                    }
                }
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && ((int)wParam == WM_KEYDOWN || (int)wParam == WM_SYSKEYDOWN))
        {
            var k = System.Runtime.InteropServices.Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (k.vkCode == VK_ESCAPE && _getFullScreen() != null)
            {
                var anyPanel = _panels.Values.FirstOrDefault(p => p.IsHandleCreated && !p.IsDisposed);
                anyPanel?.BeginInvoke((Action)_onEscape);
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        if (_keyHook   != IntPtr.Zero) UnhookWindowsHookEx(_keyHook);
    }
}
