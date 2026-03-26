using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using OvifViewer.Models;
using OvifViewer.Services;
using Serilog;

namespace OvifViewer.Controls;

[Flags]
public enum ResizeEdge { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

public class CameraPanel : UserControl
{
    private const int HoverCheckInterval = 120;

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    private readonly IRtspStreamService _rtspService;
    private readonly IOnvifService _onvifService;
    private VideoView? _videoView;
    private MediaPlayer? _player;
    private CameraPanelOverlay? _overlay;
    private CancellationTokenSource? _streamCts;
    private int _retryCount;
    private readonly System.Windows.Forms.Timer _hoverTimer;

    public event EventHandler? PanelRemoveRequested;
    public event EventHandler? ToggleFullSizeRequested;
    public event EventHandler? ZoomRequested;
    public event Action<string, StatusState>? StatusChanged;
    public event EventHandler? LayoutChanged;
    public event Action<string>? ProfileChangeRequested;
    public event EventHandler? MuteChanged;

    public CameraConfig Camera { get; private set; }
    public bool IsStreaming { get; private set; }
    public bool IsHovered { get; private set; }
    public string CurrentStatus { get; private set; } = "Connecting…";
    public StatusState CurrentState { get; private set; } = StatusState.Connecting;

    public CameraPanel(CameraConfig camera, IRtspStreamService rtspService, IOnvifService onvifService)
    {
        Camera = camera;
        _rtspService = rtspService;
        _onvifService = onvifService;

        DoubleBuffered = true;
        BackColor = Color.FromArgb(18, 18, 18);

        _hoverTimer = new System.Windows.Forms.Timer { Interval = HoverCheckInterval };
        _hoverTimer.Tick += OnHoverTick;
        _hoverTimer.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        _videoView = new VideoView { Dock = DockStyle.Fill, BackColor = Color.Black };
        Controls.Add(_videoView);

        _overlay = new CameraPanelOverlay(this, _onvifService);

        _overlay.MoveRequested += (dx, dy) =>
        {
            if (Parent == null) return;
            int nx = Math.Max(0, Math.Min(Parent.ClientSize.Width  - Width,  Left + dx));
            int ny = Math.Max(0, Math.Min(Parent.ClientSize.Height - Height, Top  + dy));
            Location = new Point(nx, ny);
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        };

        _overlay.ResizeRequested += (dx, dy, edge, orig) =>
        {
            const int MinW = 160, MinH = 120;
            var b = orig;
            if (edge.HasFlag(ResizeEdge.Right))  b.Width  = Math.Max(MinW, b.Width  + dx);
            if (edge.HasFlag(ResizeEdge.Bottom)) b.Height = Math.Max(MinH, b.Height + dy);
            if (edge.HasFlag(ResizeEdge.Left))
            {
                int nw = Math.Max(MinW, b.Width - dx);
                b.X = b.Right - nw; b.Width = nw;
            }
            if (edge.HasFlag(ResizeEdge.Top))
            {
                int nh = Math.Max(MinH, b.Height - dy);
                b.Y = b.Bottom - nh; b.Height = nh;
            }
            SetBounds(b.X, b.Y, b.Width, b.Height);
            _overlay?.SyncBounds();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        };

        _ = _overlay.Handle;  // force handle creation without showing (SetVisibleCore override blocks Show before SetParent)
        SetParent(_overlay.Handle, Handle);
        _overlay.SyncBounds();

        Resize += (_, _) => _overlay?.SyncBounds();
        LocationChanged += (_, _) => _overlay?.SyncBounds();
    }

    // ── Streaming ─────────────────────────────────────────────────────────────

    public void StartStream()
    {
        if (string.IsNullOrEmpty(Camera.RtspUri))
        {
            Log.Warning("No RTSP URI for {Name}", Camera.DisplayName);
            NotifyStatus("No URL", StatusState.Error);
            return;
        }

        if (_videoView == null) return;

        _streamCts?.Cancel();
        _streamCts = new CancellationTokenSource();
        _retryCount = 0;

        _player = new MediaPlayer(((RtspStreamService)_rtspService).LibVlc);
        _player.Hwnd = _videoView.Handle;

        WirePlayerEvents();
        ConnectOnce();
    }

    public void StopStream()
    {
        _streamCts?.Cancel();
        IsStreaming = false;
        if (_player != null)
            _rtspService.Stop(_player);
        NotifyStatus("Stopped", StatusState.Idle);
    }

    public void RequestRemove()            => PanelRemoveRequested?.Invoke(this, EventArgs.Empty);
    public void ToggleFullSize()           => ToggleFullSizeRequested?.Invoke(this, EventArgs.Empty);
    public void RequestZoom()              => ZoomRequested?.Invoke(this, EventArgs.Empty);
    public void RequestProfileChange(string token) => ProfileChangeRequested?.Invoke(token);

    // ── Audio ─────────────────────────────────────────────────────────────────

    public bool IsMuted => _player?.Mute ?? Camera.Muted;
    public int Volume   => _player?.Volume ?? 100;

    public void ToggleMute()
    {
        if (_player == null) return;
        _player.Mute  = !_player.Mute;
        Camera.Muted  = _player.Mute;
        MuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetVolume(int volume)
    {
        if (_player == null) return;
        _player.Volume = Math.Clamp(volume, 0, 200);
        if (volume > 0 && _player.Mute)
        {
            _player.Mute = false;
            Camera.Muted = false;
            MuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ── Hover tracking ────────────────────────────────────────────────────────

    private void OnHoverTick(object? sender, EventArgs e)
    {
        if (!IsHandleCreated || IsDisposed) return;

        var cursor = Cursor.Position;
        var panelScreen = RectangleToScreen(ClientRectangle);
        var overlayScreen = _overlay != null
            ? new Rectangle(_overlay.PointToScreen(Point.Empty), _overlay.Size)
            : Rectangle.Empty;

        bool hovered = panelScreen.Contains(cursor) || overlayScreen.Contains(cursor);

        if (hovered != IsHovered)
        {
            IsHovered = hovered;
            _overlay?.SetOverlayVisible(hovered);
            Invalidate();
        }
    }

    // ── Painting ──────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var color = IsHovered
            ? Color.FromArgb(80, 140, 210)
            : Color.FromArgb(45, 45, 45);
        using var pen = new Pen(color, 2);
        e.Graphics.DrawRectangle(pen, 1, 1, Width - 2, Height - 2);
    }

    // ── Reconnect logic ───────────────────────────────────────────────────────

    private void ConnectOnce()
    {
        if (_streamCts?.IsCancellationRequested != false || _player == null) return;
        _rtspService.Play(_player, Camera.RtspUri, Camera.Username, Camera.GetPassword());
    }

    private void ScheduleReconnect()
    {
        var ct = _streamCts?.Token ?? CancellationToken.None;
        if (ct.IsCancellationRequested) return;

        _retryCount++;
        var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, _retryCount)));
        Log.Warning("Stream {Name} ended — reconnecting in {Delay:0}s", Camera.DisplayName, delay.TotalSeconds);

        _ = Task.Delay(delay, ct).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            SafeInvoke(ConnectOnce);
        });
    }

    private void WirePlayerEvents()
    {
        if (_player == null) return;

        _player.EncounteredError += (_, _) =>
        {
            IsStreaming = false;
            Log.Error("Stream error on {Name}", Camera.DisplayName);
            SafeInvoke(() =>
            {
                NotifyStatus("Error", StatusState.Error);
                ScheduleReconnect();
            });
        };

        _player.EndReached += (_, _) =>
        {
            IsStreaming = false;
            SafeInvoke(() =>
            {
                NotifyStatus("Reconnecting…", StatusState.Connecting);
                ScheduleReconnect();
            });
        };

        _player.Playing += (_, _) =>
        {
            IsStreaming  = true;
            _retryCount  = 0;
            _player.Mute = Camera.Muted;
            SafeInvoke(() => NotifyStatus("Live", StatusState.Live));
        };

        _player.Buffering += (_, args) =>
        {
            if (args.Cache < 100)
                SafeInvoke(() => NotifyStatus($"Buffering {args.Cache:0}%", StatusState.Connecting));
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void NotifyStatus(string text, StatusState state)
    {
        CurrentStatus = text;
        CurrentState  = state;
        _overlay?.UpdateStatus(text, state);
        StatusChanged?.Invoke(text, state);
    }

    private void SafeInvoke(Action action)
    {
        if (IsHandleCreated && !IsDisposed)
            try { BeginInvoke(action); } catch { }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hoverTimer.Stop();
            _hoverTimer.Dispose();
            StopStream();
            if (_player != null) { _rtspService.Destroy(_player); _player = null; }
            _overlay?.Dispose();
            _videoView?.Dispose();
        }
        base.Dispose(disposing);
    }
}
