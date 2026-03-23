using OvifViewer.Models;
using OvifViewer.Services;
using Serilog;

namespace OvifViewer.Forms;

public class DiscoveryForm : Form
{
    private readonly IDiscoveryService _discovery;
    private readonly IOnvifService _onvif;
    private readonly ISettingsService _settings;

    private readonly ListView _list;
    private readonly Button _scanBtn;
    private readonly Button _addBtn;
    private readonly Label _statusLabel;
    private readonly ProgressBar _progress;
    private CancellationTokenSource? _cts;

    public List<CameraConfig> SelectedCameras { get; } = [];

    public DiscoveryForm(IDiscoveryService discovery, IOnvifService onvif, ISettingsService settings)
    {
        _discovery = discovery;
        _onvif = onvif;
        _settings = settings;

        Text = "Discover ONVIF Cameras";
        Size = new Size(700, 480);
        MinimumSize = new Size(600, 400);
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;

        // ── List view ─────────────────────────────────────────────────────────
        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            CheckBoxes = true,
            MultiSelect = true,
        };
        _list.Columns.Add("Name / URL", 280);
        _list.Columns.Add("Manufacturer", 130);
        _list.Columns.Add("Model", 130);
        _list.Columns.Add("Firmware", 100);

        // ── Bottom strip ──────────────────────────────────────────────────────
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 80 };

        _progress = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            Dock = DockStyle.Top,
            Height = 6,
            Visible = false,
        };

        _statusLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Press Scan to discover cameras on your network.",
        };

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 40,
            Padding = new Padding(4),
        };

        _addBtn = new Button { Text = "Add Selected", Width = 110, Enabled = false };
        _scanBtn = new Button { Text = "Scan Network", Width = 110 };
        var cancelBtn = new Button { Text = "Cancel", Width = 80, DialogResult = DialogResult.Cancel };

        _addBtn.Click += OnAddClick;
        _scanBtn.Click += OnScanClick;

        btnPanel.Controls.AddRange([cancelBtn, _addBtn, _scanBtn]);
        bottom.Controls.Add(btnPanel);
        bottom.Controls.Add(_statusLabel);
        bottom.Controls.Add(_progress);

        Controls.Add(_list);
        Controls.Add(bottom);

        AcceptButton = _addBtn;
        CancelButton = cancelBtn;

        _list.ItemChecked += (_, _) =>
            _addBtn.Enabled = _list.CheckedItems.Count > 0;
    }

    // ── Scan ──────────────────────────────────────────────────────────────────

    private async void OnScanClick(object? sender, EventArgs e)
    {
        _list.Items.Clear();
        _scanBtn.Enabled = false;
        _progress.Visible = true;
        _statusLabel.Text = "Scanning…";
        _cts = new CancellationTokenSource();

        var progress = new Progress<DiscoveredCamera>(cam =>
        {
            if (InvokeRequired) { BeginInvoke(() => AddToList(cam)); return; }
            AddToList(cam);
        });

        try
        {
            var cameras = await _discovery.DiscoverAsync(3000, progress, _cts.Token);

            // Attempt to enrich with device info (best effort)
            _statusLabel.Text = $"Found {cameras.Count} device(s). Fetching details…";
            foreach (var cam in cameras)
            {
                // Only enrich if we haven't already via progress
                if (string.IsNullOrEmpty(cam.Manufacturer))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Probe with empty credentials first; may fail — that's OK
                            var info = await _onvif.GetDeviceInfoAsync(
                                cam.DeviceServiceUrl, "", "");
                            cam.Manufacturer = info.Manufacturer;
                            cam.Model = info.Model;
                            cam.FirmwareVersion = info.FirmwareVersion;
                            if (string.IsNullOrEmpty(cam.DisplayName))
                                cam.DisplayName = info.DisplayName;

                            BeginInvoke(() => RefreshListItem(cam));
                        }
                        catch { /* credentials required — skip enrichment */ }
                    });
                }
            }

            _statusLabel.Text = $"Scan complete — {cameras.Count} camera(s) found.";
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Discovery scan failed");
            _statusLabel.Text = $"Scan failed: {ex.Message}";
        }
        finally
        {
            _scanBtn.Enabled = true;
            _progress.Visible = false;
        }
    }

    private void AddToList(DiscoveredCamera cam)
    {
        var item = new ListViewItem(cam.DeviceServiceUrl) { Tag = cam };
        item.SubItems.Add(cam.Manufacturer);
        item.SubItems.Add(cam.Model);
        item.SubItems.Add(cam.FirmwareVersion);
        _list.Items.Add(item);
    }

    private void RefreshListItem(DiscoveredCamera cam)
    {
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is DiscoveredCamera c && c.DeviceServiceUrl == cam.DeviceServiceUrl)
            {
                item.SubItems[1].Text = cam.Manufacturer;
                item.SubItems[2].Text = cam.Model;
                item.SubItems[3].Text = cam.FirmwareVersion;
                break;
            }
        }
    }

    // ── Add selected ──────────────────────────────────────────────────────────

    private void OnAddClick(object? sender, EventArgs e)
    {
        foreach (ListViewItem item in _list.CheckedItems)
        {
            if (item.Tag is not DiscoveredCamera discovered) continue;

            // Check for duplicate
            if (_settings.Settings.Cameras.Any(
                c => c.DeviceServiceUrl.Equals(discovered.DeviceServiceUrl,
                    StringComparison.OrdinalIgnoreCase)))
                continue;

            // Ask for credentials
            using var creds = new CameraSettingsForm(new Models.CameraConfig
            {
                DeviceServiceUrl = discovered.DeviceServiceUrl,
                DisplayName = string.IsNullOrEmpty(discovered.DisplayName)
                    ? discovered.DeviceServiceUrl
                    : discovered.DisplayName,
            }, _onvif);

            if (creds.ShowDialog(this) != DialogResult.OK) continue;

            var cam = creds.Result!;
            _settings.Settings.Cameras.Add(cam);
            SelectedCameras.Add(cam);
        }

        if (SelectedCameras.Count > 0)
        {
            _settings.Save();
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts?.Cancel();
        base.OnFormClosed(e);
    }
}
