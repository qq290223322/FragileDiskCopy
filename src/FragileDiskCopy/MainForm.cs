using System.Diagnostics;
using System.Globalization;

namespace FragileDiskCopy;

internal sealed class MainForm : Form
{
    private readonly TextBox _sourceBox = new();
    private readonly TextBox _targetBox = new();
    private readonly Button _sourceBrowseButton = new();
    private readonly Button _targetBrowseButton = new();
    private readonly NumericUpDown _speedBox = new();
    private readonly NumericUpDown _maxSpeedBox = new();
    private readonly NumericUpDown _growthIntervalBox = new();
    private readonly CheckBox _dynamicSpeedCheckBox = new();
    private readonly Button _startButton = new();
    private readonly Button _scanButton = new();
    private readonly Button _pauseButton = new();
    private readonly Button _openTargetButton = new();
    private readonly Button _openManifestButton = new();
    private readonly Button _continueTaskButton = new();
    private readonly CheckBox _followFileCheckBox = new();
    private readonly ComboBox _fileTypeFilterBox = new();
    private readonly Label _statusLabel = new();
    private readonly Label _overallLabel = new();
    private readonly Label _fileLabel = new();
    private readonly Label _speedMetricLabel = new();
    private readonly Label _timeMetricLabel = new();
    private readonly Label _dropMetricValueLabel = new();
    private readonly Label _stallMetricValueLabel = new();
    private readonly FlowLayoutPanel _metricsPanel = new();
    private readonly PercentageProgressBar _overallProgressBar = new();
    private readonly PercentageProgressBar _fileProgressBar = new();
    private readonly VirtualFileListView _fileList = new();
    private readonly TextBox _logBox = new();
    private readonly List<string> _sourcePaths = [];

    private CopyEngine? _engine;
    private CancellationTokenSource? _cancellation;
    private Task<CopyReport>? _runningTask;
    private bool _closeAfterPause;
    private string? _lastLoggedError;
    private string? _lastResumePath;
    private string? _automaticPauseReason;
    private long _observedProcessedBytes = -1;
    private bool _taskStarted;

    public MainForm()
    {
        Text = "脆盘安全拷贝";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1000, 560);
        MaximumSize = new Size(1000, 1200);
        Size = new Size(1000, 720);
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildLayout();
        _fileList.SelectionChanged += FileSelectionChanged;
        LoadSettings();
        FormClosing += HandleFormClosing;
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 1,
            RowCount = 10
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));

        ConfigureBrowseButton(_sourceBrowseButton, "选择 A", BrowseSource);
        ConfigureBrowseButton(_targetBrowseButton, "选择 B", BrowseTarget);
        _sourceBox.ReadOnly = true;
        _sourceBox.BackColor = Color.White;
        layout.Controls.Add(
            CreatePathRow("源（多选）", _sourceBox, _sourceBrowseButton),
            0,
            0);
        layout.Controls.Add(
            CreatePathRow("目标 B", _targetBox, _targetBrowseButton),
            0,
            1);

        var settings = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 3, 0, 0)
        };
        settings.Controls.Add(new Label
        {
            Text = "限速",
            AutoSize = true,
            Margin = new Padding(0, 6, 8, 0)
        });
        _speedBox.Minimum = 0.1M;
        _speedBox.Maximum = 20M;
        _speedBox.DecimalPlaces = 1;
        _speedBox.Increment = 0.1M;
        _speedBox.Value = 1M;
        _speedBox.Width = 90;
        _speedBox.TextAlign = HorizontalAlignment.Right;
        settings.Controls.Add(_speedBox);
        settings.Controls.Add(new Label
        {
            Text = "MiB/s（基础）",
            AutoSize = true,
            Margin = new Padding(8, 6, 0, 0)
        });
        _dynamicSpeedCheckBox.Text = "动态调速";
        _dynamicSpeedCheckBox.AutoSize = true;
        _dynamicSpeedCheckBox.Margin = new Padding(18, 6, 8, 0);
        _dynamicSpeedCheckBox.CheckedChanged += DynamicSpeedChanged;
        settings.Controls.Add(_dynamicSpeedCheckBox);
        settings.Controls.Add(new Label
        {
            Text = "最高",
            AutoSize = true,
            Margin = new Padding(0, 6, 5, 0)
        });
        _maxSpeedBox.Minimum = 0.1M;
        _maxSpeedBox.Maximum = 1_000_000M;
        _maxSpeedBox.DecimalPlaces = 1;
        _maxSpeedBox.Increment = 0.1M;
        _maxSpeedBox.Value = 2M;
        _maxSpeedBox.Width = 75;
        _maxSpeedBox.TextAlign = HorizontalAlignment.Right;
        _maxSpeedBox.Enabled = false;
        _speedBox.ValueChanged += SpeedBoxValueChanged;
        settings.Controls.Add(_maxSpeedBox);
        settings.Controls.Add(new Label
        {
            Text = "MiB/s",
            AutoSize = true,
            Margin = new Padding(5, 6, 0, 0)
        });
        settings.Controls.Add(new Label
        {
            Text = "间隔",
            AutoSize = true,
            Margin = new Padding(14, 6, 5, 0)
        });
        _growthIntervalBox.Minimum = 10;
        _growthIntervalBox.Maximum = 3600;
        _growthIntervalBox.Increment = 10;
        _growthIntervalBox.Value = 10;
        _growthIntervalBox.Width = 65;
        _growthIntervalBox.TextAlign = HorizontalAlignment.Right;
        _growthIntervalBox.Enabled = false;
        settings.Controls.Add(_growthIntervalBox);
        settings.Controls.Add(new Label
        {
            Text = "秒",
            AutoSize = true,
            Margin = new Padding(5, 6, 0, 0)
        });
        layout.Controls.Add(settings, 0, 2);

        _statusLabel.Text = "首次运行会先扫描并生成清单。";
        _statusLabel.AutoEllipsis = true;
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(_statusLabel, 0, 3);

        layout.Controls.Add(
            CreateProgressRow(_overallLabel, _overallProgressBar, "总进度"),
            0,
            4);
        layout.Controls.Add(
            CreateProgressRow(_fileLabel, _fileProgressBar, "当前文件"),
            0,
            5);
        _overallProgressBar.Dock = DockStyle.Fill;
        _fileProgressBar.Dock = DockStyle.Fill;

        ConfigureMetricLabels();
        _metricsPanel.Dock = DockStyle.Fill;
        _metricsPanel.FlowDirection = FlowDirection.LeftToRight;
        _metricsPanel.WrapContents = false;
        _metricsPanel.Padding = Padding.Empty;
        _metricsPanel.Controls.AddRange(
            [
                _speedMetricLabel,
                _timeMetricLabel,
                CreateMetricCaption("掉盘"),
                _dropMetricValueLabel,
                CreateMetricCaption("卡住"),
                _stallMetricValueLabel
            ]);
        layout.Controls.Add(_metricsPanel, 0, 6);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0)
        };
        _startButton.Text = "开始 / 继续";
        _startButton.Width = 112;
        _startButton.Click += StartClicked;
        _scanButton.Text = "扫描文件";
        _scanButton.Width = 88;
        _scanButton.Click += ScanClicked;
        _continueTaskButton.Text = "继续任务（TSV）";
        _continueTaskButton.Width = 132;
        _continueTaskButton.Click += ContinueTaskClicked;
        _pauseButton.Text = "暂停";
        _pauseButton.Width = 78;
        _pauseButton.Enabled = false;
        _pauseButton.Click += PauseClicked;
        _openTargetButton.Text = "打开目标目录";
        _openTargetButton.Width = 112;
        _openTargetButton.Click += OpenTargetClicked;
        _openManifestButton.Text = "打开清单";
        _openManifestButton.Width = 90;
        _openManifestButton.Click += OpenManifestClicked;
        _followFileCheckBox.Text = "跟随文件";
        _followFileCheckBox.AutoSize = true;
        _followFileCheckBox.Checked = true;
        _followFileCheckBox.Margin = new Padding(8, 7, 0, 0);
        _followFileCheckBox.CheckedChanged += FollowFileCheckedChanged;
        _fileTypeFilterBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _fileTypeFilterBox.Width = 132;
        _fileTypeFilterBox.Margin = new Padding(8, 5, 0, 0);
        _fileTypeFilterBox.Items.AddRange(
            ["全部文件", "图片", "视频", "文档", "图片 + 视频", "图片 + 视频 + 文档"]);
        _fileTypeFilterBox.SelectedIndex = 0;
        _fileTypeFilterBox.SelectedIndexChanged += FileTypeFilterChanged;
        buttons.Controls.AddRange(
            [
                _startButton,
                _scanButton,
                _continueTaskButton,
                _pauseButton,
                _openTargetButton,
                _openManifestButton,
                _followFileCheckBox,
                _fileTypeFilterBox
            ]);
        layout.Controls.Add(buttons, 0, 7);

        _fileList.Dock = DockStyle.Fill;
        layout.Controls.Add(_fileList, 0, 8);

        _logBox.Dock = DockStyle.Fill;
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.BackColor = Color.White;
        _logBox.Text = "操作说明：\r\n" +
                       "1. 选中源目录和目标目录，设置足够低的读取速度。\r\n" +
                       "2. 首次开始会生成 _copy-manifest.tsv 清单，再逐个文件打勾。\r\n" +
                       "3. 源盘或目标盘断开时程序会自动暂停，设备恢复后需要手动点“开始 / 继续”。\r\n" +
                       "4. 读取速度暂时为 0 时程序会持续等待，不会超时跳过。\r\n" +
                       "5. “继续任务（TSV）”只负责选择清单，选好后还需点击“开始 / 继续”。\r\n" +
                       "6. 不要删除目标目录中的 _copy-manifest.tsv 和 _copy-resume。";
        layout.Controls.Add(_logBox, 0, 9);

        Controls.Add(layout);
    }

    private static Control CreatePathRow(
        string labelText,
        TextBox box,
        Button button)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 2, 0, 2)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));

        box.Dock = DockStyle.Fill;
        box.Margin = new Padding(0, 2, 8, 2);
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0, 1, 0, 1);
        panel.Controls.Add(new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0),
            Margin = new Padding(0, 1, 4, 1)
        }, 0, 0);
        panel.Controls.Add(box, 1, 0);
        panel.Controls.Add(button, 2, 0);
        return panel;
    }

    private static void ConfigureBrowseButton(Button button, string text, EventHandler click)
    {
        button.Text = text;
        button.Click += click;
    }

    private static Control CreateProgressRow(Label label, PercentageProgressBar bar, string text)
    {
        label.Text = text;
        label.AutoEllipsis = true;
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Margin = new Padding(0, 0, 8, 0);
        bar.Dock = DockStyle.Fill;
        bar.Margin = new Padding(0, 4, 0, 4);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(bar, 1, 0);
        return panel;
    }

    private void ConfigureMetricLabels()
    {
        foreach (var label in new[] { _speedMetricLabel, _timeMetricLabel })
        {
            label.AutoSize = true;
            label.Margin = new Padding(8, 5, 0, 0);
            label.ForeColor = Color.Black;
        }

        _dropMetricValueLabel.AutoSize = true;
        _dropMetricValueLabel.Margin = new Padding(2, 5, 0, 0);
        _dropMetricValueLabel.ForeColor = Color.Red;
        _dropMetricValueLabel.Font = new Font(Font, FontStyle.Bold);

        _stallMetricValueLabel.AutoSize = true;
        _stallMetricValueLabel.Margin = new Padding(2, 5, 0, 0);
        _stallMetricValueLabel.ForeColor = Color.OrangeRed;
        _stallMetricValueLabel.Font = new Font(Font, FontStyle.Bold);

        _speedMetricLabel.Text = "速度 --";
        _timeMetricLabel.Text = "剩余 --";
        _dropMetricValueLabel.Text = "0";
        _stallMetricValueLabel.Text = "0";
    }

    private Label CreateMetricCaption(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(8, 5, 0, 0)
        };
    }

    private void BrowseSource(object? sender, EventArgs e)
    {
        using var dialog = new MultiFolderDialog(_sourcePaths);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            SetSourcePaths(dialog.SelectedFolders);
    }

    private void BrowseTarget(object? sender, EventArgs e)
    {
        ChooseFolder(_targetBox);
    }

    private void SetSourcePaths(IEnumerable<string> paths)
    {
        _sourcePaths.Clear();
        foreach (var path in paths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _sourcePaths.Add(path);
        }
        _sourceBox.Text = string.Join(" | ", _sourcePaths);
        _sourceBox.SelectionStart = 0;
    }

    private static bool SourcesEqual(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second)
    {
        if (first.Count != second.Count)
            return false;
        for (var i = 0; i < first.Count; i++)
        {
            if (!string.Equals(first[i], second[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private void ChooseFolder(TextBox box)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "请选择目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (Directory.Exists(box.Text))
            dialog.SelectedPath = box.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK)
            box.Text = dialog.SelectedPath;
    }

    private async void ScanClicked(object? sender, EventArgs e)
    {
        if (_runningTask is { IsCompleted: false } || _taskStarted)
            return;

        try
        {
            var sources = _sourcePaths.ToArray();
            var target = Path.GetFullPath(_targetBox.Text.Trim());
            if (sources.Length == 0 || sources.Any(path => !Directory.Exists(path)))
                throw new DirectoryNotFoundException("请先选择至少一个有效的源文件夹。");

            var storageProblem = CopyEngine.GetStorageProblem(sources, target);
            if (storageProblem is not null)
                throw new StorageUnavailableException(storageProblem);

            var engine = new CopyEngine(
                sources,
                target,
                (long)(_speedBox.Value * 1024M * 1024M));
            engine.SetTypeFilter(SelectedFileTypeCategory());
            _engine = engine;
            _scanButton.Enabled = false;
            _statusLabel.Text = "正在扫描源文件夹并生成清单…";

            var files = await Task.Run(() => engine.ScanFiles(CancellationToken.None));
            SetInitialFileList(files);
            _fileList.SetSelectionEnabled(true);
            var copyFiles = files.Where(file =>
                file.Status is not ItemStatus.ScanError and not ItemStatus.Skipped).ToList();
            var doneFiles = copyFiles.Count(file => file.Status == ItemStatus.Done);
            var totalBytes = copyFiles.Sum(file => file.Size);
            var doneBytes = copyFiles.Where(file => file.Status == ItemStatus.Done).Sum(file => file.Size);
            _overallProgressBar.Value = totalBytes == 0
                ? 0
                : (int)Math.Clamp(100L * doneBytes / totalBytes, 0, 100);
            _overallLabel.Text =
                $"扫描完成：{doneFiles}/{copyFiles.Count} 个文件 · {FormatBytes(doneBytes)} / {FormatBytes(totalBytes)}";
            _fileLabel.Text = "当前文件：尚未开始";
            _fileProgressBar.Value = 0;
            _fileProgressBar.Stalled = false;
            _speedMetricLabel.Text = "速度 --";
            _speedMetricLabel.ForeColor = Color.Black;
            _timeMetricLabel.Text = "剩余 --";
            _dropMetricValueLabel.Text = engine.DiskDropCount.ToString(CultureInfo.InvariantCulture);
            _stallMetricValueLabel.Text = engine.StallCount.ToString(CultureInfo.InvariantCulture);
            _statusLabel.Text =
                $"扫描完成，共 {files.Count} 项；当前列表筛选：{_fileTypeFilterBox.Text}。";
            _fileList.SetFileTypeFilter(SelectedFileTypeCategory());
            SaveSettings();
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException or InvalidOperationException)
        {
            _statusLabel.Text = "扫描失败：" + ex.Message;
            MessageBox.Show(this, ex.Message, "扫描失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _scanButton.Enabled = !_taskStarted;
        }
    }

    private void FileTypeFilterChanged(object? sender, EventArgs e)
    {
        _fileList.SetFileTypeFilter(SelectedFileTypeCategory());
    }

    private void FileSelectionChanged(string relativePath, bool selected)
    {
        if (!_taskStarted)
            _engine?.SetItemSelected(relativePath, selected);
    }

    private FileTypeCategory SelectedFileTypeCategory()
    {
        return _fileTypeFilterBox.SelectedIndex switch
        {
            1 => FileTypeCategory.Images,
            2 => FileTypeCategory.Videos,
            3 => FileTypeCategory.Documents,
            4 => FileTypeCategory.Images | FileTypeCategory.Videos,
            5 => FileTypeCategory.Images | FileTypeCategory.Videos | FileTypeCategory.Documents,
            _ => FileTypeCategory.AllFiles
        };
    }

    private void ContinueTaskClicked(object? sender, EventArgs e)
    {
        if (_runningTask is { IsCompleted: false })
            return;

        using var dialog = new OpenFileDialog
        {
            Title = "打开拷贝清单并继续任务",
            Filter = "拷贝清单 (*_copy-manifest.tsv)|*_copy-manifest.tsv|TSV 文件 (*.tsv)|*.tsv|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            var target = Path.GetDirectoryName(dialog.FileName);
            if (string.IsNullOrEmpty(target))
                throw new InvalidDataException("无法确定清单所在的目标目录。");

            var sources = CopyEngine.ReadSourcesFromManifest(dialog.FileName).ToList();
            if (sources.Count == 0)
            {
                if (_sourcePaths.Count > 0 && _sourcePaths.All(Directory.Exists))
                {
                    sources.AddRange(_sourcePaths);
                    AppendLog("旧版清单未记录源目录，已沿用当前选择的源文件夹。");
                }
                else
                {
                    _targetBox.Text = target;
                    _statusLabel.Text = "清单未记录源目录，请先选择源文件夹再继续。";
                    AppendLog("该清单缺少源目录信息，请先选择源文件夹后再继续。");
                    return;
                }
            }

            if (sources.Any(path => !Directory.Exists(path)))
            {
                _targetBox.Text = target;
                _statusLabel.Text = "清单中至少一个源目录当前不可用，请恢复后再继续。";
                AppendLog("清单记录的源目录不存在或当前不可用。");
                return;
            }

            SetSourcePaths(sources);
            _targetBox.Text = target;
            var previewEngine = new CopyEngine(
                sources,
                target,
                (long)(_speedBox.Value * 1024M * 1024M));
            var files = previewEngine.ReadManifestFileList();
            _engine = previewEngine;
            _taskStarted = false;
            SetInitialFileList(files);
            _fileList.SetSelectionEnabled(true);

            var copyFiles = files.Where(file => file.Status is not ItemStatus.ScanError and not ItemStatus.Skipped).ToList();
            var doneFiles = copyFiles.Count(file => file.Status == ItemStatus.Done);
            var totalBytes = copyFiles.Sum(file => file.Size);
            var doneBytes = copyFiles.Where(file => file.Status == ItemStatus.Done).Sum(file => file.Size);
            _overallProgressBar.Value = totalBytes == 0
                ? 0
                : (int)Math.Clamp(100L * doneBytes / totalBytes, 0, 100);
            _overallLabel.Text =
                $"已载入清单：{doneFiles}/{copyFiles.Count} 个文件 · {FormatBytes(doneBytes)} / {FormatBytes(totalBytes)}";
            _fileProgressBar.Value = 0;
            _fileProgressBar.Stalled = false;
            _fileLabel.Text = "当前文件：尚未开始";
            _speedMetricLabel.Text = "速度 --";
            _speedMetricLabel.ForeColor = Color.Black;
            _timeMetricLabel.Text = "剩余 --";
            _dropMetricValueLabel.Text = previewEngine.DiskDropCount.ToString(CultureInfo.InvariantCulture);
            _stallMetricValueLabel.Text = previewEngine.StallCount.ToString(CultureInfo.InvariantCulture);
            _lastResumePath = null;
            SaveSettings();
            _statusLabel.Text = $"已选择清单并载入 {files.Count} 项；请点击“开始 / 继续”后再开始拷贝。";
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "无法打开清单", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void StartClicked(object? sender, EventArgs e)
    {
        if (_runningTask is { IsCompleted: false })
            return;

        try
        {
            var sources = _sourcePaths.ToArray();
            var target = Path.GetFullPath(_targetBox.Text.Trim());
            var bytesPerSecond = (long)(_speedBox.Value * 1024M * 1024M);
            var dynamicSpeed = _dynamicSpeedCheckBox.Checked;
            var maximumBytesPerSecond = dynamicSpeed
                ? (long)(_maxSpeedBox.Value * 1024M * 1024M)
                : bytesPerSecond;
            var growthIntervalSeconds = (int)_growthIntervalBox.Value;
            if (dynamicSpeed && maximumBytesPerSecond <= bytesPerSecond)
                throw new InvalidOperationException("动态调速的最高速度必须大于基础限速。");

            if (sources.Length == 0)
                throw new DirectoryNotFoundException("请至少选择一个源文件夹。");
            if (sources.Any(path => !Directory.Exists(path)))
                throw new DirectoryNotFoundException("至少一个源文件夹不存在或当前不可用。");

            var storageProblem = CopyEngine.GetStorageProblem(sources, target);
            if (storageProblem is not null)
            {
                _statusLabel.Text = storageProblem;
                AppendLog(storageProblem);
                return;
            }

            var manifestPath = Path.Combine(target, "_copy-manifest.tsv");
            if (!File.Exists(manifestPath) &&
                Directory.Exists(target) &&
                Directory.EnumerateFileSystemEntries(target).Any())
            {
                var answer = MessageBox.Show(
                    this,
                    "目标目录中已有内容。\r\n继续时，同名文件会在复制完整后被覆盖。\r\n是否继续？",
                    "确认目标目录",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes)
                    return;
            }

            if (_engine is null ||
                !SourcesEqual(_engine.SourceRoots, sources) ||
                !string.Equals(_engine.TargetRoot, target, StringComparison.OrdinalIgnoreCase))
            {
                _engine = new CopyEngine(sources, target, bytesPerSecond);
                ClearFileList();
            }
            else
            {
                _engine.SetBytesPerSecond(bytesPerSecond);
            }

            var selectedCategory = SelectedFileTypeCategory();
            _engine.SetTypeFilter(selectedCategory);
            if (_fileList.TotalRowCount > 0)
            {
                var selectedPaths = _fileList.GetSelectedPaths(selectedCategory);
                if (selectedPaths.Count == 0)
                    throw new InvalidOperationException("当前文件类型和勾选设置下没有可复制的文件。");
                _engine.ApplySelection(selectedPaths);
            }

            var initialBytesPerSecond = Math.Min(
                DynamicSpeedPolicy.StartingBytesPerSecond,
                dynamicSpeed ? maximumBytesPerSecond : bytesPerSecond);
            _engine.SetBytesPerSecond(initialBytesPerSecond);

            SaveSettings();
            SetRunningState(true);
            _taskStarted = true;
            _fileList.SetRunningMode(_followFileCheckBox.Checked);
            _overallProgressBar.Value = 0;
            _fileProgressBar.Value = 0;
            _fileProgressBar.Stalled = false;
            _overallLabel.Text = "总进度";
            _fileLabel.Text = "当前文件：准备中";
            _speedMetricLabel.Text = "速度 准备中";
            _speedMetricLabel.ForeColor = Color.Black;
            _timeMetricLabel.Text = "剩余 计算中";
            _dropMetricValueLabel.Text = "0";
            _stallMetricValueLabel.Text = "0";
            _lastLoggedError = null;
            _lastResumePath = null;
            _observedProcessedBytes = -1;
            AppendLog(dynamicSpeed
                ? $"开始：动态起步 0.5 MiB/s，基础限速 {_speedBox.Value:F1} MiB/s，" +
                  $"动态最高 {_maxSpeedBox.Value:F1} MiB/s，" +
                  $"增长间隔 {growthIntervalSeconds} 秒"
                : $"开始：保护起步 {initialBytesPerSecond / 1024d / 1024d:F1} MiB/s，" +
                  $"每 10 秒增加到基础限速 {_speedBox.Value:F1} MiB/s");

            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            var engine = _engine;
            _runningTask = Task.Run(() => engine.Run(ReportProgress, cancellation.Token));
            _automaticPauseReason = null;
            using var monitorCancellation = new CancellationTokenSource();
            var monitorTask = MonitorStorageAsync(
                sources,
                target,
                engine,
                bytesPerSecond,
                maximumBytesPerSecond,
                dynamicSpeed,
                dynamicSpeed ? growthIntervalSeconds : 10,
                cancellation,
                monitorCancellation.Token);
            CopyReport report;
            try
            {
                report = await _runningTask;
            }
            finally
            {
                monitorCancellation.Cancel();
                try
                {
                    await monitorTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (report.Paused && Volatile.Read(ref _automaticPauseReason) is { } automaticReason)
                report = report with { PauseReason = automaticReason };

            _overallProgressBar.Value = report.TotalFiles == 0
                ? 0
                : Math.Clamp((int)(100L * report.DoneFiles / report.TotalFiles), 0, 100);
            UpdateMetrics(0, null, report.DiskDropCount, report.StallCount);

            if (report.Paused && report.PauseReason is not null)
            {
                _statusLabel.Text = "硬盘已断开，已自动暂停：" + report.PauseReason;
                AppendLog("硬盘已断开，已自动暂停；临时文件和清单均已保留。设备恢复后请点击“开始 / 继续”。");
            }
            else if (report.Paused)
            {
                _statusLabel.Text = "已暂停，可点“开始 / 继续”。";
                AppendLog("已暂停；临时文件和清单已保留。");
            }
            else if (report.FailedFiles > 0 || report.ScanErrors > 0)
            {
                _statusLabel.Text =
                    $"完成 {report.DoneFiles}/{report.TotalFiles}，失败 {report.FailedFiles}，扫描错误 {report.ScanErrors}。";
                AppendLog("全部待处理项已走过一遍。失败项会在下次开始时重试；可查看清单。");
            }
            else
            {
                _statusLabel.Text = $"已完成 {report.DoneFiles}/{report.TotalFiles}。";
                _taskStarted = false;
                _speedMetricLabel.Text = "速度 已完成";
                _speedMetricLabel.ForeColor = Color.Black;
                _timeMetricLabel.Text = "剩余 0秒";
                _dropMetricValueLabel.Text = report.DiskDropCount.ToString(CultureInfo.InvariantCulture);
                _stallMetricValueLabel.Text = report.StallCount.ToString(CultureInfo.InvariantCulture);
                AppendLog("拷贝完成。清单中的文件均已打勾。");
            }

            if (report.SkippedItems > 0)
                AppendLog($"跳过 {report.SkippedItems} 个目录链接或文件链接，详见清单。");
        }
        catch (StorageUnavailableException ex)
        {
            _statusLabel.Text = "硬盘未就绪，已暂停：" + ex.Message;
            AppendLog("硬盘未就绪，程序没有继续读取或写入。恢复后请点击“开始 / 继续”。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            _statusLabel.Text = "未开始或已停止：" + ex.Message;
            AppendLog("错误：" + ex.Message);
            MessageBox.Show(this, ex.Message, "无法继续", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            _runningTask = null;
            SetRunningState(false);
        }
    }

    private void PauseClicked(object? sender, EventArgs e)
    {
        _pauseButton.Enabled = false;
        _fileProgressBar.Stalled = false;
        _statusLabel.Text = "正在暂停：等待本次小块写入完成…";
        _cancellation?.Cancel();
    }

    private void FollowFileCheckedChanged(object? sender, EventArgs e)
    {
        if (_taskStarted)
            _fileList.SetRunningMode(_followFileCheckBox.Checked);
    }

    private void DynamicSpeedChanged(object? sender, EventArgs e)
    {
        _maxSpeedBox.Enabled =
            _dynamicSpeedCheckBox.Checked &&
            _runningTask is not { IsCompleted: false };
        _growthIntervalBox.Enabled =
            _dynamicSpeedCheckBox.Checked &&
            _runningTask is not { IsCompleted: false };
        if (_dynamicSpeedCheckBox.Checked && _maxSpeedBox.Value <= _speedBox.Value)
        {
            _maxSpeedBox.Value = Math.Min(
                _maxSpeedBox.Maximum,
                _speedBox.Value + _maxSpeedBox.Increment);
        }
    }

    private void SpeedBoxValueChanged(object? sender, EventArgs e)
    {
        if (_maxSpeedBox.Value <= _speedBox.Value)
        {
            _maxSpeedBox.Value = Math.Min(
                _maxSpeedBox.Maximum,
                _speedBox.Value + _maxSpeedBox.Increment);
        }
    }

    private void ReportProgress(CopyProgress progress)
    {
        Interlocked.Exchange(ref _observedProcessedBytes, progress.ProcessedBytes);
        PostToUi(() =>
        {
            if (IsDisposed)
                return;

            if (progress.FileList is not null)
                ReplaceFileList(progress.FileList);
            if (progress.CurrentFile is not null)
            {
                _fileProgressBar.Stalled = false;
                UpdateFileRow(progress.CurrentFile);
            }

            var overallPercent = progress.TotalBytes == 0
                ? (progress.TotalFiles == 0 ? 0 : 100L * progress.CompletedFiles / progress.TotalFiles)
                : 100L * progress.ProcessedBytes / progress.TotalBytes;
            overallPercent = Math.Clamp(overallPercent, 0, 100);
            _overallProgressBar.Value = (int)overallPercent;
            _overallLabel.Text =
                $"总进度 · {progress.CompletedFiles}/{progress.TotalFiles} 个文件 · " +
                $"{FormatBytes(progress.ProcessedBytes)} / {FormatBytes(progress.TotalBytes)}";

            if (progress.CurrentFile is { } current)
            {
                var filePercent = current.Size == 0
                    ? (current.Status == ItemStatus.Done ? 100 : 0)
                    : (int)Math.Clamp(100L * current.ProcessedBytes / current.Size, 0, 100);
                _fileProgressBar.Value = filePercent;
                if (current.ResumeBytes > 0)
                {
                    var remainingBytes = Math.Max(0, current.Size - current.ResumeBytes);
                    _fileLabel.Text =
                        $"当前文件 · 续拷 {FormatBytes(current.ProcessedBytes)} / {FormatBytes(current.Size)}" +
                        $"（已有 {FormatBytes(current.ResumeBytes)}，本次剩余 {FormatBytes(remainingBytes)}） · " +
                        current.RelativePath;
                    if (!string.Equals(_lastResumePath, current.RelativePath, StringComparison.OrdinalIgnoreCase))
                    {
                        _lastResumePath = current.RelativePath;
                        AppendLog(
                            $"续拷文件：{current.RelativePath}，已有 {FormatBytes(current.ResumeBytes)}，" +
                            $"本次需读取剩余 {FormatBytes(remainingBytes)}。");
                    }
                }
                else
                {
                    _fileLabel.Text =
                        $"当前文件 · {FormatBytes(current.ProcessedBytes)} / {FormatBytes(current.Size)} · " +
                        current.RelativePath;
                }

                var failureKey = current.RelativePath + "\0" + current.Error;
                if (!string.IsNullOrEmpty(current.Error) &&
                    !string.Equals(failureKey, _lastLoggedError, StringComparison.Ordinal))
                {
                    _lastLoggedError = failureKey;
                    AppendLog($"失败：{current.RelativePath} — {current.Error}");
                }
            }
            UpdateMetrics(
                progress.BytesPerSecond,
                progress.EstimatedRemaining,
                progress.DiskDropCount,
                progress.StallCount);
        });
    }

    private void ClearFileList()
    {
        _fileList.ClearRows();
    }

    private void ReplaceFileList(IReadOnlyList<FileStatusRow> files)
    {
        _fileList.ReplaceRows(files);
    }

    private void SetInitialFileList(IReadOnlyList<FileStatusRow> files)
    {
        _fileList.SetInitialRows(files);
    }

    private void UpdateFileRow(FileStatusRow file)
    {
        _fileList.UpdateRow(file);
    }

    private void UpdateMetrics(
        double bytesPerSecond,
        TimeSpan? estimatedRemaining,
        int diskDrops,
        int stalls)
    {
        _speedMetricLabel.Text = $"速度 {FormatRate(bytesPerSecond)}";
        _speedMetricLabel.ForeColor = bytesPerSecond <= 0 ? Color.Red : Color.Black;
        _timeMetricLabel.Text = $"剩余 {FormatRemaining(estimatedRemaining)}";
        _dropMetricValueLabel.Text = diskDrops.ToString(CultureInfo.InvariantCulture);
        _stallMetricValueLabel.Text = stalls.ToString(CultureInfo.InvariantCulture);
    }

    private async Task MonitorStorageAsync(
        IReadOnlyList<string> sources,
        string target,
        CopyEngine engine,
        long baseBytesPerSecond,
        long maximumBytesPerSecond,
        bool dynamicSpeed,
        int growthIntervalSeconds,
        CancellationTokenSource copyCancellation,
        CancellationToken monitorCancellation)
    {
        var lastProcessed = -1L;
        var quietSeconds = 0;
        var stallTracker = new StallEscalationTracker();
        var speedPolicy = new DynamicSpeedPolicy(
            baseBytesPerSecond,
            dynamicSpeed ? maximumBytesPerSecond : baseBytesPerSecond,
            growthIntervalSeconds);

        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), monitorCancellation);
            var problem = CopyEngine.GetStorageProblem(sources, target);
            if (problem is not null)
            {
                engine.RecordDiskDrop();
                Interlocked.Exchange(ref _automaticPauseReason, problem);
                PostToUi(() =>
                {
                    if (IsDisposed)
                        return;
                    _statusLabel.Text = "硬盘已断开，正在暂停：" + problem;
                    UpdateMetrics(0, null, engine.DiskDropCount, engine.StallCount);
                    AppendLog("检测到硬盘断开，正在安全暂停；不会自动继续。");
                });

                copyCancellation.Cancel();
                return;
            }

            if (copyCancellation.IsCancellationRequested)
                continue;

            var processed = Interlocked.Read(ref _observedProcessedBytes);
            if (processed < 0)
                continue;
            if (processed != lastProcessed)
            {
                lastProcessed = processed;
                quietSeconds = 0;
                stallTracker.Reset();
            }
            else
            {
                quietSeconds++;
                if (quietSeconds == 2)
                {
                    PostToUi(() =>
                    {
                        if (!IsDisposed)
                            UpdateMetrics(0, null, engine.DiskDropCount, engine.StallCount);
                    });
                }

                if (stallTracker.RecordStall(quietSeconds))
                {
                    engine.RecordStall();
                    speedPolicy.ResetStability();
                    PostToUi(() =>
                    {
                        if (!IsDisposed)
                        {
                            UpdateMetrics(0, null, engine.DiskDropCount, engine.StallCount);
                            _fileProgressBar.Stalled = true;
                            AppendLog("读取连续 5 秒没有新字节，已记录一次卡住；程序仍会继续等待。");
                        }
                    });
                }

                if (dynamicSpeed && stallTracker.ShouldReduce(quietSeconds))
                {
                    var (previousLimit, reducedLimit) = speedPolicy.ReduceAfterStall();
                    if (reducedLimit == previousLimit)
                        continue;

                    engine.SetBytesPerSecond(reducedLimit);
                    var previousMiB = previousLimit / 1024d / 1024d;
                    var nextMiB = reducedLimit / 1024d / 1024d;
                    var stalledForSeconds = quietSeconds;
                    PostToUi(() =>
                    {
                        if (!IsDisposed)
                        {
                            UpdateMetrics(0, null, engine.DiskDropCount, engine.StallCount);
                            AppendLog(
                                $"持续卡住 {stalledForSeconds} 秒，动态限速从 {previousMiB:F1} " +
                                $"降到 {nextMiB:F1} MiB/s。");
                        }
                    });
                }
            }

            if (quietSeconds != 0)
                continue;

            if (!speedPolicy.RecordStableSecond(out var nextLimit))
                continue;
            engine.SetBytesPerSecond(nextLimit);
            var limitMiB = nextLimit / 1024d / 1024d;
            PostToUi(() =>
            {
                if (!IsDisposed)
                    AppendLog(dynamicSpeed
                        ? $"动态调速：连续 {growthIntervalSeconds} 秒未卡住，限速提升到 {limitMiB:F1} MiB/s。"
                        : $"启动保护：连续 10 秒未卡住，限速提升到 {limitMiB:F1} MiB/s。");
            });
        }
    }

    private void PostToUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The form is already closing.
        }
    }

    private void SetRunningState(bool running)
    {
        var pathsEnabled = !running && !_taskStarted;
        _sourceBox.Enabled = pathsEnabled;
        _targetBox.Enabled = pathsEnabled;
        _sourceBrowseButton.Enabled = pathsEnabled;
        _targetBrowseButton.Enabled = pathsEnabled;
        _speedBox.Enabled = !running;
        _dynamicSpeedCheckBox.Enabled = !running;
        _maxSpeedBox.Enabled = !running && _dynamicSpeedCheckBox.Checked;
        _growthIntervalBox.Enabled = !running && _dynamicSpeedCheckBox.Checked;
        _fileTypeFilterBox.Enabled = !running && !_taskStarted;
        _fileList.SetSelectionEnabled(!running && !_taskStarted);
        _startButton.Enabled = !running;
        _scanButton.Enabled = !running && !_taskStarted;
        _continueTaskButton.Enabled = !running && !_taskStarted;
        _pauseButton.Enabled = running;
    }

    private void OpenTargetClicked(object? sender, EventArgs e)
    {
        OpenFolder(_targetBox.Text);
    }

    private void OpenManifestClicked(object? sender, EventArgs e)
    {
        var path = Path.Combine(_targetBox.Text.Trim(), "_copy-manifest.tsv");
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "清单尚未生成。", "打开清单", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
            return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void AppendLog(string message)
    {
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Format(CultureInfo.InvariantCulture, "{0:F1} {1}", value, units[unit]);
    }

    private static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
            return "0 B/s";
        return FormatBytes((long)bytesPerSecond) + "/s";
    }

    private static string FormatRemaining(TimeSpan? remaining)
    {
        if (remaining is null)
            return "等待读取";
        var value = remaining.Value;
        if (value.TotalDays >= 1)
            return $"{(int)value.TotalDays}天 {value.Hours}小时";
        if (value.TotalHours >= 1)
            return $"{(int)value.TotalHours}小时 {value.Minutes}分";
        if (value.TotalMinutes >= 1)
            return $"{(int)value.TotalMinutes}分 {value.Seconds}秒";
        return $"{Math.Max(0, value.Seconds)}秒";
    }

    private string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FragileDiskCopy",
            "session.txt");

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;
            var lines = File.ReadAllLines(SettingsPath);
            if (lines.Length >= 1)
                SetSourcePaths(lines[0].Split(" | ", StringSplitOptions.RemoveEmptyEntries));
            if (lines.Length >= 2)
                _targetBox.Text = lines[1];
            if (lines.Length >= 3 &&
                decimal.TryParse(lines[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var speed))
            {
                _speedBox.Value = Math.Clamp(speed, _speedBox.Minimum, _speedBox.Maximum);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are optional.
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllLines(
                SettingsPath,
                [
                    string.Join(" | ", _sourcePaths),
                    _targetBox.Text.Trim(),
                    _speedBox.Value.ToString(CultureInfo.InvariantCulture)
                ]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are optional.
        }
    }

    private async void HandleFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_runningTask is not { IsCompleted: false } || _closeAfterPause)
            return;

        e.Cancel = true;
        var answer = MessageBox.Show(
            this,
            "正在拷贝。关闭后会保留清单和临时文件，下次可以继续。\r\n是否现在安全暂停并关闭？",
            "确认关闭",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        _closeAfterPause = true;
        _cancellation?.Cancel();
        try
        {
            await _runningTask;
        }
        catch
        {
            // Partial files and the manifest still allow a retry.
        }
        Close();
    }
}
