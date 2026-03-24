using OvifViewer.Models;
using OvifViewer.Services;

namespace OvifViewer.Forms;

/// <summary>
/// Configure credentials, stream profile, and PTZ for a single camera.
/// </summary>
public class CameraSettingsForm : Form
{
    private readonly IOnvifService _onvif;
    private readonly TextBox _nameBox;
    private readonly TextBox _userBox;
    private readonly TextBox _passBox;
    private readonly ComboBox _profileBox;
    private readonly CheckBox _ptzCheck;
    private readonly CheckBox _autoConnectCheck;
    private readonly Button _testBtn;
    private readonly Label _testLabel;
    private CameraConfig _camera;

    public CameraConfig? Result { get; private set; }

    public CameraSettingsForm(CameraConfig camera, IOnvifService onvif)
    {
        _camera = camera;
        _onvif = onvif;

        Text = $"Camera Settings — {camera.DisplayName}";
        Size = new Size(480, 370);
        MinimumSize = new Size(380, 320);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            RowCount = 8,
            ColumnCount = 2,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _nameBox = new TextBox { Dock = DockStyle.Fill, Text = camera.DisplayName };
        _userBox = new TextBox { Dock = DockStyle.Fill, Text = camera.Username };
        _passBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };

        if (!string.IsNullOrEmpty(camera.EncryptedPassword))
            _passBox.Text = camera.GetPassword();

        _profileBox = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };

        _ptzCheck = new CheckBox
        {
            Text = "Enable PTZ controls",
            Dock = DockStyle.Fill,
            Checked = camera.PtzEnabled,
        };

        _autoConnectCheck = new CheckBox
        {
            Text = "Auto-connect on startup",
            Dock = DockStyle.Fill,
            Checked = camera.AutoConnect,
        };

        _testBtn = new Button { Text = "Test & Load Profiles", Dock = DockStyle.Fill };
        _testLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Enter credentials then click Test.",
        };

        _testBtn.Click += OnTestClick;

        int row = 0;
        void Row(string label, Control ctrl)
        {
            table.Controls.Add(new Label
            {
                Text = label,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill
            }, 0, row);
            table.Controls.Add(ctrl, 1, row);
            row++;
        }

        Row("Display Name:", _nameBox);
        Row("URL:", new Label { Text = camera.DeviceServiceUrl, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
        Row("Username:", _userBox);
        Row("Password:", _passBox);
        Row("Stream Profile:", _profileBox);
        table.Controls.Add(_ptzCheck, 1, row++);
        table.Controls.Add(_autoConnectCheck, 1, row++);

        var testRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        testRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        testRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        testRow.Controls.Add(_testBtn, 0, 0);
        testRow.Controls.Add(_testLabel, 1, 0);
        table.Controls.Add(testRow, 0, row);
        table.SetColumnSpan(testRow, 2);
        row++;

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
        };
        var ok = new Button { Text = "Save", Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
        ok.Click += OnSaveClick;
        btnPanel.Controls.AddRange([cancel, ok]);
        table.Controls.Add(btnPanel, 0, row);
        table.SetColumnSpan(btnPanel, 2);

        Controls.Add(table);
        CancelButton = cancel;


    }

    private async void OnTestClick(object? sender, EventArgs e)
    {
        _testBtn.Enabled = false;
        _testLabel.Text = "Connecting…";
        _profileBox.Items.Clear();

        var temp = BuildTempConfig();

        try
        {
            var profiles = await _onvif.GetProfilesAsync(temp);
            foreach (var p in profiles)
                _profileBox.Items.Add(p);

            // Re-select previously chosen profile
            if (!string.IsNullOrEmpty(_camera.SelectedProfileToken))
            {
                for (int i = 0; i < _profileBox.Items.Count; i++)
                {
                    if (_profileBox.Items[i] is CameraProfile cp &&
                        cp.Token == _camera.SelectedProfileToken)
                    {
                        _profileBox.SelectedIndex = i;
                        break;
                    }
                }
            }

            if (_profileBox.SelectedIndex < 0 && _profileBox.Items.Count > 0)
                _profileBox.SelectedIndex = 0;

            bool hasPtz = await _onvif.HasPtzAsync(temp);
            _ptzCheck.Enabled = hasPtz;
            if (!hasPtz) _ptzCheck.Checked = false;

            _testLabel.ForeColor = Color.Green;
            _testLabel.Text = $"OK — {profiles.Count} profile(s) found.";
        }
        catch (Exception ex)
        {
            _testLabel.ForeColor = Color.Red;
            // Show type + message; InnerException often has the real cause
            var inner = ex.InnerException?.Message ?? string.Empty;
            var detail = string.IsNullOrEmpty(inner) ? ex.Message : $"{ex.Message} → {inner}";
            _testLabel.Text = $"Failed: {detail}";
            _testLabel.AutoSize = true;
        }
        finally
        {
            _testBtn.Enabled = true;
        }
    }

    private void OnSaveClick(object? sender, EventArgs e)
    {
        var profile = _profileBox.SelectedItem as CameraProfile;
        if (profile == null && _profileBox.Items.Count > 0)
        {
            MessageBox.Show("Please test the connection and select a stream profile.",
                "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var cam = BuildTempConfig();
        cam.SelectedProfileToken = profile?.Token ?? _camera.SelectedProfileToken;
        cam.PtzEnabled = _ptzCheck.Checked;
        cam.AutoConnect = _autoConnectCheck.Checked;
        cam.Id = _camera.Id;

        Result = cam;
        DialogResult = DialogResult.OK;
        Close();
    }

    private CameraConfig BuildTempConfig()
    {
        var c = new CameraConfig
        {
            DeviceServiceUrl = _camera.DeviceServiceUrl,
            DisplayName = _nameBox.Text.Trim(),
            Username = _userBox.Text.Trim(),
        };
        c.SetPassword(_passBox.Text);
        return c;
    }
}
