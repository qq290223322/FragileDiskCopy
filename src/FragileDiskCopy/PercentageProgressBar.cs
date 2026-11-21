namespace FragileDiskCopy;

internal sealed class PercentageProgressBar : Control
{
    private int _value;
    private bool _stalled;

    public PercentageProgressBar()
    {
        DoubleBuffered = true;
        AccessibleRole = AccessibleRole.ProgressBar;
        MinimumSize = new Size(80, 16);
    }

    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (_value == clamped)
                return;
            _value = clamped;
            Invalidate();
        }
    }

    public bool Stalled
    {
        get => _stalled;
        set
        {
            if (_stalled == value)
                return;
            _stalled = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var bounds = new Rectangle(0, 0, Math.Max(1, ClientSize.Width - 1), Math.Max(1, ClientSize.Height - 1));
        e.Graphics.Clear(SystemColors.Control);
        using var border = new Pen(SystemColors.ControlDark);
        e.Graphics.DrawRectangle(border, bounds);

        var fillWidth = (int)Math.Round((bounds.Width - 2) * (_value / 100d));
        if (fillWidth > 0)
        {
            using var fill = new SolidBrush(
                _stalled ? Color.Red : Color.FromArgb(83, 183, 39));
            e.Graphics.FillRectangle(fill, 1, 1, fillWidth, bounds.Height - 2);
        }

        TextRenderer.DrawText(
            e.Graphics,
            $"{_value}%",
            Font,
            bounds,
            Color.Black,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}
