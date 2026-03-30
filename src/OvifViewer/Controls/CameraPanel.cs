using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using OvifViewer.Models;
using OvifViewer.Services;
using Serilog;

namespace OvifViewer.Controls;

public enum StatusState { Connecting, Live, Idle, Error }

[Flags]
public enum ResizeEdge { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

/// <summary>
/// A camera panel that lives absolutely-positioned on a Canvas in MainWindow.
/// Layout: title bar (30 px, top) | VideoView (fill) | PTZ panel (bottom, optional).
/// Resize handles are placed in the OverlayLayer so they appear above the native video HWND.
/// </summary>
public class CameraPanel : UserControl
{
    private const int TitleBarHeight = 60;
    private const int HandleEdge = 10;
    private const int HandleCorner = 20;

    private readonly IRtspStreamService _rtspService;
    private readonly IOnvifService _onvifService;

    // Video
    private VideoView? _videoView;
    private MediaPlayer? _player;
    private CancellationTokenSource? _streamCts;
    private int _retryCount;

    // Title bar refs
    private TextBlock _titleLabel = null!;
    private TextBlock _statusLabel = null!;
    private Border _statusDot = null!;
    private Button _audioBtn = null!;

    // PTZ
    private Panel _ptzPanel = null!;

    // Overlay resize handles (in OverlayLayer)
    private OverlayLayer? _overlayLayer;
    private readonly Border[] _handles = new Border[5]; // L, R, B, BL, BR

    // Drag state
    private bool _dragging;
    private Point _dragOffset;

    // Resize state
    private bool _resizing;
    private Point _resizeStartPos;
    private Rect _resizeOrigBounds;
    private ResizeEdge _resizeEdge;

    // Profiles
    private List<CameraProfile> _profiles = [];
    private bool _profilesLoaded;

    // ── Events ────────────────────────────────────────────────────────────────

    public event EventHandler? PanelRemoveRequested;
    public event EventHandler? ToggleFullSizeRequested;
    public event EventHandler? ZoomRequested;
    public event Action<string, StatusState>? StatusChanged;
    public event EventHandler? LayoutChanged;
    public event Action<string>? ProfileChangeRequested;
    public event EventHandler? MuteChanged;

    // ── Properties ────────────────────────────────────────────────────────────

    public CameraConfig Camera { get; }
    public bool IsStreaming { get; private set; }
    public bool IsMuted => _player?.Mute ?? Camera.Muted;
    public int Volume => _player?.Volume ?? 100;

    // ── Constructor ───────────────────────────────────────────────────────────

    public CameraPanel(CameraConfig camera, IRtspStreamService rtspService, IOnvifService onvifService)
    {
        Camera = camera;
        _rtspService = rtspService;
        _onvifService = onvifService;
        ClipToBounds = true;
        Background = new SolidColorBrush(Color.FromRgb(18, 18, 18));
        BuildContent();
    }

    // ── Content ───────────────────────────────────────────────────────────────

    private void BuildContent()
    {
        _videoView = new VideoView();

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(TitleBarHeight, GridUnitType.Pixel));
        grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var titleBar = BuildTitleBar();
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(_videoView, 1);

        _ptzPanel = BuildPtzPanel();
        _ptzPanel.IsVisible = Camera.PtzEnabled;
        Grid.SetRow(_ptzPanel, 2);

        grid.Children.Add(titleBar);
        grid.Children.Add(_videoView);
        grid.Children.Add(_ptzPanel);

        Content = grid;
    }

    private Border BuildTitleBar()
    {
        _titleLabel = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(Camera.DisplayName)
                ? Camera.DeviceServiceUrl : Camera.DisplayName,
            Foreground = Brushes.White,
            FontSize = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        _statusDot = new Border
        {
            Width = 16, Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Colors.Orange),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0),
        };
        _statusLabel = new TextBlock
        {
            Text = "Connecting…",
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };

        var closeBtn = MakeIconBtn("✕", Color.FromRgb(196, 43, 28));
        closeBtn.Click += (_, _) => RequestRemove();

        var actionsBtn = MakeIconBtn("⋮");
        actionsBtn.Click += (_, _) => ShowActionsMenu(actionsBtn);

        var streamBtn = MakeIconBtn("▤");
        streamBtn.Click += (_, _) =>
        {
            if (!_profilesLoaded) { _profilesLoaded = true; _ = LoadProfilesAsync(); }
            ShowStreamMenu(streamBtn);
        };

        _audioBtn = MakeIconBtn("🔊");
        _audioBtn.Click += (_, _) => ShowAudioMenu(_audioBtn);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 0,
        };
        right.Children.Add(_statusLabel);
        right.Children.Add(_statusDot);
        right.Children.Add(_audioBtn);
        right.Children.Add(streamBtn);
        right.Children.Add(actionsBtn);
        right.Children.Add(closeBtn);

        var inner = new Grid();
        inner.Children.Add(_titleLabel);
        inner.Children.Add(right);

        var bar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(240, 22, 22, 22)),
            Child = inner,
            Cursor = new Cursor(StandardCursorType.SizeAll),
        };
        bar.PointerPressed += OnTitleBarPointerPressed;
        bar.PointerMoved += OnTitleBarPointerMoved;
        bar.PointerReleased += OnTitleBarPointerReleased;
        bar.DoubleTapped += (_, _) => ToggleFullSizeRequested?.Invoke(this, EventArgs.Empty);
        return bar;
    }

    private Panel BuildPtzPanel()
    {
        var panel = new Panel
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 20, 20, 20)),
            Height = 148,
        };

        // Separator line at top
        panel.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        });

        (string label, float pan, float tilt, float zoom)[] defs =
        [
            ("▲",  0f,   0.5f, 0f),
            ("◄", -0.5f, 0f,   0f),
            ("■",  0f,   0f,   0f),
            ("►",  0.5f, 0f,   0f),
            ("▼",  0f,  -0.5f, 0f),
        ];

        int col = 0, row = 0;
        const int bw = 52, bh = 52, gap = 4, padX = 8, padY = 8;
        foreach (var (text, pan, tilt, zoom) in defs)
        {
            var btn = new Button
            {
                Content = text,
                Width = bw, Height = bh,
                Padding = new Thickness(0),
                FontSize = 16,
            };
            btn.SetValue(Canvas.LeftProperty, (double)(padX + col * (bw + gap)));
            btn.SetValue(Canvas.TopProperty, (double)(padY + row * (bh + gap)));

            float p = pan, t = tilt, z = zoom;
            if (text == "■")
                btn.Click += async (_, _) => await SafePtzAsync(() => _onvifService.PtzStopAsync(Camera));
            else
            {
                btn.AddHandler(PointerPressedEvent, async (object? s, PointerPressedEventArgs e) =>
                    await SafePtzAsync(() => _onvifService.PtzMoveAsync(Camera, p, t, z)),
                    RoutingStrategies.Tunnel);
                btn.AddHandler(PointerReleasedEvent, async (object? s, PointerReleasedEventArgs e) =>
                    await SafePtzAsync(() => _onvifService.PtzStopAsync(Camera)),
                    RoutingStrategies.Tunnel);
            }

            panel.Children.Add(btn);
            col++;
            if (col > 2) { col = 0; row++; }
        }

        return panel;
    }

    // ── Overlay layer (resize handles) ────────────────────────────────────────

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _overlayLayer = OverlayLayer.GetOverlayLayer(this);
        if (_overlayLayer == null) return;

        (ResizeEdge Edge, StandardCursorType Cursor)[] specs =
        [
            (ResizeEdge.Left,                        StandardCursorType.SizeWestEast),
            (ResizeEdge.Right,                       StandardCursorType.SizeWestEast),
            (ResizeEdge.Bottom,                      StandardCursorType.SizeNorthSouth),
            (ResizeEdge.Left  | ResizeEdge.Bottom,   StandardCursorType.BottomLeftCorner),
            (ResizeEdge.Right | ResizeEdge.Bottom,   StandardCursorType.BottomRightCorner),
        ];

        for (int i = 0; i < specs.Length; i++)
        {
            var (edge, cursor) = specs[i];
            var handle = new Border
            {
                Background = Brushes.Transparent,
                Cursor = new Cursor(cursor),
                ZIndex = 9999,
            };
            SetupResizeHandle(handle, edge);
            _overlayLayer.Children.Add(handle);
            _handles[i] = handle;
        }

        // Sync positions whenever layout changes (see OnPropertyChanged override)
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_overlayLayer == null) return;
        foreach (var h in _handles)
            if (h != null) _overlayLayer.Children.Remove(h);
        _overlayLayer = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
            Dispatcher.UIThread.Post(SyncHandles, DispatcherPriority.Background);
    }

    private void SyncHandles()
    {
        if (_overlayLayer == null) return;
        if (this.TransformToVisual(_overlayLayer) is not Matrix mat) return;

        var tl = mat.Transform(new Point(0, 0));
        double w = Bounds.Width, h = Bounds.Height;
        int E = HandleEdge, C = HandleCorner, T = TitleBarHeight;

        void Place(int i, double x, double y, double bw, double bh)
        {
            if (_handles[i] == null) return;
            Canvas.SetLeft(_handles[i], tl.X + x);
            Canvas.SetTop(_handles[i], tl.Y + y);
            _handles[i].Width = bw;
            _handles[i].Height = bh;
        }

        Place(0, 0,         T,     E, Math.Max(0, h - T - C));   // Left
        Place(1, w - E,     T,     E, Math.Max(0, h - T - C));   // Right
        Place(2, C,         h - E, Math.Max(0, w - C * 2), E);   // Bottom
        Place(3, 0,         h - C, C, C);                        // Bottom-left
        Place(4, w - C,     h - C, C, C);                        // Bottom-right
    }

    private void SetupResizeHandle(Border handle, ResizeEdge edge)
    {
        handle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            _resizing = true;
            _resizeEdge = edge;
            _resizeStartPos = e.GetPosition(_overlayLayer);
            _resizeOrigBounds = new Rect(
                Canvas.GetLeft(this), Canvas.GetTop(this),
                Bounds.Width, Bounds.Height);
            e.Pointer.Capture(handle);
            e.Handled = true;
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!_resizing || e.Pointer.Captured != handle) return;
            var cur = e.GetPosition(_overlayLayer);
            double dx = cur.X - _resizeStartPos.X;
            double dy = cur.Y - _resizeStartPos.Y;
            ApplyResize(dx, dy);
            e.Handled = true;
        };
        handle.PointerReleased += (_, e) =>
        {
            if (!_resizing) return;
            _resizing = false;
            e.Pointer.Capture(null);
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    private void ApplyResize(double dx, double dy)
    {
        const double MinW = 160, MinH = 120;
        var b = _resizeOrigBounds;

        if (_resizeEdge.HasFlag(ResizeEdge.Right))  b = b.WithWidth(Math.Max(MinW, b.Width + dx));
        if (_resizeEdge.HasFlag(ResizeEdge.Bottom)) b = b.WithHeight(Math.Max(MinH, b.Height + dy));
        if (_resizeEdge.HasFlag(ResizeEdge.Left))
        {
            double nw = Math.Max(MinW, b.Width - dx);
            b = new Rect(b.Right - nw, b.Y, nw, b.Height);
        }
        if (_resizeEdge.HasFlag(ResizeEdge.Top))
        {
            double nh = Math.Max(MinH, b.Height - dy);
            b = new Rect(b.X, b.Bottom - nh, b.Width, nh);
        }

        Canvas.SetLeft(this, b.X);
        Canvas.SetTop(this, b.Y);
        Width = b.Width;
        Height = b.Height;
        SyncHandles();
    }

    // ── Title-bar drag ────────────────────────────────────────────────────────

    private void OnTitleBarPointerPressed(object? s, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _dragOffset = e.GetPosition(this);
        e.Pointer.Capture((IInputElement)s!);
        e.Handled = true;
    }

    private void OnTitleBarPointerMoved(object? s, PointerEventArgs e)
    {
        if (!_dragging || e.Pointer.Captured != s as IInputElement) return;
        if (Parent is not Canvas canvas) return;

        var pos = e.GetPosition(canvas);
        double nx = Math.Max(0, Math.Min(canvas.Bounds.Width - Width, pos.X - _dragOffset.X));
        double ny = Math.Max(0, Math.Min(canvas.Bounds.Height - Height, pos.Y - _dragOffset.Y));
        Canvas.SetLeft(this, nx);
        Canvas.SetTop(this, ny);
        SyncHandles();
        e.Handled = true;
    }

    private void OnTitleBarPointerReleased(object? s, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Streaming ─────────────────────────────────────────────────────────────

    public void StartStream()
    {
        if (string.IsNullOrEmpty(Camera.RtspUri))
        {
            NotifyStatus("No URL", StatusState.Error);
            return;
        }
        if (_videoView == null) return;

        _streamCts?.Cancel();
        _streamCts = new CancellationTokenSource();
        _retryCount = 0;

        _player = new MediaPlayer(((RtspStreamService)_rtspService).LibVlc);
        _player.Mute = Camera.Muted;
        _videoView.MediaPlayer = _player;

        WirePlayerEvents();
        ConnectOnce();
    }

    public void StopStream()
    {
        _streamCts?.Cancel();
        IsStreaming = false;
        if (_player != null) _rtspService.Stop(_player);
        NotifyStatus("Stopped", StatusState.Idle);
    }

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
            Dispatcher.UIThread.Post(ConnectOnce);
        });
    }

    private void WirePlayerEvents()
    {
        if (_player == null) return;
        _player.EncounteredError += (_, _) =>
        {
            IsStreaming = false;
            Dispatcher.UIThread.Post(() =>
            {
                NotifyStatus("Error", StatusState.Error);
                ScheduleReconnect();
            });
        };
        _player.EndReached += (_, _) =>
        {
            IsStreaming = false;
            Dispatcher.UIThread.Post(() =>
            {
                NotifyStatus("Reconnecting…", StatusState.Connecting);
                ScheduleReconnect();
            });
        };
        _player.Playing += (_, _) =>
        {
            IsStreaming = true;
            _retryCount = 0;
            _player.Mute = Camera.Muted;
            Dispatcher.UIThread.Post(() => NotifyStatus("Live", StatusState.Live));
        };
        _player.Buffering += (_, args) =>
        {
            if (args.Cache < 100)
                Dispatcher.UIThread.Post(() => NotifyStatus($"Buffering {args.Cache:0}%", StatusState.Connecting));
        };
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void RequestRemove() => PanelRemoveRequested?.Invoke(this, EventArgs.Empty);
    public void RequestZoom() => ZoomRequested?.Invoke(this, EventArgs.Empty);
    public void RequestProfileChange(string token) => ProfileChangeRequested?.Invoke(token);

    public void ToggleMute()
    {
        if (_player == null) return;
        _player.Mute = !_player.Mute;
        Camera.Muted = _player.Mute;
        RefreshAudioBtn();
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
        RefreshAudioBtn();
    }

    private void RefreshAudioBtn()
    {
        if (_audioBtn != null)
            _audioBtn.Content = (_player?.Mute ?? Camera.Muted) ? "🔇" : "🔊";
    }

    public void UpdateTitle(string name)
    {
        if (_titleLabel != null) _titleLabel.Text = name;
    }

    // ── Status notification ───────────────────────────────────────────────────

    private void NotifyStatus(string text, StatusState state)
    {
        if (_statusLabel != null) _statusLabel.Text = text;
        if (_statusDot != null)
        {
            _statusDot.Background = state switch
            {
                StatusState.Live       => new SolidColorBrush(Color.FromRgb(50, 205, 50)),
                StatusState.Connecting => new SolidColorBrush(Colors.Orange),
                StatusState.Error      => new SolidColorBrush(Color.FromRgb(220, 50, 50)),
                _                      => new SolidColorBrush(Color.FromRgb(100, 100, 100)),
            };
        }
        StatusChanged?.Invoke(text, state);
    }

    // ── Menus ─────────────────────────────────────────────────────────────────

    private void ShowActionsMenu(Control anchor)
    {
        var menu = new ContextMenu();
        menu.ItemsSource = new object[]
        {
            new MenuItem { Header = "Reconnect",         Command = new RelayCommand(() => { StopStream(); StartStream(); }) },
            new MenuItem { Header = "Show in Zoom Panel",Command = new RelayCommand(() => ZoomRequested?.Invoke(this, EventArgs.Empty)) },
            new Separator(),
            new MenuItem { Header = "Remove Camera",     Command = new RelayCommand(RequestRemove) },
        };
        anchor.ContextMenu = menu;
        menu.Open(anchor);
    }

    private void ShowStreamMenu(Control anchor)
    {
        var menu = new ContextMenu();
        if (_profiles.Count == 0)
        {
            menu.ItemsSource = new[] { new MenuItem { Header = _profilesLoaded ? "No profiles found" : "Loading…", IsEnabled = false } };
        }
        else
        {
            var items = new List<MenuItem>();
            foreach (var p in _profiles)
            {
                var label = string.IsNullOrEmpty(p.Name) ? p.Token : p.Name;
                if (p.Width > 0 && p.Height > 0) label += $"  {p.Width}×{p.Height}";
                if (!string.IsNullOrEmpty(p.Encoding)) label += $"  {p.Encoding}";
                var token = p.Token;
                items.Add(new MenuItem
                {
                    Header = label,
                    Icon = p.Token == Camera.SelectedProfileToken ? new TextBlock { Text = "✓" } : null,
                    Command = new RelayCommand(() => RequestProfileChange(token)),
                });
            }
            menu.ItemsSource = items;
        }
        anchor.ContextMenu = menu;
        menu.Open(anchor);
    }

    private void ShowAudioMenu(Control anchor)
    {
        var muteItem = new MenuItem
        {
            Header = (_player?.Mute ?? Camera.Muted) ? "Unmute" : "Mute",
            Command = new RelayCommand(ToggleMute),
        };
        var menu = new ContextMenu { ItemsSource = new[] { muteItem } };
        anchor.ContextMenu = menu;
        menu.Open(anchor);
    }

    // ── Profiles ──────────────────────────────────────────────────────────────

    private async Task LoadProfilesAsync()
    {
        try
        {
            _profiles = (await _onvifService.GetProfilesAsync(Camera)).ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load profiles for {Name}", Camera.DisplayName);
        }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        _streamCts?.Cancel();
        if (_player != null)
        {
            _rtspService.Destroy(_player);
            _player = null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Button MakeIconBtn(string text, Color? hoverBg = null)
    {
        var btn = new Button
        {
            Content = text,
            Width = TitleBarHeight,
            Height = TitleBarHeight,
            Padding = new Thickness(0),
            FontSize = 18,
            Background = Brushes.Transparent,
        };
        return btn;
    }

    private static async Task SafePtzAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Log.Warning(ex, "PTZ command failed"); }
    }
}

/// <summary>Minimal ICommand implementation for menus.</summary>
file sealed class RelayCommand(Action execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => execute();
}
