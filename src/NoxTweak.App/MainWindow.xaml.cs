using System.Windows;

namespace NoxTweak.App;

public partial class MainWindow : Window
{
    public MainWindow() { InitializeComponent(); }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // A chrome-less maximized window overhangs the screen by its resize border; pull it back in.
        var max = WindowState == WindowState.Maximized;
        RootBorder.Padding = max ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaxButton.Content = max ? "" : "";
    }
}
