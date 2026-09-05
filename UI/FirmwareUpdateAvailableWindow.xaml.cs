using System.Windows;

namespace SC3RGBController.UI;

public partial class FirmwareUpdateAvailableWindow : Window
{
    public FirmwareUpdateAvailableWindow() => InitializeComponent();

    private void LaterButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void UpdateButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}