namespace FragileDiskCopy;

internal sealed class MultiFolderDialog : Form
{
    private readonly ListBox _folders = new();

    public MultiFolderDialog(IEnumerable<string> initialFolders)
    {
        Text = "选择源文件夹（可多选）";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(760, 440);
        MinimumSize = new Size(620, 360);
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;

        foreach (var folder in initialFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            _folders.Items.Add(folder);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        var addRemove = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        var addButton = new Button { Text = "添加文件夹", Width = 110 };
        addButton.Click += AddFolder;
        var removeButton = new Button { Text = "移除选中", Width = 100 };
        removeButton.Click += RemoveSelected;
        addRemove.Controls.AddRange([addButton, removeButton]);
        layout.Controls.Add(addRemove, 0, 0);

        _folders.Dock = DockStyle.Fill;
        _folders.HorizontalScrollbar = true;
        _folders.IntegralHeight = false;
        layout.Controls.Add(_folders, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var okButton = new Button { Text = "确定", Width = 88 };
        okButton.Click += (_, _) =>
        {
            if (_folders.Items.Count == 0)
            {
                MessageBox.Show(this, "请至少添加一个源文件夹。", "选择源文件夹");
                return;
            }
            DialogResult = DialogResult.OK;
        };
        var cancelButton = new Button { Text = "取消", Width = 88 };
        cancelButton.Click += (_, _) => DialogResult = DialogResult.Cancel;
        buttons.Controls.AddRange([okButton, cancelButton]);
        layout.Controls.Add(buttons, 0, 2);

        Controls.Add(layout);
    }

    public IReadOnlyList<string> SelectedFolders => _folders.Items.Cast<string>().ToArray();

    private void AddFolder(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择要添加的源文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (_folders.SelectedItem is string selected)
            dialog.SelectedPath = selected;
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dialog.SelectedPath));
        if (_folders.Items.Cast<string>().Any(path => string.Equals(
                path,
                folder,
                StringComparison.OrdinalIgnoreCase)))
            return;
        _folders.Items.Add(folder);
        _folders.SelectedItem = folder;
    }

    private void RemoveSelected(object? sender, EventArgs e)
    {
        if (_folders.SelectedIndex < 0)
            return;
        _folders.Items.RemoveAt(_folders.SelectedIndex);
    }
}
