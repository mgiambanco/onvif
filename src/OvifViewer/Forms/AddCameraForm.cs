using OvifViewer.Models;

namespace OvifViewer.Forms;

/// <summary>Simple form to manually enter a camera IP/hostname.</summary>
public class AddCameraForm : Form
{
    private readonly TextBox _urlBox;
    private readonly TextBox _nameBox;
    public CameraConfig? Result { get; private set; }

    public AddCameraForm()
    {
        Text = "Add Camera Manually";
        Size = new Size(440, 200);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            RowCount = 3,
            ColumnCount = 2,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _nameBox = new TextBox { Dock = DockStyle.Fill };
        _urlBox = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "http://192.168.1.100/onvif/device_service",
        };

        table.Controls.Add(new Label { Text = "Display Name:", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 0);
        table.Controls.Add(_nameBox, 1, 0);
        table.Controls.Add(new Label { Text = "Device URL:", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 1);
        table.Controls.Add(_urlBox, 1, 1);

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
        };
        var ok = new Button { Text = "Next →", DialogResult = DialogResult.None, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };

        ok.Click += OnOkClick;
        btnPanel.Controls.AddRange([cancel, ok]);
        table.Controls.Add(btnPanel, 1, 2);

        Controls.Add(table);
        AcceptButton = ok;
        CancelButton = cancel;


    }

    private void OnOkClick(object? sender, EventArgs e)
    {
        var url = _urlBox.Text.Trim();
        if (string.IsNullOrEmpty(url))
        {
            MessageBox.Show("Please enter the ONVIF device service URL.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Result = new CameraConfig
        {
            DeviceServiceUrl = url,
            DisplayName = string.IsNullOrEmpty(_nameBox.Text.Trim()) ? url : _nameBox.Text.Trim(),
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}
