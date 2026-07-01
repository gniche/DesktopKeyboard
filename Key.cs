using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace DesktopKeyboard;

// Shared immutable brushes: the blue accent, the amber modifier-lock, and the chrome greys.
internal static class Palette
{
    public static readonly IBrush Accent = new ImmutableSolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2));
    public static readonly IBrush ModLock = new ImmutableSolidColorBrush(Color.FromRgb(210, 140, 30));
    public static readonly IBrush Panel = new ImmutableSolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    public static readonly IBrush Button = new ImmutableSolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
    public static readonly IBrush Outline = new ImmutableSolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
}

// White text over four 1px-offset black copies — the outline-text trick used for key glyphs
// and the mode button label. Drawn directly in one Control (cached FormattedText painted five
// times) rather than six stacked TextBlocks, so a full keyboard is ~1 element per glyph.
internal sealed class OutlinedText : Control
{
    private static readonly (double X, double Y)[] Offsets = { (-1, -1), (1, -1), (-1, 1), (1, 1) };

    private readonly double _fontSize;
    private readonly Typeface _typeface;
    private string _text;
    private FormattedText? _white, _black;   // rebuilt lazily when the text changes

    public OutlinedText(string text, double fontSize, FontWeight weight = FontWeight.Normal)
    {
        _text = text;
        _fontSize = fontSize;
        _typeface = new Typeface(FontFamily.Default, FontStyle.Normal, weight);
        IsHitTestVisible = false;
    }

    public string Text
    {
        set
        {
            if (_text == value) return;
            _text = value;
            _white = _black = null;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private void Ensure()
    {
        if (_white != null) return;
        _white = Make(Brushes.White);
        _black = Make(Brushes.Black);
    }

    private FormattedText Make(IBrush brush) =>
        new(_text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, _fontSize, brush);

    protected override Size MeasureOverride(Size availableSize)
    {
        Ensure();
        return new Size(_white!.Width + 2, _white.Height + 2);   // +2 for the 1px outline each side
    }

    public override void Render(DrawingContext context)
    {
        Ensure();
        double x = (Bounds.Width - _white!.Width) / 2;
        double y = (Bounds.Height - _white.Height) / 2;
        foreach (var (dx, dy) in Offsets) context.DrawText(_black!, new Point(x + dx, y + dy));
        context.DrawText(_white, new Point(x, y));
    }
}

// One keyboard key (no control theme needed): themed background + press-highlight that
// fades out on release + an OutlinedText glyph. Raises Pressed/Released for the central
// long-press + tap logic in MainWindow.
internal sealed class Key : Panel
{
    public string KeyTag { get; }
    public string DefaultLabel { get; }   // ctor label; UpdateKeys restores it when no remap applies

    private readonly Border _bg;
    private readonly Border _highlight;
    private readonly OutlinedText _glyph;
    private IBrush? _themeBg;
    private IBrush? _override;

    public event Action<Key>? Pressed;
    public event Action<Key>? Released;

    public Key(string tag, string label, double fontSize = 22)
    {
        KeyTag = tag;
        DefaultLabel = label;
        Margin = new Thickness(3);

        _bg = new Border { CornerRadius = new CornerRadius(6) };
        _highlight = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new ImmutableSolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            Opacity = 0,
            Transitions = new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromSeconds(0.12) },
            },
        };
        _glyph = new OutlinedText(label, fontSize) { Margin = new Thickness(0, 0, 0, 3) };

        Children.Add(_bg);
        Children.Add(_highlight);
        Children.Add(_glyph);

        PointerPressed += OnPressed;
        PointerReleased += OnReleased;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Pointer.Capture(this);            // so release fires here even if the finger slips off
        _highlight.Opacity = 1;
        Pressed?.Invoke(this);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        _highlight.Opacity = 0;
        Released?.Invoke(this);
    }

    public void SetLabel(string s) => _glyph.Text = s;

    // Themed grey background (from ApplyTheme). Ignored while an override is active.
    public void SetThemeBackground(IBrush b)
    {
        _themeBg = b;
        if (_override == null) _bg.Background = b;
    }

    // Modifier highlight: accent/amber, or null to fall back to the themed background.
    public void SetOverride(IBrush? b)
    {
        _override = b;
        _bg.Background = b ?? _themeBg;
    }
}
