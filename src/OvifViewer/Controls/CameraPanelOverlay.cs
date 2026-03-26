using OvifViewer.Services;
using OvifViewer.Models;

namespace OvifViewer.Controls;

public enum StatusState { Connecting, Live, Idle, Error }

/// <summary>
/// Borderless overlay form parented to CameraPanel via Win32 SetParent.
/// Auto-hides when the cursor leaves the panel. Forwards title-bar drags
/// back to CameraPanel so the panel moves correctly.
///
/// Title-bar layout (right to left):  [✕] [⋮] [▤ stream] [🔊 audio] | [Camera Name ...]
/// Each icon button opens a dropdown menu.
/// </summary>
public class CameraPanelOverlay : Form
{
    private readonly CameraPanel _owner;
    private readonly IOnvifService? _onvifService;

    private Panel _titleBar = null!;
    private Label _titleLabel = null!;
    private Label _statusLabel = null!;
    private PictureBox _statusDot = null!;
    private Panel _ptzPanel = null!;
    private Button _audioBtn = null!;
    private Button _streamBtn = null!;
    private ContextMenuStrip _actionsMenu = null!;
    private readonly List<(ResizeEdge Edge, Panel Handle)> _resizeHandles = [];

    private List<CameraProfile> _profiles = [];
    private bool _profilesLoaded;

    private const int TitleBarHeight = 30;

    // Title-bar drag state
    private Point _titleDragStart;
    private bool _titleDragging;
    private bool _titleDragMoved;

    public event Action<int, int>? MoveRequested;
    public event Action<int, int, ResizeEdge, Rectangle>? ResizeRequested;

    public CameraPanelOverlay(CameraPanel owner, IOnvifService? onvifService = null)
    {
        _owner = owner;
        _onvifService = onvifService;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        BackColor       = Color.Magenta;
        TransparencyKey = Color.Magenta;
        TopMost         = false;
        StartPosition   = FormStartPosition.Manual;

        BuildActionsMenu();
        BuildResizeHandles();   // before title bar so title bar z-order is on top
        BuildTitleBar();
        BuildStatusBar();
        BuildPtzPanel();
    }

    // ── Build UI ──────────────────────────────────────────────────────────────

    private void BuildActionsMenu()
    {
        _actionsMenu = new ContextMenuStrip();
        _actionsMenu.Items.Add("Reconnect",        null, (_, _) => { _owner.StopStream(); _owner.StartStream(); });
        _actionsMenu.Items.Add("Show in Zoom Panel", null, (_, _) => _owner.RequestZoom());
        _actionsMenu.Items.Add(new ToolStripSeparator());
        _actionsMenu.Items.Add("Remove Camera",    null, (_, _) => _owner.RequestRemove());
    }

    private void BuildTitleBar()
    {
        _titleBar = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = TitleBarHeight,
            BackColor = Color.FromArgb(220, 22, 22, 22),
            Cursor    = Cursors.SizeAll,
        };

        _titleLabel = new Label
        {
            AutoSize  = false,
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font      = new Font("Segoe UI", 9f),
            Padding   = new Padding(8, 0, 0, 0),
            Text      = _owner.Camera.DisplayName,
            Cursor    = Cursors.SizeAll,
        };

        // Close — direct action, no menu
        var closeBtn = MakeIconButton("✕", Color.FromArgb(196, 43, 28));
        closeBtn.Click += (_, _) => _owner.RequestRemove();

        // Actions menu  ⋮
        var actionsBtn = MakeIconButton("⋮", Color.Transparent);
        actionsBtn.Click += (_, _) => _actionsMenu.Show(actionsBtn, new Point(0, actionsBtn.Height));

        // Stream / profile selector  ▤
        _streamBtn = MakeIconButton("▤", Color.Transparent);
        _streamBtn.Click += (_, _) => ShowStreamMenu();

        // Audio  🔊 / 🔇
        _audioBtn = MakeIconButton("🔊", Color.Transparent);
        _audioBtn.Font = new Font("Segoe UI Emoji", 9f);
        _audioBtn.Click += (_, _) => ShowAudioMenu();

        _titleBar.Controls.Add(_titleLabel);    // Fill — must be added first
        _titleBar.Controls.Add(closeBtn);        // DockStyle.Right — rightmost
        _titleBar.Controls.Add(actionsBtn);
        _titleBar.Controls.Add(_streamBtn);
        _titleBar.Controls.Add(_audioBtn);       // leftmost of the right-docked buttons

        _titleBar.DoubleClick   += (_, _) => _owner.ToggleFullSize();
        _titleLabel.DoubleClick += (_, _) => _owner.ToggleFullSize();
        _titleBar.MouseDown     += (_, e) => { if (e.Button == MouseButtons.Right) _actionsMenu.Show(Cursor.Position); };

        _titleBar.MouseDown   += TitleDragDown;
        _titleBar.MouseMove   += TitleDragMove;
        _titleBar.MouseUp     += TitleDragUp;
        _titleLabel.MouseDown += TitleDragDown;

        Controls.Add(_titleBar);
    }

    // ── Audio dropdown ────────────────────────────────────────────────────────

    private void ShowAudioMenu()
    {
        var menu = new ContextMenuStrip();

        var muteItem = new ToolStripMenuItem(_owner.IsMuted ? "Unmute" : "Mute");
        muteItem.Click += (_, _) =>
        {
            _owner.ToggleMute();
            RefreshAudioButton();
        };
        menu.Items.Add(muteItem);
        menu.Items.Add(new ToolStripSeparator());

        var volLabel = new ToolStripLabel($"Volume: {_owner.Volume}%")
        {
            Font = new Font("Segoe UI", 8f),
        };
        menu.Items.Add(volLabel);

        var trackBar = new TrackBar
        {
            Minimum       = 0,
            Maximum       = 200,
            Value         = _owner.Volume,
            TickFrequency = 50,
            SmallChange   = 5,
            LargeChange   = 20,
            Width         = 170,
        };
        trackBar.ValueChanged += (_, _) =>
        {
            _owner.SetVolume(trackBar.Value);
            volLabel.Text = $"Volume: {trackBar.Value}%";
            RefreshAudioButton();
        };
        menu.Items.Add(new ToolStripControlHost(trackBar) { AutoSize = false, Size = new Size(170, trackBar.PreferredSize.Height) });

        menu.Show(_audioBtn, new Point(0, _audioBtn.Height));
    }

    private void RefreshAudioButton()
    {
        if (_audioBtn != null)
            _audioBtn.Text = _owner.IsMuted ? "🔇" : "🔊";
    }

    // ── Stream / profile dropdown ─────────────────────────────────────────────

    private void ShowStreamMenu()
    {
        var menu = new ContextMenuStrip();

        if (_profiles.Count == 0)
        {
            var loading = _profilesLoaded ? "No profiles found" : "Loading…";
            menu.Items.Add(new ToolStripMenuItem(loading) { Enabled = false });
        }
        else
        {
            foreach (var p in _profiles)
            {
                var label = string.IsNullOrEmpty(p.Name) ? p.Token : p.Name;
                if (p.Width > 0 && p.Height > 0) label += $"  {p.Width}×{p.Height}";
                if (!string.IsNullOrEmpty(p.Encoding)) label += $"  {p.Encoding}";

                var item = new ToolStripMenuItem(label)
                {
                    Checked = p.Token == _owner.Camera.SelectedProfileToken,
                };
                var token = p.Token;
                item.Click += (_, _) => _owner.RequestProfileChange(token);
                menu.Items.Add(item);
            }
        }

        menu.Show(_streamBtn, new Point(0, _streamBtn.Height));
    }

    // ── Profile loading ───────────────────────────────────────────────────────

    private async Task LoadProfilesAsync()
    {
        try
        {
            var profiles = (await _onvifService!.GetProfilesAsync(_owner.Camera)).ToList();
            if (!IsHandleCreated || IsDisposed) return;
            BeginInvoke(() => _profiles = profiles);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not load profiles for {Name}", _owner.Camera.DisplayName);
        }
    }

    // ── Drag-to-move ──────────────────────────────────────────────────────────

    private void TitleDragDown(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _titleDragStart   = Cursor.Position;
        _titleDragging    = true;
        _titleDragMoved   = false;
        _titleBar.Capture = true;
    }

    private void TitleDragMove(object? s, MouseEventArgs e)
    {
        if (!_titleDragging || e.Button != MouseButtons.Left) return;
        var cur = Cursor.Position;
        int dx = cur.X - _titleDragStart.X;
        int dy = cur.Y - _titleDragStart.Y;
        if (!_titleDragMoved && Math.Abs(dx) <= 3 && Math.Abs(dy) <= 3) return;
        _titleDragMoved   = true;
        MoveRequested?.Invoke(dx, dy);
        _titleDragStart   = cur;
    }

    private void TitleDragUp(object? s, MouseEventArgs e)
    {
        _titleDragging    = false;
        _titleBar.Capture = false;
    }

    // ── Resize handles ────────────────────────────────────────────────────────

    private void BuildResizeHandles()
    {
        (ResizeEdge Edge, Cursor Cur)[] defs =
        [
            (ResizeEdge.Left,                      Cursors.SizeWE),
            (ResizeEdge.Right,                     Cursors.SizeWE),
            (ResizeEdge.Bottom,                    Cursors.SizeNS),
            (ResizeEdge.Left  | ResizeEdge.Bottom, Cursors.SizeNESW),
            (ResizeEdge.Right | ResizeEdge.Bottom, Cursors.SizeNWSE),
        ];

        foreach (var (edge, cur) in defs)
        {
            var handle = new Panel { BackColor = Color.FromArgb(55, 55, 65), Cursor = cur };

            Point     start      = default;
            Rectangle origBounds = default;

            handle.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                start          = Cursor.Position;
                origBounds     = _owner.Bounds;
                handle.Capture = true;
            };
            handle.MouseMove += (_, e) =>
            {
                if (!handle.Capture || e.Button != MouseButtons.Left) return;
                var c = Cursor.Position;
                ResizeRequested?.Invoke(c.X - start.X, c.Y - start.Y, edge, origBounds);
            };
            handle.MouseUp += (_, _) => handle.Capture = false;

            Controls.Add(handle);
            _resizeHandles.Add((edge, handle));
        }
    }

    private void PositionResizeHandles()
    {
        const int E = 5, C = 10;
        foreach (var (edge, handle) in _resizeHandles)
        {
            bool left   = edge.HasFlag(ResizeEdge.Left);
            bool right  = edge.HasFlag(ResizeEdge.Right);
            bool bottom = edge.HasFlag(ResizeEdge.Bottom);
            bool corner = (left || right) && bottom;

            int x, y, w, h;
            if (corner)
            {
                w = h = C;
                x = right ? Width - C : 0;
                y = Height - C;
            }
            else if (left || right)
            {
                x = right ? Width - E : 0;
                y = TitleBarHeight;
                w = E;
                h = Math.Max(0, Height - TitleBarHeight - C);
            }
            else
            {
                x = C; y = Height - E;
                w = Math.Max(0, Width - C * 2);
                h = E;
            }
            handle.SetBounds(x, y, w, h);
            handle.BringToFront();
        }
    }

    // ── Status bar ────────────────────────────────────────────────────────────

    private void BuildStatusBar()
    {
        _statusDot = new PictureBox
        {
            Width     = 8,
            Height    = 8,
            BackColor = Color.Orange,
            Cursor    = Cursors.Default,
        };

        _statusLabel = new Label
        {
            AutoSize  = true,
            ForeColor = Color.FromArgb(200, 200, 200),
            BackColor = Color.Transparent,
            Font      = new Font("Segoe UI", 7.5f),
            Text      = "Connecting…",
            Cursor    = Cursors.Default,
        };

        Controls.Add(_statusDot);
        Controls.Add(_statusLabel);
    }

    // ── PTZ panel ─────────────────────────────────────────────────────────────

    private void BuildPtzPanel()
    {
        _ptzPanel = new Panel
        {
            BackColor = Color.FromArgb(180, 20, 20, 20),
            Visible   = _owner.Camera.PtzEnabled,
            Size      = new Size(100, 70),
        };

        (string text, float pan, float tilt, float zoom)[] defs =
        [
            ("▲",  0f,    0.5f,  0f),
            ("◄", -0.5f,  0f,    0f),
            ("■",  0f,    0f,    0f),
            ("►",  0.5f,  0f,    0f),
            ("▼",  0f,   -0.5f,  0f),
        ];

        int col = 0, row = 0;
        foreach (var (text, pan, tilt, zoom) in defs)
        {
            var btn = new Button
            {
                Text      = text,
                Width     = 26,
                Height    = 26,
                Left      = col * 28 + 4,
                Top       = row * 28 + 4,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(60, 60, 60),
                Font      = new Font("Segoe UI", 8f),
                Cursor    = Cursors.Hand,
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
            btn.FlatAppearance.BorderSize  = 1;

            float p = pan, t = tilt, z = zoom;
            if (text == "■")
                btn.Click     += async (_, _) => await SafePtzAsync(() => _onvifService!.PtzStopAsync(_owner.Camera));
            else
            {
                btn.MouseDown += async (_, _) => await SafePtzAsync(() => _onvifService!.PtzMoveAsync(_owner.Camera, p, t, z));
                btn.MouseUp   += async (_, _) => await SafePtzAsync(() => _onvifService!.PtzStopAsync(_owner.Camera));
            }

            _ptzPanel.Controls.Add(btn);
            col++;
            if (col > 2) { col = 0; row++; }
        }

        Controls.Add(_ptzPanel);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void UpdateStatus(string text, StatusState state)
    {
        if (InvokeRequired) { BeginInvoke(() => UpdateStatus(text, state)); return; }
        _statusLabel.Text    = text;
        _statusDot.BackColor = state switch
        {
            StatusState.Live       => Color.FromArgb(50, 205, 50),
            StatusState.Connecting => Color.FromArgb(255, 165, 0),
            StatusState.Error      => Color.FromArgb(220, 50, 50),
            _                      => Color.FromArgb(100, 100, 100),
        };
        PositionStatusItems();
    }

    public void SetOverlayVisible(bool visible)
    {
        if (InvokeRequired) { BeginInvoke(() => SetOverlayVisible(visible)); return; }
        Visible = visible;
        if (visible && !_profilesLoaded && _onvifService != null)
        {
            _profilesLoaded = true;
            _ = LoadProfilesAsync();
        }
    }

    public void SyncBounds()
    {
        if (!_owner.IsHandleCreated) return;
        Bounds = new Rectangle(0, 0, _owner.Width, _owner.Height);
        PositionPtzPanel();
        PositionStatusItems();
        PositionResizeHandles();
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PositionPtzPanel();
        PositionStatusItems();
        PositionResizeHandles();
    }

    private void PositionPtzPanel()
    {
        if (_ptzPanel == null) return;
        _ptzPanel.Location = new Point(8, Height - _ptzPanel.Height - 8);
    }

    private void PositionStatusItems()
    {
        if (_statusLabel == null || _statusDot == null) return;
        int dotX = Width - _statusDot.Width - 8;
        int dotY = Height - _statusDot.Height - 8;
        _statusDot.Location   = new Point(dotX, dotY);
        _statusLabel.Location = new Point(dotX - _statusLabel.Width - 4, dotY - 1);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Button MakeIconButton(string text, Color hoverColor)
    {
        var btn = new Button
        {
            Text      = text,
            Width     = TitleBarHeight,
            Height    = TitleBarHeight,
            Dock      = DockStyle.Right,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font      = new Font("Segoe UI", 9f),
            Cursor    = Cursors.Hand,
        };
        btn.FlatAppearance.BorderSize             = 0;
        btn.FlatAppearance.MouseOverBackColor      = hoverColor == Color.Transparent
            ? Color.FromArgb(60, 60, 60)
            : hoverColor;
        return btn;
    }

    private static async Task SafePtzAsync(Func<Task> action)
    {
        try   { await action(); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "PTZ command failed"); }
    }

    // ── Form lifecycle ────────────────────────────────────────────────────────

    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated) { CreateHandle(); base.SetVisibleCore(false); return; }
        base.SetVisibleCore(value);
    }

    protected override bool ShowWithoutActivation => true;
}
