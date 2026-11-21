using System.Drawing.Drawing2D;

namespace FragileDiskCopy;

internal sealed class VirtualFileListView : Control
{
    private const int HeaderHeight = 26;
    private const int RowHeight = 23;

    private readonly VScrollBar _scrollBar = new();
    private readonly List<FileStatusRow> _allRows = [];
    private readonly List<FileStatusRow> _rows = [];
    private readonly Dictionary<string, int> _indexes = new(StringComparer.OrdinalIgnoreCase);
    private bool _dynamicOrder;
    private bool _showUnfinishedOnly;
    private bool _followCurrent;
    private FileTypeCategory _typeFilter = FileTypeCategory.AllFiles;
    private bool _selectionEnabled = true;

    public event Action<string, bool>? SelectionChanged;

    public VirtualFileListView()
    {
        DoubleBuffered = true;
        BackColor = Color.White;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "文件列表";

        _scrollBar.Dock = DockStyle.Right;
        _scrollBar.Visible = false;
        _scrollBar.ValueChanged += (_, _) => Invalidate();
        Controls.Add(_scrollBar);
    }

    public int RowCount => _rows.Count;
    public int TotalRowCount => _allRows.Count;

    public void SetSelectionEnabled(bool enabled)
    {
        _selectionEnabled = enabled;
        Invalidate();
    }

    public IReadOnlyList<string> GetSelectedPaths(FileTypeCategory category)
    {
        return _allRows
            .Where(row => row.Selected && FileTypeFilter.Matches(row.RelativePath, category))
            .Select(row => row.RelativePath)
            .ToArray();
    }

    public void SetSelected(string relativePath, bool selected)
    {
        var index = _allRows.FindIndex(row =>
            string.Equals(row.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || _allRows[index].Selected == selected)
            return;
        _allRows[index] = _allRows[index] with { Selected = selected };
        RebuildVisibleRows();
        SelectionChanged?.Invoke(relativePath, selected);
    }

    public void SetInitialRows(IReadOnlyList<FileStatusRow> rows)
    {
        _dynamicOrder = false;
        _showUnfinishedOnly = false;
        _followCurrent = false;
        ReplaceAllRows(rows);
        RebuildVisibleRows();
        _scrollBar.Value = 0;
    }

    public void ReplaceRows(IReadOnlyList<FileStatusRow> rows)
    {
        ReplaceAllRows(rows);
        RebuildVisibleRows();
    }

    public void SetRunningMode(bool followCurrent)
    {
        _dynamicOrder = true;
        _showUnfinishedOnly = followCurrent;
        _followCurrent = followCurrent;
        RebuildVisibleRows();
    }

    public void SetFileTypeFilter(FileTypeCategory category)
    {
        _typeFilter = category;
        RebuildVisibleRows();
    }

    public void UpdateRow(FileStatusRow row)
    {
        var allIndex = _allRows.FindIndex(candidate =>
            string.Equals(candidate.RelativePath, row.RelativePath, StringComparison.OrdinalIgnoreCase));
        if (allIndex < 0)
        {
            _allRows.Add(row);
        }
        else
        {
            _allRows[allIndex] = row;
        }

        RebuildVisibleRows();
        FollowCurrentFile(row.RelativePath);
    }

    public void ClearRows()
    {
        _allRows.Clear();
        _rows.Clear();
        _indexes.Clear();
        _dynamicOrder = false;
        _showUnfinishedOnly = false;
        _followCurrent = false;
        _scrollBar.Value = 0;
        UpdateScrollBar();
        UpdateAccessibleDescription();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScrollBar();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_scrollBar.Visible)
            return;

        var delta = e.Delta > 0 ? -3 : 3;
        _scrollBar.Value = Math.Clamp(_scrollBar.Value + delta, _scrollBar.Minimum, _scrollBar.Maximum - _scrollBar.LargeChange + 1);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_selectionEnabled || e.Button != MouseButtons.Left ||
            e.Y < HeaderHeight || e.X >= 28)
            return;

        var visibleIndex = (e.Y - HeaderHeight) / RowHeight;
        var index = _scrollBar.Value + visibleIndex;
        if (visibleIndex < 0 || visibleIndex >= VisibleRowCount || index >= _rows.Count)
            return;

        var row = _rows[index];
        SetSelected(row.RelativePath, !row.Selected);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.None;
        e.Graphics.Clear(Color.White);

        var width = ClientSize.Width - (_scrollBar.Visible ? _scrollBar.Width : 0);
        var columns = GetColumns(width);
        DrawHeader(e.Graphics, columns);

        if (_rows.Count == 0)
        {
            var emptyMessage = _allRows.Count == 0
                ? "尚未载入文件清单"
                : _typeFilter != FileTypeCategory.AllFiles
                    ? "没有符合文件类型筛选的文件"
                    : "没有未完成文件";
            TextRenderer.DrawText(
                e.Graphics,
                emptyMessage,
                Font,
                new Rectangle(10, HeaderHeight + 16, width - 20, 24),
                Color.DimGray,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }

        var first = _scrollBar.Value;
        var visibleRows = VisibleRowCount;
        for (var visible = 0; visible < visibleRows && first + visible < _rows.Count; visible++)
        {
            var index = first + visible;
            var row = _rows[index];
            var y = HeaderHeight + visible * RowHeight;
            var rowRectangle = new Rectangle(0, y, width, RowHeight);
            if (index % 2 == 1)
            {
                using var alternate = new SolidBrush(Color.FromArgb(249, 249, 249));
                e.Graphics.FillRectangle(alternate, rowRectangle);
            }

            var color = row.Status switch
            {
                _ when !row.Selected => Color.Gray,
                ItemStatus.Copying or ItemStatus.Done => Color.ForestGreen,
                ItemStatus.Failed or ItemStatus.ScanError => Color.Firebrick,
                ItemStatus.Skipped => Color.DimGray,
                _ => Color.Black
            };
            ControlPaint.DrawCheckBox(
                e.Graphics,
                new Rectangle(6, y + 4, 14, 14),
                row.Selected ? ButtonState.Checked : ButtonState.Normal);
            var x = 22;
            DrawCell(e.Graphics, FileStatusText(row), rowRectangle, x, columns[0] - 22, color);
            x += columns[0];
            DrawCell(e.Graphics, row.RelativePath, rowRectangle, x, columns[1], Color.Black);
            x += columns[1];
            DrawCell(e.Graphics, FormatBytes(row.Size), rowRectangle, x, columns[2], Color.Black);
            x += columns[2];
            DrawCell(
                e.Graphics,
                row.ProcessedBytes > 0 ? FormatExactBytes(row.ProcessedBytes) : "",
                rowRectangle,
                x,
                columns[3],
                Color.Black);
            x += columns[3];
            DrawCell(e.Graphics, row.Error ?? "", rowRectangle, x, columns[4], Color.Firebrick);

            using var line = new Pen(Color.FromArgb(235, 235, 235));
            e.Graphics.DrawLine(line, 0, y + RowHeight - 1, width, y + RowHeight - 1);
        }
    }

    private int VisibleRowCount => Math.Max(0, (ClientSize.Height - HeaderHeight) / RowHeight);

    private void ReplaceAllRows(IReadOnlyList<FileStatusRow> rows)
    {
        _allRows.Clear();
        _allRows.AddRange(rows);
    }

    private void RebuildVisibleRows()
    {
        var filtered = _allRows.Where(row => FileTypeFilter.Matches(row.RelativePath, _typeFilter));
        var ordered = _dynamicOrder
            ? _showUnfinishedOnly
                ? filtered.Where(IsUnfinished)
                : filtered.OrderBy(row => row.Status == ItemStatus.Done ? 1 : 0)
            : filtered;

        _rows.Clear();
        _rows.AddRange(ordered);
        _indexes.Clear();
        for (var i = 0; i < _rows.Count; i++)
            _indexes[_rows[i].RelativePath] = i;

        UpdateScrollBar();
        UpdateAccessibleDescription();
        Invalidate();
    }

    private static bool IsUnfinished(FileStatusRow row)
    {
        return row.Selected &&
               row.Status is ItemStatus.Pending or ItemStatus.Copying or ItemStatus.Failed;
    }

    private void FollowCurrentFile(string relativePath)
    {
        if (!_followCurrent || !_indexes.TryGetValue(relativePath, out var index))
            return;

        var first = _scrollBar.Value;
        var visibleRows = VisibleRowCount;
        if (index >= first && index < first + visibleRows)
            return;

        _scrollBar.Value = Math.Clamp(
            index - visibleRows / 3,
            _scrollBar.Minimum,
            Math.Max(_scrollBar.Minimum, _scrollBar.Maximum - _scrollBar.LargeChange + 1));
    }

    private void UpdateScrollBar()
    {
        var visibleRows = VisibleRowCount;
        var needed = _rows.Count > visibleRows;
        _scrollBar.Visible = needed;
        if (!needed)
        {
            _scrollBar.Value = 0;
            return;
        }

        _scrollBar.Minimum = 0;
        _scrollBar.Maximum = _rows.Count - 1;
        _scrollBar.LargeChange = Math.Max(1, visibleRows);
        _scrollBar.SmallChange = 1;
        _scrollBar.Value = Math.Min(_scrollBar.Value, Math.Max(0, _scrollBar.Maximum - _scrollBar.LargeChange + 1));
    }

    private void UpdateAccessibleDescription()
    {
        var completed = _allRows.Count(row => row.Status == ItemStatus.Done);
        AccessibleDescription = $"当前显示 {_rows.Count} 项，共 {_allRows.Count} 项，已完成 {completed} 项";
    }

    private static int[] GetColumns(int width)
    {
        var status = Math.Max(80, width * 12 / 100);
        var size = Math.Max(80, width * 13 / 100);
        var processed = Math.Max(130, width * 18 / 100);
        var error = Math.Max(140, width * 20 / 100);
        var path = Math.Max(100, width - status - size - processed - error);
        return [status, path, size, processed, error];
    }

    private void DrawHeader(Graphics graphics, int[] columns)
    {
        var width = ClientSize.Width - (_scrollBar.Visible ? _scrollBar.Width : 0);
        using var background = new SolidBrush(Color.FromArgb(245, 245, 245));
        using var line = new Pen(Color.FromArgb(205, 205, 205));
        graphics.FillRectangle(background, 0, 0, width, HeaderHeight);
        graphics.DrawLine(line, 0, HeaderHeight - 1, width, HeaderHeight - 1);

        TextRenderer.DrawText(
            graphics,
            "选",
            Font,
            new Rectangle(5, 0, 22, HeaderHeight),
            Color.Black,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        string[] headers = ["状态", "文件", "大小", "已处理", "说明"];
        for (var i = 0; i < headers.Length; i++)
        {
            var x = GetColumnX(columns, i) + (i == 0 ? 22 : 0);
            var textWidth = columns[i] - (i == 0 ? 22 : 0);
            TextRenderer.DrawText(
                graphics,
                headers[i],
                Font,
                new Rectangle(x + 6, 0, textWidth - 8, HeaderHeight),
                Color.Black,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    private static void DrawCell(
        Graphics graphics,
        string text,
        Rectangle row,
        int columnX,
        int columnWidth,
        Color color)
    {
        TextRenderer.DrawText(
            graphics,
            text,
            SystemFonts.MessageBoxFont ?? Control.DefaultFont,
            new Rectangle(row.X + columnX + 6, row.Y, columnWidth - 8, row.Height),
            color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static int GetColumnX(int[] columns, int index)
    {
        var x = 0;
        for (var i = 0; i < index; i++)
            x += columns[i];
        return x;
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
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value:F1} {units[unit]}");
    }

    private static string FormatExactBytes(long bytes)
    {
        return bytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " 字节";
    }

    private static string FileStatusText(FileStatusRow row)
    {
        if (!row.Selected)
            return "未勾选";
        return row.Status switch
        {
            ItemStatus.Pending => "待拷贝",
            ItemStatus.Copying => "正在拷贝",
            ItemStatus.Done => "已完成",
            ItemStatus.Failed => "失败",
            ItemStatus.ScanError => "扫描错误",
            ItemStatus.Skipped => "已跳过",
            _ => row.Status.ToString()
        };
    }
}
