using System.Windows;

namespace SC3RGBController.UI;

public partial class RecoveryModeWindow : Window
{
    public RecoveryModeWindow() => InitializeComponent();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void RestoreButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
