using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace DesktopKeyboard;

// Minimal touch-friendly slider (big thumb, tall track), drawn without any control theme.
// Tap or drag to set. Raises ValueChanged only on user interaction, not on programmatic Value.
internal sealed class TouchSlider : Panel
{
    private readonly Border _thumb;
    private const double ThumbSize = 44;

    public double Minimum = 0, Maximum = 100, Step = 0;
    private double _value;
    public event Action<double>? ValueChanged;

    public double Value
    {
        get => _value;
        set { _value = Clamp(value); UpdateThumb(); }
    }

    public TouchSlider()
    {
        Height = 56;

        var track = new Border
        {
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = new ImmutableSolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _thumb = new Border
        {
            Width = ThumbSize,
            Height = ThumbSize,
            CornerRadius = new CornerRadius(ThumbSize / 2),
            Background = new ImmutableSolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Children.Add(track);
        Children.Add(_thumb);

        PointerPressed += (_, e) => { e.Pointer.Capture(this); SetFromPointer(e.GetPosition(this).X); };
        PointerMoved += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                SetFromPointer(e.GetPosition(this).X);
        };
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var s = base.ArrangeOverride(finalSize);
        PositionThumb(finalSize.Width);
        return s;
    }

    private void SetFromPointer(double x)
    {
        double w = Bounds.Width;
        if (w <= 0) return;
        double frac = Math.Clamp(x / w, 0, 1);
        double v = Clamp(Minimum + frac * (Maximum - Minimum));
        if (v != _value) { _value = v; UpdateThumb(); ValueChanged?.Invoke(v); }
    }

    private double Clamp(double v)
    {
        v = Math.Clamp(v, Minimum, Maximum);
        if (Step > 0) v = Math.Round(v / Step) * Step;
        return v;
    }

    private void UpdateThumb() => PositionThumb(Bounds.Width);

    private void PositionThumb(double w)
    {
        if (w <= 0) return;
        double frac = Maximum > Minimum ? (_value - Minimum) / (Maximum - Minimum) : 0;
        _thumb.Margin = new Thickness(frac * (w - ThumbSize), 0, 0, 0);
    }
}
