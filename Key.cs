using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace DesktopKeyboard;

// One keyboard key, drawn from Borders + TextBlocks (no control theme needed):
//   themed background  +  press-highlight (fades out on release)  +  white glyph with a
//   1px black outline (four offset copies, no shader effect).
// Raises Pressed/Released for the central long-press + tap logic in MainWindow.
internal sealed class Key : Panel
{
    public string KeyTag { get; }

    private readonly Border _bg;
    private readonly Border _highlight;
    private readonly TextBlock[] _outline = new TextBlock[4];
    private readonly TextBlock _text;
    private IBrush? _themeBg;
    private IBrush? _override;

    public event Action<Key>? Pressed;
    public event Action<Key>? Released;

    private static readonly (double X, double Y)[] Offsets = { (-1, -1), (1, -1), (-1, 1), (1, 1) };

    public Key(string tag, string label, double fontSize = 22)
    {
        KeyTag = tag;
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

        Children.Add(_bg);
        Children.Add(_highlight);

        for (int i = 0; i < 4; i++)
        {
            _outline[i] = MakeGlyph(label, fontSize, Brushes.Black);
            _outline[i].RenderTransform = new TranslateTransform(Offsets[i].X, Offsets[i].Y);
            Children.Add(_outline[i]);
        }
        _text = MakeGlyph(label, fontSize, Brushes.White);
        Children.Add(_text);

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

    private static TextBlock MakeGlyph(string label, double fontSize, IBrush fg) => new()
    {
        Text = label,
        FontSize = fontSize,
        Foreground = fg,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 0, 3),
        IsHitTestVisible = false,
    };

    public void SetLabel(string s)
    {
        _text.Text = s;
        foreach (var t in _outline) t.Text = s;
    }

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
