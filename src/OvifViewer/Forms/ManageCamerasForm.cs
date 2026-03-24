using OvifViewer.Models;
using OvifViewer.Services;

namespace OvifViewer.Forms;

/// <summary>Lists all configured cameras and allows editing or removing them.</summary>
public class ManageCamerasForm : Form
{
    private readonly ISettingsService _settings;
    private readonly IOnvifService _onvif;
    private readonly ListView _list;

    public ManageCamerasForm(ISettingsService settings, IOnvifService onvif)
    {
        _settings = settings;
        _onvif = onvif;

        Text = "Manage Cameras";
        Size = new Size(620, 420);
        MinimumSize = new Size(500, 360);
        StartPosition = FormStartPosition.CenterParent;

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
        };
        _list.Columns.Add("Name", 200);
        _list.Columns.Add("URL", 260);
        _list.Columns.Add("Auto-Connect", 90);

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(4),
        };

        var editBtn = new Button { Text = "Edit…", Width = 80 };
        var removeBtn = new Button { Text = "Remove", Width = 80 };
        var closeBtn = new Button { Text = "Close", Width = 80, DialogResult = DialogResult.Cancel };

        editBtn.Click += OnEditClick;
        removeBtn.Click += OnRemoveClick;

        btnPanel.Controls.AddRange([closeBtn, removeBtn, editBtn]);

        Controls.Add(_list);
        Controls.Add(btnPanel);
        CancelButton = closeBtn;

        RefreshList();
    }

    private void RefreshList()
    {
        _list.Items.Clear();
        foreach (var cam in _settings.Settings.Cameras)
        {
            var item = new ListViewItem(cam.DisplayName) { Tag = cam };
            item.SubItems.Add(cam.DeviceServiceUrl);
            item.SubItems.Add(cam.AutoConnect ? "Yes" : "No");
            _list.Items.Add(item);
        }
    }

    private void OnEditClick(object? sender, EventArgs e)
    {
        if (_list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is not CameraConfig cam) return;

        using var form = new CameraSettingsForm(cam, _onvif);
        if (form.ShowDialog(this) != DialogResult.OK || form.Result == null) return;

        // Mutate in-place so live CameraPanel references stay valid
        var r = form.Result;
        cam.DisplayName          = r.DisplayName;
        cam.Username             = r.Username;
        cam.EncryptedPassword    = r.EncryptedPassword;
        cam.SelectedProfileToken = r.SelectedProfileToken;
        cam.PtzEnabled           = r.PtzEnabled;
        cam.AutoConnect          = r.AutoConnect;
        cam.RtspUri              = string.Empty;   // clear cache

        _settings.Save();
        RefreshList();
    }

    private void OnRemoveClick(object? sender, EventArgs e)
    {
        if (_list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is not CameraConfig cam) return;

        var confirm = MessageBox.Show(
            $"Remove \"{cam.DisplayName}\"?",
            "Confirm Remove",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes) return;

        _settings.Settings.Cameras.Remove(cam);
        _settings.Settings.PanelLayouts.RemoveAll(l => l.CameraId == cam.Id);
        _settings.Save();
        RefreshList();
    }
}
