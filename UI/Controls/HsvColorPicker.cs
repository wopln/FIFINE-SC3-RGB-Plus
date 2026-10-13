using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SC3RGBController.UI.Controls;

public sealed class HsvColorPicker : FrameworkElement
{
    private enum InteractionMode { None, Hue, SaturationValue }

    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor), typeof(Color), typeof(HsvColorPicker),
        new FrameworkPropertyMetadata(Color.FromRgb(255, 120, 0), FrameworkPropertyMetadataOptions.AffectsRender, OnSelectedColorChanged));

    // Hue ring segments: one frozen brush per 2 degrees, shared by every instance.
    private const int HueSegmentStep = 2;
    private static readonly SolidColorBrush[] HueSegmentBrushes = CreateHueSegmentBrushes();

    // Selector strokes: 3px white plus a 1.5px black outer edge keeps both selectors
    // visible (>= 3:1) on light and dark colors alike.
    private static readonly Pen SelectorWhitePen = Freeze(new Pen(Brushes.White, 3));
    private static readonly Pen SelectorBlackPen = Freeze(new Pen(Brushes.Black, 1.5));
    private static readonly Pen SaturationValueEdgePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(28, 28, 28))), 3));

    private double _hue = 28.235;
    private double _saturation = 1;
    private double _value = 1;
    private double _cachedHue = double.NaN;
    private WriteableBitmap? _svBitmap;
    private InteractionMode _interaction;
    private bool _settingInternally;
    private Pen[]? _hueSegmentPens;
    private double _hueSegmentPenThickness = double.NaN;

    public HsvColorPicker()
    {
        Focusable = true;
        FocusVisualStyle = null; // Focus ring is drawn in OnRender.
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
        MinWidth = 240;
        MinHeight = 240;
    }

    public Color SelectedColor
    {
        get => (Color)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    public event EventHandler? SelectedColorChanged;

    internal double Hue => _hue;
    internal double Saturation => _saturation;
    internal double Brightness => _value;

    protected override AutomationPeer OnCreateAutomationPeer() => new HsvColorPickerAutomationPeer(this);

    /// <summary>Sets the color from UI Automation (ValuePattern.SetValue), preserving any binding.</summary>
    internal void SetColorFromAutomation(Color color)
    {
        SetCurrentValue(SelectedColorProperty, color);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double size = Math.Max(1, Math.Min(ActualWidth, ActualHeight));
        Point center = new(ActualWidth / 2, ActualHeight / 2);
        double ringThickness = Math.Clamp(size * 0.115, 24, 38);
        double outerRadius = size / 2 - 7;
        double ringRadius = outerRadius - ringThickness / 2;
        double innerRadius = ringRadius - ringThickness / 2 - 13;

        DrawHueRing(drawingContext, center, ringRadius, ringThickness);
        DrawSaturationValue(drawingContext, center, innerRadius);
        DrawSelectors(drawingContext, center, ringRadius, innerRadius);

        if (IsKeyboardFocused)
        {
            DrawFocusRing(drawingContext);
        }
    }

    private void DrawFocusRing(DrawingContext dc)
    {
        bool highContrast = SystemParameters.HighContrast;
        Brush ringBrush = highContrast
            ? SystemColors.HighlightBrush
            : TryFindResource("FocusRingBrush") as Brush ?? Brushes.White;
        Brush innerBrush = highContrast ? SystemColors.WindowBrush : Brushes.Black;

        double width = ActualWidth;
        double height = ActualHeight;
        // 2px ring occupying 0..2px from the edge, then a 1px contrasting line at 2..3px.
        dc.DrawRoundedRectangle(
            null,
            new Pen(ringBrush, 2),
            new Rect(1, 1, Math.Max(0, width - 2), Math.Max(0, height - 2)),
            10,
            10);
        dc.DrawRoundedRectangle(
            null,
            new Pen(innerBrush, 1),
            new Rect(2.5, 2.5, Math.Max(0, width - 5), Math.Max(0, height - 5)),
            8.5,
            8.5);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        CaptureMouse();
        UpdateFromPoint(e.GetPosition(this), true);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateFromPoint(e.GetPosition(this), false);
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
        {
            UpdateFromPoint(e.GetPosition(this), false);
            ReleaseMouseCapture();
            _interaction = InteractionMode.None;
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        double step = shift ? 0.05 : 0.01;
        double hueStep = shift ? 15 : 5;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Ctrl+Left/Right: fine hue. Other Ctrl chords (e.g. Ctrl+PageUp for tab switching) pass through.
            if (e.Key == Key.Left) _hue = (_hue + 359) % 360;
            else if (e.Key == Key.Right) _hue = (_hue + 1) % 360;
            else { base.OnKeyDown(e); return; }
        }
        else
        {
            switch (e.Key)
            {
                case Key.Left: _saturation = Math.Max(0, _saturation - step); break;
                case Key.Right: _saturation = Math.Min(1, _saturation + step); break;
                case Key.Up: _value = Math.Min(1, _value + step); break;
                case Key.Down: _value = Math.Max(0, _value - step); break;
                case Key.PageUp: _hue = WrapHue(_hue - hueStep); break;
                case Key.PageDown: _hue = WrapHue(_hue + hueStep); break;
                case Key.Home: _saturation = 1; _value = 1; break; // Pure, fully bright version of the current hue.
                default: base.OnKeyDown(e); return;
            }
        }

        CommitColor();
        e.Handled = true;
    }

    private static double WrapHue(double hue) => (hue % 360 + 360) % 360;

    private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        HsvColorPicker picker = (HsvColorPicker)d;
        if (!picker._settingInternally)
        {
            (picker._hue, picker._saturation, picker._value) = RgbToHsv((Color)e.NewValue, picker._hue);
            picker.InvalidateVisual();
        }

        // Only notify if a peer already exists (i.e. an automation client has asked for one).
        if (UIElementAutomationPeer.FromElement(picker) is HsvColorPickerAutomationPeer peer)
        {
            peer.RaiseValueChanged();
        }

        picker.SelectedColorChanged?.Invoke(picker, EventArgs.Empty);
    }

    private void UpdateFromPoint(Point point, bool chooseInteraction)
    {
        double size = Math.Max(1, Math.Min(ActualWidth, ActualHeight));
        Point center = new(ActualWidth / 2, ActualHeight / 2);
        double dx = point.X - center.X;
        double dy = point.Y - center.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        double ringThickness = Math.Clamp(size * 0.115, 24, 38);
        double outerRadius = size / 2 - 7;
        double ringRadius = outerRadius - ringThickness / 2;
        double innerRadius = ringRadius - ringThickness / 2 - 13;

        if (chooseInteraction)
        {
            _interaction = distance >= ringRadius - ringThickness * 0.72
                ? InteractionMode.Hue
                : InteractionMode.SaturationValue;
        }

        if (_interaction == InteractionMode.Hue)
        {
            _hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
        }
        else if (_interaction == InteractionMode.SaturationValue)
        {
            double halfExtent = innerRadius / Math.Sqrt(2);
            double x = Math.Clamp(dx, -halfExtent, halfExtent);
            double y = Math.Clamp(dy, -halfExtent, halfExtent);
            _saturation = Math.Clamp((x / halfExtent + 1) / 2, 0, 1);
            _value = Math.Clamp(1 - (y / halfExtent + 1) / 2, 0, 1);
        }

        CommitColor();
    }

    private void CommitColor()
    {
        _settingInternally = true;
        try
        {
            SetCurrentValue(SelectedColorProperty, ColorFromHsv(_hue, _saturation, _value));
        }
        finally
        {
            _settingInternally = false;
        }
        InvalidateVisual();
    }

    private void DrawHueRing(DrawingContext dc, Point center, double radius, double thickness)
    {
        Pen[] pens = GetHueSegmentPens(thickness);
        for (int i = 0; i < pens.Length; i++)
        {
            int degree = i * HueSegmentStep;
            double a1 = degree * Math.PI / 180;
            double a2 = (degree + 2.6) * Math.PI / 180;
            Point p1 = new(center.X + Math.Cos(a1) * radius, center.Y + Math.Sin(a1) * radius);
            Point p2 = new(center.X + Math.Cos(a2) * radius, center.Y + Math.Sin(a2) * radius);
            dc.DrawLine(pens[i], p1, p2);
        }
    }

    private Pen[] GetHueSegmentPens(double thickness)
    {
        if (_hueSegmentPens is not null && _hueSegmentPenThickness == thickness)
        {
            return _hueSegmentPens;
        }

        Pen[] pens = new Pen[HueSegmentBrushes.Length];
        for (int i = 0; i < pens.Length; i++)
        {
            pens[i] = Freeze(new Pen(HueSegmentBrushes[i], thickness)
            {
                StartLineCap = PenLineCap.Square,
                EndLineCap = PenLineCap.Square
            });
        }

        _hueSegmentPens = pens;
        _hueSegmentPenThickness = thickness;
        return pens;
    }

    private static SolidColorBrush[] CreateHueSegmentBrushes()
    {
        SolidColorBrush[] brushes = new SolidColorBrush[360 / HueSegmentStep];
        for (int i = 0; i < brushes.Length; i++)
        {
            brushes[i] = Freeze(new SolidColorBrush(ColorFromHsv(i * HueSegmentStep, 1, 1)));
        }
        return brushes;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private void DrawSaturationValue(DrawingContext dc, Point center, double radius)
    {
        EnsureSaturationValueBitmap();
        Rect rect = new(center.X - radius, center.Y - radius, radius * 2, radius * 2);
        dc.PushClip(new EllipseGeometry(center, radius, radius));
        dc.DrawImage(_svBitmap, rect);
        dc.Pop();
        dc.DrawEllipse(null, SaturationValueEdgePen, center, radius, radius);
    }

    private void DrawSelectors(DrawingContext dc, Point center, double ringRadius, double innerRadius)
    {
        // Hue selector: white ring centered at r=13 (11.5..14.5), black outer edge at 14.5..16.
        double angle = _hue * Math.PI / 180;
        Point huePoint = new(center.X + Math.Cos(angle) * ringRadius, center.Y + Math.Sin(angle) * ringRadius);
        dc.DrawEllipse(null, SelectorWhitePen, huePoint, 13, 13);
        dc.DrawEllipse(null, SelectorBlackPen, huePoint, 15.25, 15.25);

        // Saturation/value selector: swatch of the current color, white ring (6.5..9.5), black outer edge (9.5..11).
        double halfExtent = innerRadius / Math.Sqrt(2);
        Point svPoint = new(
            center.X + (2 * _saturation - 1) * halfExtent,
            center.Y + (1 - 2 * _value) * halfExtent);
        dc.DrawEllipse(new SolidColorBrush(SelectedColor), SelectorWhitePen, svPoint, 8, 8);
        dc.DrawEllipse(null, SelectorBlackPen, svPoint, 10.25, 10.25);
    }

    private void EnsureSaturationValueBitmap()
    {
        if (_svBitmap is not null && Math.Abs(_cachedHue - _hue) < 0.1)
        {
            return;
        }

        const int size = 256;
        const int stride = size * 4;
        byte[] pixels = new byte[size * stride];
        for (int y = 0; y < size; y++)
        {
            double normalizedY = 2 * y / (double)(size - 1) - 1;
            double value = Math.Clamp(1 - (normalizedY / Math.Sqrt(2) + 1) / 2, 0, 1);
            for (int x = 0; x < size; x++)
            {
                double normalizedX = 2 * x / (double)(size - 1) - 1;
                double saturation = Math.Clamp((normalizedX / Math.Sqrt(2) + 1) / 2, 0, 1);
                Color color = ColorFromHsv(_hue, saturation, value);
                int offset = y * stride + x * 4;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = 255;
            }
        }

        _svBitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        _svBitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, stride, 0);
        _cachedHue = _hue;
    }

    public static Color ColorFromHsv(double hue, double saturation, double value)
    {
        hue = (hue % 360 + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);
        double chroma = value * saturation;
        double section = hue / 60;
        double x = chroma * (1 - Math.Abs(section % 2 - 1));
        (double r, double g, double b) = section switch
        {
            < 1 => (chroma, x, 0.0), < 2 => (x, chroma, 0.0), < 3 => (0.0, chroma, x),
            < 4 => (0.0, x, chroma), < 5 => (x, 0.0, chroma), _ => (chroma, 0.0, x)
        };
        double match = value - chroma;
        return Color.FromRgb(
            (byte)Math.Round((r + match) * 255),
            (byte)Math.Round((g + match) * 255),
            (byte)Math.Round((b + match) * 255));
    }

    public static (double Hue, double Saturation, double Value) RgbToHsv(Color color, double fallbackHue = 0)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double hue = delta == 0 ? fallbackHue : max == r
            ? 60 * (((g - b) / delta) % 6)
            : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        return (hue, max == 0 ? 0 : delta / max, max);
    }
}
