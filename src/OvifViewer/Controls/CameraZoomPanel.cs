using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using OvifViewer.Models;
using OvifViewer.Services;
using Serilog;

namespace OvifViewer.Controls;

/// <summary>
/// Floating, resizable panel that shows a live feed of a selected camera
/// and exposes PTZ controls when the camera supports it.
/// Drag via the title bar; resize via the grip in the bottom-right corner.
/// </summary>
public class CameraZoomPanel : Form
{
    private const int HeaderHeight = 32;
    private const int GripSize     = 16;
    private const int BtnSize      = 30;
    private const int BtnGap       = 4;

    private readonly IRtspStreamService _rtspService;
    private readonly IOnvifService      _onvifService;

    private Panel     _header     = null!;
    private VideoView _videoView  = null!;
    private Label     _titleLabel = null!;
    private Panel     _ptzPanel   = null!;

    private MediaPlayer?  _player;
    private CameraConfig? _camera;

    // Drag state
    private Point _dragStart;
    private bool  _dragging;

    public CameraZoomPanel(IRtspStreamService rtspService, IOnvifService onvifService)
    {
        _rtspService  = rtspService;
        _onvifService = onvifService;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        BackColor       = Color.FromArgb(18, 18, 18);
        MinimumSize     = new Size(240, 200);
        Size            = new Size(380, 480);
        StartPosition   = FormStartPosition.Manual;

        BuildUI();
    }

    // ── Build UI ──────────────────────────────────────────────────────────────

    private void BuildUI()
    {
        // ── Header / title bar ─────────────────────────────────────────────
        _header = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = HeaderHeight,
            BackColor = Color.FromArgb(30, 30, 30),
            Cursor    = Cursors.SizeAll,
        };
        _titleLabel = new Label
        {
            Dock      = DockStyle.Fill,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font      = new Font("Segoe UI", 9f),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(8, 0, 0, 0),
            Cursor    = Cursors.SizeAll,
        };
        var closeBtn = new Button
        {
            Text      = "✕",
            Dock      = DockStyle.Right,
            Width     = HeaderHeight,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font      = new Font("Segoe UI", 9f),
            Cursor    = Cursors.Hand,
        };
        closeBtn.FlatAppearance.BorderSize             = 0;
        closeBtn.FlatAppearance.MouseOverBackColor     = Color.FromArgb(196, 43, 28);
        closeBtn.Click += (_, _) => Hide();

        _header.Controls.Add(_titleLabel);
        _header.Controls.Add(closeBtn);

        // Wire drag on header and title label
        _header.MouseDown     += HeaderDragDown;
        _header.MouseMove     += HeaderDragMove;
        _header.MouseUp       += HeaderDragUp;
        _titleLabel.MouseDown += HeaderDragDown;

        // ── Resize grip (bottom-right corner) ──────────────────────────────
        var grip = new Panel
        {
            Width     = GripSize,
            Height    = GripSize,
            BackColor = Color.FromArgb(50, 50, 50),
            Cursor    = Cursors.SizeNWSE,
        };
        grip.Paint += (_, e) =>
        {
            // Draw three diagonal lines as a visual grip indicator
            using var pen = new Pen(Color.FromArgb(100, 100, 100), 1);
            for (int i = 1; i <= 3; i++)
            {
                int o = i * 4;
                e.Graphics.DrawLine(pen, o, GripSize - 1, GripSize - 1, o);
            }
        };

        Point gripDragStart = default;
        Size  gripOrigSize  = default;
        grip.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            gripDragStart = Cursor.Position;
            gripOrigSize  = Size;
            grip.Capture  = true;
        };
        grip.MouseMove += (_, e) =>
        {
            if (!grip.Capture || e.Button != MouseButtons.Left) return;
            var cur = Cursor.Position;
            int nw = Math.Max(MinimumSize.Width,  gripOrigSize.Width  + (cur.X - gripDragStart.X));
            int nh = Math.Max(MinimumSize.Height, gripOrigSize.Height + (cur.Y - gripDragStart.Y));
            Size = new Size(nw, nh);
        };
        grip.MouseUp += (_, _) => grip.Capture = false;

        grip.Location = new Point(ClientSize.Width - GripSize, ClientSize.Height - GripSize);
        this.Resize  += (_, _) =>
            grip.Location = new Point(ClientSize.Width - GripSize, ClientSize.Height - GripSize);

        // ── PTZ panel ──────────────────────────────────────────────────────
        _ptzPanel         = BuildPtzPanel();
        _ptzPanel.Visible = false;

        // ── Video ──────────────────────────────────────────────────────────
        _videoView = new VideoView { Dock = DockStyle.Fill, BackColor = Color.Black };

        Controls.Add(_videoView);
        Controls.Add(grip);
        Controls.Add(_ptzPanel);
        Controls.Add(_header);
    }

    // ── Header drag ───────────────────────────────────────────────────────────

    private void HeaderDragDown(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragStart      = Cursor.Position;
        _dragging       = true;
        _header.Capture = true;
    }

    private void HeaderDragMove(object? s, MouseEventArgs e)
    {
        if (!_dragging || e.Button != MouseButtons.Left) return;
        var cur = Cursor.Position;
        Left += cur.X - _dragStart.X;
        Top  += cur.Y - _dragStart.Y;
        _dragStart = cur;
    }

    private void HeaderDragUp(object? s, MouseEventArgs e)
    {
        _dragging       = false;
        _header.Capture = false;
    }

    // ── PTZ controls ─────────────────────────────────────────────────────────

    // Col:  0    1    2
    // Row 0: -    ▲    -
    // Row 1: ◄    ■    ►
    // Row 2: -    ▼    -
    // Row 3: Z+   -    Z-
    private Panel BuildPtzPanel()
    {
        const int rows   = 4;
        const int padX   = 8;
        const int padY   = 8;
        int       panelH = padY * 2 + rows * BtnSize + (rows - 1) * BtnGap + 1;

        var panel = new Panel
        {
            Dock      = DockStyle.Bottom,
            Height    = panelH,
            BackColor = Color.FromArgb(25, 25, 25),
        };
        panel.Controls.Add(new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 1,
            BackColor = Color.FromArgb(50, 50, 50),
        });

        (string Label, int Col, int Row, float Pan, float Tilt, float Zoom)[] defs =
        [
            ("▲",  1, 0,  0f,    0.5f,  0f),
            ("◄",  0, 1, -0.5f,  0f,    0f),
            ("■",  1, 1,  0f,    0f,    0f),
            ("►",  2, 1,  0.5f,  0f,    0f),
            ("▼",  1, 2,  0f,   -0.5f,  0f),
            ("Z+", 0, 3,  0f,    0f,    0.5f),
            ("Z-", 2, 3,  0f,    0f,   -0.5f),
        ];

        foreach (var d in defs)
        {
            var btn = new Button
            {
                Text      = d.Label,
                Left      = padX + d.Col * (BtnSize + BtnGap),
                Top       = padY + 1 + d.Row * (BtnSize + BtnGap),
                Width     = BtnSize,
                Height    = BtnSize,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = d.Label == "■" ? Color.FromArgb(70, 40, 40) : Color.FromArgb(55, 55, 55),
                Font      = new Font("Segoe UI", 8f),
                Cursor    = Cursors.Hand,
            };
            btn.FlatAppearance.BorderColor        = Color.FromArgb(75, 75, 75);
            btn.FlatAppearance.MouseOverBackColor  = Color.FromArgb(85, 85, 85);

            float p = d.Pan, t = d.Tilt, z = d.Zoom;
            if (d.Label == "■")
                btn.Click += async (_, _) => await SafePtzAsync(() => _onvifService.PtzStopAsync(_camera!));
            else
            {
                btn.MouseDown += async (_, _) => await SafePtzAsync(() => _onvifService.PtzMoveAsync(_camera!, p, t, z));
                btn.MouseUp   += async (_, _) => await SafePtzAsync(() => _onvifService.PtzStopAsync(_camera!));
            }
            panel.Controls.Add(btn);
        }

        return panel;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task LoadCameraAsync(CameraConfig camera)
    {
        StopCurrent();
        _camera          = camera;
        _titleLabel.Text = string.IsNullOrWhiteSpace(camera.DisplayName)
            ? camera.DeviceServiceUrl : camera.DisplayName;

        if (string.IsNullOrEmpty(camera.RtspUri))
        {
            try { camera.RtspUri = await _onvifService.GetStreamUriAsync(camera); }
            catch (Exception ex)
            {
                Log.Warning(ex, "Zoom panel: could not fetch RTSP URI for {Name}", camera.DisplayName);
            }
        }

        if (!string.IsNullOrEmpty(camera.RtspUri) && _camera == camera)
        {
            _player      = new MediaPlayer(((RtspStreamService)_rtspService).LibVlc);
            _player.Hwnd = _videoView.Handle;
            _rtspService.Play(_player, camera.RtspUri, camera.Username, camera.GetPassword());
        }

        bool hasPtz = camera.PtzEnabled;
        if (!hasPtz)
        {
            try { hasPtz = await _onvifService.HasPtzAsync(camera); }
            catch { /* not supported */ }
        }
        if (_camera == camera)
            _ptzPanel.Visible = hasPtz;
    }

    public void Clear()
    {
        StopCurrent();
        _camera           = null;
        if (IsHandleCreated)
        {
            _titleLabel.Text  = "";
            _ptzPanel.Visible = false;
        }
    }

    // ── Prevent accidental close ──────────────────────────────────────────────

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Only intercept when the user closes the panel directly while it is visible.
        // If the owner (MainForm) is closing, Visible will be false (Clear/Hide was
        // already called) — let it close so it doesn't cancel the app exit.
        if (e.CloseReason == CloseReason.UserClosing && Visible)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override bool ShowWithoutActivation => false;

    // ── Internals ─────────────────────────────────────────────────────────────

    private void StopCurrent()
    {
        if (_player == null) return;
        _rtspService.Destroy(_player);
        _player = null;
    }

    private static async Task SafePtzAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Log.Warning(ex, "PTZ command failed"); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) StopCurrent();
        base.Dispose(disposing);
    }
}
