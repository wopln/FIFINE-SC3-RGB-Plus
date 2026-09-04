using System.Windows;

namespace SC3RGBController.UI;

public partial class RestoreConfirmationWindow : Window
{
    public RestoreConfirmationWindow() => InitializeComponent();
    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void RestoreButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
