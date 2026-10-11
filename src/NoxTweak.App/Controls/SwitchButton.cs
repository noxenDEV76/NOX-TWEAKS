using System.Windows;
using System.Windows.Controls;

namespace NoxTweak.App.Controls;

/// <summary>
/// A button that carries an On/Off state set only from the view-model. Because clicking it never flips the
/// state by itself, a switch can only show "on" once the underlying operation really succeeded.
/// </summary>
public class SwitchButton : Button
{
    public static readonly DependencyProperty IsOnProperty =
        DependencyProperty.Register(nameof(IsOn), typeof(bool), typeof(SwitchButton), new PropertyMetadata(false));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SwitchButton), new PropertyMetadata(""));

    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }
}
