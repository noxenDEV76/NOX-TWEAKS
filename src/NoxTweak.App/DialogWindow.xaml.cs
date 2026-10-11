using System.Windows;
using System.Windows.Input;

namespace NoxTweak.App;

public partial class DialogWindow : Window
{
    private DialogWindow() { InitializeComponent(); }

    /// <summary>Shows a dark confirmation/info dialog. Returns true when the primary button was pressed.</summary>
    public static bool Ask(Window? owner, string title, string message, string primary, string? secondary, IReadOnlyList<string>? details)
    {
        var d = new DialogWindow();
        if (owner is { IsVisible: true }) d.Owner = owner;
        d.Title = title;
        d.TitleText.Text = title;
        d.MessageText.Text = message;
        d.PrimaryButtonControl.Content = primary;
        if (secondary is null) d.SecondaryButton.Visibility = Visibility.Collapsed;
        else d.SecondaryButton.Content = secondary;
        if (details is { Count: > 0 }) d.DetailsList.ItemsSource = details;
        else d.DetailsScroll.Visibility = Visibility.Collapsed;
        d.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Button) d.DragMove(); };
        return d.ShowDialog() == true;
    }

    private void OnPrimary(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnSecondary(object sender, RoutedEventArgs e) => DialogResult = false;
}
