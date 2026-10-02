using System.Drawing.Drawing2D;
using TextFlow.Contracts.Targeting;
using TextFlow.Core.Menus;
using TextFlow.Infrastructure.Windows;

namespace TextFlow.Spikes;

/// <summary>
/// S7 prototype of the group menu. Never activates (WS_EX_NOACTIVATE, MA_NOACTIVATE), so the target
/// app keeps focus and caret; keyboard input arrives from the hook via <see cref="Apply"/>.
/// All members run on the UI thread owned by <see cref="MenuHost"/>.
/// </summary>
internal sealed class GroupMenuPopup : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopMost = 0x00000008;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const int MaxVisibleRows = 12;

    private static readonly Color Background = Color.FromArgb(0xFF, 0xFF, 0xFF);
    private static readonly Color HeaderTop = Color.FromArgb(0xE8, 0xF1, 0xFD);
    private static readonly Color HeaderBottom = Color.FromArgb(0xF7, 0xF9, 0xFC);
    private static readonly Color Border = Color.FromArgb(0xC4, 0xC4, 0xC4);
    private static readonly Color Foreground = Color.FromArgb(0x1B, 0x1B, 0x1B);
    private static readonly Color Muted = Color.FromArgb(0x6B, 0x6B, 0x6B);
    private static readonly Color Accent = Color.FromArgb(0x00, 0x5F, 0xB8);
    private static readonly Color SelectedBackground = Color.FromArgb(0xDB, 0xEA, 0xFB);
    private static readonly Color HoverBackground = Color.FromArgb(0xF2, 0xF2, 0xF2);

    private readonly Action<MenuStep> _onFinished;
    private MenuState? _state;
    private double _scale = 1;
    private int _scroll;
    private int _hover = -1;
    private bool _roundedByDwm;
    private Font _font;
    private Font _infoFont;
    private Font _headerFont;

    public GroupMenuPopup(Action<MenuStep> onFinished)
    {
        _onFinished = onFinished;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Background;
        (_font, _infoFont, _headerFont) = CreateFonts(1);
    }

    private readonly Lock _boundsLock = new();
    private Rectangle _visibleBounds;

    /// <summary>Screen bounds while visible, readable from any thread (hook clicks are tested against it).</summary>
    public Rectangle VisibleBounds
    {
        get
        {
            lock (_boundsLock)
            {
                return _visibleBounds;
            }
        }

        private set
        {
            lock (_boundsLock)
            {
                _visibleBounds = value;
            }
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTopMost;
            return parameters;
        }
    }

    private int RowHeight => Scaled(30);

    private int HeaderHeight => Scaled(30);

    public void Open(GroupMenu menu, PixelRect anchor, PixelRect monitor, double scale)
    {
        if (Math.Abs(scale - _scale) > 0.01)
        {
            DisposeFonts();
            (_font, _infoFont, _headerFont) = CreateFonts(scale);
            _scale = scale;
        }

        Present(MenuNavigator.Open(menu));
        Location = Place(anchor, monitor);
        if (!Visible)
        {
            Show();
        }

        VisibleBounds = Bounds;
    }

    public void Apply(MenuInput input)
    {
        if (_state is not null)
        {
            ApplyStep(MenuNavigator.Apply(_state, input));
        }
    }

    /// <summary>Closes without a choice (focus change, click outside, other key).</summary>
    public void Dismiss()
    {
        if (_state is not null)
        {
            ApplyStep(new MenuStep(_state, Closed: true));
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseActivate)
        {
            m.Result = MaNoActivate; // clicking must not steal focus from the target
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = IndexAt(e.Location);
        if (hover != _hover)
        {
            _hover = hover;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_state is not null && IndexAt(e.Location) is var index and >= 0)
        {
            ApplyStep(MenuNavigator.Activate(_state, index));
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_state is null)
        {
            return;
        }

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var level = _state.Current;

        using (var header = new LinearGradientBrush(new Rectangle(0, 0, Width, HeaderHeight), HeaderTop, HeaderBottom, LinearGradientMode.Vertical))
        {
            g.FillRectangle(header, 0, 0, Width, HeaderHeight);
        }

        var breadcrumb = string.Join("  ›  ", _state.Path.Select(p => p.Title));
        TextRenderer.DrawText(g, breadcrumb, _headerFont, new Rectangle(Scaled(12), 0, Width - Scaled(24), HeaderHeight), Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        var visible = Math.Min(MaxVisibleRows, level.Entries.Count);
        for (var row = 0; row < visible; row++)
        {
            var index = _scroll + row;
            DrawEntry(g, level, index, new Rectangle(0, HeaderHeight + (row * RowHeight), Width, RowHeight));
        }

        if (level.Entries.Count == 0)
        {
            TextRenderer.DrawText(g, "(grupo vacío)", _infoFont, new Rectangle(Scaled(12), HeaderHeight, Width, RowHeight), Muted,
                TextFormatFlags.VerticalCenter);
        }

        if (!_roundedByDwm)
        {
            using var border = new Pen(Border);
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _roundedByDwm = WindowStyling.TryRoundCorners(Handle); // Windows 11: DWM draws corners, border and shadow
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeFonts();
        }

        base.Dispose(disposing);
    }

    private void DrawEntry(Graphics g, MenuLevel level, int index, Rectangle row)
    {
        var entry = level.Entries[index];
        var highlighted = index == level.Selected || (index == _hover && entry.IsSelectable);
        if (highlighted)
        {
            var pill = Rectangle.FromLTRB(row.Left + Scaled(4), row.Top + Scaled(2), row.Right - Scaled(4), row.Bottom - Scaled(2));
            using var path = RoundedRectangle(pill, Scaled(6));
            using var brush = new SolidBrush(index == level.Selected ? SelectedBackground : HoverBackground);
            g.FillPath(brush, path);
        }

        if (index == level.Selected)
        {
            using var accent = new SolidBrush(Accent);
            using var bar = RoundedRectangle(new Rectangle(row.Left + Scaled(4), row.Top + Scaled(8), Scaled(3), row.Height - Scaled(16)), Scaled(1));
            g.FillPath(accent, bar);
        }

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var numberBox = new Rectangle(row.Left + Scaled(10), row.Top, Scaled(22), row.Height);
        if (level.NumberOf(entry) is { } number)
        {
            TextRenderer.DrawText(g, number.ToString(System.Globalization.CultureInfo.InvariantCulture), _headerFont, numberBox, Accent,
                flags | TextFormatFlags.HorizontalCenter);
        }
        else if (!entry.IsSelectable)
        {
            TextRenderer.DrawText(g, "ⓘ", _font, numberBox, Muted, flags | TextFormatFlags.HorizontalCenter);
        }

        var labelBox = Rectangle.FromLTRB(numberBox.Right + Scaled(6), row.Top, row.Right - Scaled(28), row.Bottom);
        TextRenderer.DrawText(g, entry.Label, entry.IsSelectable ? _font : _infoFont, labelBox, entry.IsSelectable ? Foreground : Muted, flags);

        if (entry is MenuGroupEntry)
        {
            TextRenderer.DrawText(g, "›", _font, Rectangle.FromLTRB(row.Right - Scaled(26), row.Top, row.Right - Scaled(8), row.Bottom), Muted,
                flags | TextFormatFlags.HorizontalCenter);
        }
    }

    private void Present(MenuState state)
    {
        _state = state;
        _hover = -1;
        var level = state.Current;
        var rows = Math.Clamp(level.Entries.Count, 1, MaxVisibleRows);

        var widest = level.Entries
            .Select(e => TextRenderer.MeasureText(e.Label, e.IsSelectable ? _font : _infoFont).Width)
            .DefaultIfEmpty(0)
            .Max();
        var title = TextRenderer.MeasureText(string.Join("  ›  ", state.Path.Select(p => p.Title)), _headerFont).Width + Scaled(24);
        var width = Math.Clamp(Math.Max(widest + Scaled(80), title), Scaled(260), Scaled(560));
        Size = new Size(width, HeaderHeight + (rows * RowHeight) + Scaled(1));

        KeepSelectionVisible(level);
        Invalidate();
    }

    private void ApplyStep(MenuStep step)
    {
        if (step.IsFinished)
        {
            _state = null;
            Hide();
            VisibleBounds = Rectangle.Empty;
            _onFinished(step);
            return;
        }

        var levelChanged = _state is null || step.State.Path.Count != _state.Path.Count;
        if (levelChanged)
        {
            _scroll = 0;
            var location = Location;
            Present(step.State);
            Location = location;
            VisibleBounds = Bounds;
        }
        else
        {
            _state = step.State;
            KeepSelectionVisible(step.State.Current);
            Invalidate();
        }
    }

    private void KeepSelectionVisible(MenuLevel level)
    {
        if (level.Selected < _scroll)
        {
            _scroll = level.Selected;
        }
        else if (level.Selected >= _scroll + MaxVisibleRows)
        {
            _scroll = level.Selected - MaxVisibleRows + 1;
        }

        _scroll = Math.Max(0, _scroll);
    }

    /// <summary>Below the caret, flipped above when there is no room, clamped to the target's monitor.</summary>
    private Point Place(PixelRect anchor, PixelRect monitor)
    {
        var gap = Scaled(4);
        var x = Math.Clamp(anchor.Left, monitor.Left, Math.Max(monitor.Left, monitor.Right - Width));
        var y = anchor.Bottom + gap;
        if (y + Height > monitor.Bottom)
        {
            y = Math.Max(monitor.Top, anchor.Top - gap - Height);
        }

        return new Point(x, y);
    }

    private int IndexAt(Point point)
    {
        if (_state is null || point.Y < HeaderHeight)
        {
            return -1;
        }

        var index = _scroll + ((point.Y - HeaderHeight) / RowHeight);
        return index < _state.Current.Entries.Count ? index : -1;
    }

    private int Scaled(int value) => (int)Math.Round(value * _scale);

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(1, radius * 2);
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static (Font Normal, Font Info, Font Header) CreateFonts(double scale)
    {
        var size = (float)(14 * scale);
        return (
            new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Pixel),
            new Font("Segoe UI", size, FontStyle.Italic, GraphicsUnit.Pixel),
            new Font("Segoe UI Semibold", (float)(12.5 * scale), FontStyle.Regular, GraphicsUnit.Pixel));
    }

    private void DisposeFonts()
    {
        _font.Dispose();
        _infoFont.Dispose();
        _headerFont.Dispose();
    }
}
