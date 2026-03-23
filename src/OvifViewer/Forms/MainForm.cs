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
        _cameraList.Columns.Add("Camera", 118);
        _cameraList.Columns.Add("Status", 60);
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

        var sidebarHeader = new Label
        {
            Text      = "CAMERAS",
            Dock      = DockStyle.Top,
            Height    = 24,
            ForeColor = Color.FromArgb(130, 130, 130),
            BackColor = Color.FromArgb(30, 30, 30),
            Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(8, 0, 0, 0),
        };

        _sidebar = new Panel
        {
            Dock      = DockStyle.Left,
            Width     = 182,
            BackColor = Color.FromArgb(22, 22, 22),
        };
        _sidebar.Controls.Add(_cameraList);
        _sidebar.Controls.Add(sidebarHeader);

        var splitter = new Splitter
        {
            Dock      = DockStyle.Left,
            Width     = 3,
            BackColor = Color.FromArgb(45, 45, 45),
            MinSize   = 120,
            MinExtra  = 300,
        };

        Controls.Add(_canvas);
        Controls.Add(splitter);
        Controls.Add(_sidebar);
        Controls.Add(_statusBar);

        BuildMenu();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _mouseHook = new MouseHook(_panels, () => _fullScreenPanel, ToggleFullScreen, ExitFullScreen);
        await RestoreCameraPanelsAsync();
    }

    // ── Camera panels ─────────────────────────────────────────────────────────

    private async Task RestoreCameraPanelsAsync()
    {
        foreach (var cam in _settings.Settings.Cameras.Where(c => c.AutoConnect))
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

        var panel = new CameraPanel(camera, _rtspService);
        panel.PanelRemoveRequested    += (_, _) => RemoveCameraPanel(camera.Id);
        panel.ToggleFullSizeRequested += (_, _) => ToggleFullScreen(panel);
        panel.StatusChanged           += (text, state) => UpdateCameraListItem(camera.Id, text, state);
        panel.LayoutChanged           += (_, _) => SavePanelLayout(panel);

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

    // ── Grid / full-screen ────────────────────────────────────────────────────

    private void TileAll()
    {
        var panels = _panels.Values.ToList();
        if (panels.Count == 0) return;

        int cols = (int)Math.Ceiling(Math.Sqrt(panels.Count));
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
        TileAll();
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

        var viewMenu = new ToolStripMenuItem("View");
        viewMenu.DropDownItems.Add("Reconnect All", null, OnReconnectAllClick);

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

    private void OnReconnectAllClick(object? sender, EventArgs e)
    {
        foreach (var panel in _panels.Values)
        {
            panel.StopStream();
            panel.StartStream();
        }
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
        var item = new ListViewItem(name) { Tag = Color.FromArgb(255, 165, 0), ToolTipText = ip };
        item.SubItems.Add("Connecting…");
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
        var color = state switch
        {
            StatusState.Live       => Color.FromArgb(50, 205, 50),
            StatusState.Connecting => Color.FromArgb(255, 165, 0),
            StatusState.Error      => Color.FromArgb(220, 80, 80),
            _                      => Color.FromArgb(120, 120, 120),
        };
        item.Tag = color;
        item.SubItems[1].Text = text;
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
