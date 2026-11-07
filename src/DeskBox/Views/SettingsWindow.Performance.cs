using Microsoft.UI.Xaml;
using Windows.System;

namespace DeskBox.Views;

public sealed partial class SettingsWindow
{
    private async void OpenGraphicsSettingsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(
                    new Uri("ms-settings:display-advancedgraphics")))
            {
                App.Log(
                    "[Performance] Windows graphics settings could not be opened.");
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[Performance] Failed to open Windows graphics settings: {ex}");
        }
    }
}
