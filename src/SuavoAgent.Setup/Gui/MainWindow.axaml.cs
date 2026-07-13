using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SuavoAgent.Setup.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OpenThirdPartyNotices(object? sender, RoutedEventArgs args)
    {
        var dialog = ThirdPartyNotices.CreateWindow();
        await dialog.ShowDialog(this);
    }
}
