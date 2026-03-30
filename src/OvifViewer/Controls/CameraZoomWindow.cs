using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using OvifViewer.Models;
using OvifViewer.Services;
using Serilog;

namespace OvifViewer.Controls;

/// <summary>
/// Floating, resizable window that shows a live feed of a selected camera with PTZ controls.
/// </summary>
public class CameraZoomWindow : Window
{
    private const int HeaderHeight = 64;
    private const int GripSize = 32;
    private const int BtnSize = 60;
    private const int BtnGap = 8;

    private readonly IRtspStreamService _rtspService;
    private readonly IOnvifService _onvifService;

    private TextBlock _titleLabel = null!;
    private VideoView _videoView = null!;
    private Panel _ptzPanel = null!;

    private MediaPlayer? _player;
    private CameraConfig? _camera;

    // Drag state
    private bool _dragging;
    private Point _dragStart;
    private PixelPoint _windowStartPos;

    public CameraZoomWindow(IRtspStreamService rtspService, IOnvifService onvifService)
    {
        _rtspService = rtspService;
        _onvifService = onvifService;

        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(18, 18, 18));
        MinWidth = 240;
        MinHeight = 200;
        Width = 380;
        Height = 480;
        CanResize = false; // we handle resize manually
        WindowStartupLocation = WindowStartupLocation.Manual;

        BuildUI();
    }

    private void BuildUI()
    {
        // ── Header ────────────────────────────────────────────────────────────
        _titleLabel = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 18,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var closeBtn = new Button
        {
            Content = "✕",
            Width = HeaderHeight, Height = HeaderHeight,
            Padding = new Thickness(0),
            FontSize = 18,
            Background = Brushes.Transparent,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
        };
        closeBtn.Click += (_, _) => Hide();

        var headerInner = new Grid();
        headerInner.Children.Add(_titleLabel);
        headerInner.Children.Add(closeBtn);

        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            Height = HeaderHeight,
            Child = headerInner,
            Cursor = new Cursor(StandardCursorType.SizeAll),
        };
        header.PointerPressed += OnHeaderPointerPressed;
        header.PointerMoved += OnHeaderPointerMoved;
        header.PointerReleased += OnHeaderPointerReleased;
        _titleLabel.PointerPressed += OnHeaderPointerPressed;

        // ── Video view ────────────────────────────────────────────────────────
        _videoView = new VideoView();

        // ── PTZ panel ─────────────────────────────────────────────────────────
        _ptzPanel = BuildPtzPanel();
        _ptzPanel.IsVisible = false;

        // ── Resize grip (bottom-right) ─────────────────────────────────────────
        var grip = new Border
        {
            Width = GripSize, Height = GripSize,
            Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            Cursor = new Cursor(StandardCursorType.BottomRightCorner),
            ZIndex = 10,
        };
        PixelPoint gripDragStart = default;
        PixelSize gripOrigSize = default;
        grip.PointerPressed += (_, e) =>
        {
            gripDragStart = this.PointToScreen(e.GetPosition(this));
            gripOrigSize = new PixelSize((int)Width, (int)Height);
            e.Pointer.Capture(grip);
        };
        grip.PointerMoved += (_, e) =>
        {
            if (e.Pointer.Captured != grip) return;
            var cur = this.PointToScreen(e.GetPosition(this));
            int nw = Math.Max((int)MinWidth, gripOrigSize.Width + cur.X - gripDragStart.X);
            int nh = Math.Max((int)MinHeight, gripOrigSize.Height + cur.Y - gripDragStart.Y);
            Width = nw;
            Height = nh;
        };
        grip.PointerReleased += (_, e) => e.Pointer.Capture(null);

        // ── Layout ────────────────────────────────────────────────────────────
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(HeaderHeight, GridUnitType.Pixel));
        grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        Grid.SetRow(header, 0);
        Grid.SetRow(_videoView, 1);
        Grid.SetRow(_ptzPanel, 2);

        grid.Children.Add(header);
        grid.Children.Add(_videoView);
        grid.Children.Add(_ptzPanel);

        var root = new Panel();
        root.Children.Add(grid);
        root.Children.Add(grip);

        Content = root;
    }

    private Panel BuildPtzPanel()
    {
        var panel = new Panel
        {
            Background = new SolidColorBrush(Color.FromRgb(25, 25, 25)),
            Height = (int)Math.Ceiling((double)(BtnGap + 4 * (BtnSize + BtnGap) + BtnGap)),
        };
        panel.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
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
                Content = d.Label,
                Width = BtnSize, Height = BtnSize,
                Padding = new Thickness(0),
                FontSize = 16,
            };
            Canvas.SetLeft(btn, BtnGap + d.Col * (BtnSize + BtnGap));
            Canvas.SetTop(btn, BtnGap + 1 + d.Row * (BtnSize + BtnGap));

            float p = d.Pan, t = d.Tilt, z = d.Zoom;
            if (d.Label == "■")
                btn.Click += async (_, _) => await SafePtzAsync(() => _onvifService.PtzStopAsync(_camera!));
            else
            {
                btn.AddHandler(PointerPressedEvent, async (object? s, PointerPressedEventArgs e) =>
                    await SafePtzAsync(() => _onvifService.PtzMoveAsync(_camera!, p, t, z)),
                    RoutingStrategies.Tunnel);
                btn.AddHandler(PointerReleasedEvent, async (object? s, PointerReleasedEventArgs e) =>
                    await SafePtzAsync(() => _onvifService.PtzStopAsync(_camera!)),
                    RoutingStrategies.Tunnel);
            }
            panel.Children.Add(btn);
        }
        return panel;
    }

    // ── Header drag ───────────────────────────────────────────────────────────

    private void OnHeaderPointerPressed(object? s, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _windowStartPos = Position;
        e.Pointer.Capture((IInputElement)s!);
    }

    private void OnHeaderPointerMoved(object? s, PointerEventArgs e)
    {
        if (!_dragging || e.Pointer.Captured != s as IInputElement) return;
        var cur = e.GetPosition(this);
        var delta = cur - _dragStart;
        Position = new PixelPoint(
            _windowStartPos.X + (int)delta.X,
            _windowStartPos.Y + (int)delta.Y);
    }

    private void OnHeaderPointerReleased(object? s, PointerReleasedEventArgs e)
    {
        _dragging = false;
        e.Pointer.Capture(null);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task LoadCameraAsync(CameraConfig camera)
    {
        StopCurrent();
        _camera = camera;
        _titleLabel.Text = string.IsNullOrWhiteSpace(camera.DisplayName)
            ? camera.DeviceServiceUrl : camera.DisplayName;

        if (string.IsNullOrEmpty(camera.RtspUri))
        {
            try { camera.RtspUri = await _onvifService.GetStreamUriAsync(camera); }
            catch (Exception ex) { Log.Warning(ex, "Zoom panel: could not fetch RTSP URI for {Name}", camera.DisplayName); }
        }

        if (!string.IsNullOrEmpty(camera.RtspUri) && _camera == camera)
        {
            _player = new MediaPlayer(((RtspStreamService)_rtspService).LibVlc);
            _videoView.MediaPlayer = _player;
            _rtspService.Play(_player, camera.RtspUri, camera.Username, camera.GetPassword());
        }

        bool hasPtz = camera.PtzEnabled;
        if (!hasPtz)
        {
            try { hasPtz = await _onvifService.HasPtzAsync(camera); }
            catch { /* not supported */ }
        }
        if (_camera == camera)
            _ptzPanel.IsVisible = hasPtz;
    }

    public void Clear()
    {
        StopCurrent();
        _camera = null;
        if (IsVisible)
        {
            _titleLabel.Text = "";
            _ptzPanel.IsVisible = false;
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private void StopCurrent()
    {
        if (_player == null) return;
        _rtspService.Destroy(_player);
        _player = null;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        StopCurrent();
    }

    private static async Task SafePtzAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Log.Warning(ex, "PTZ command failed"); }
    }
}
