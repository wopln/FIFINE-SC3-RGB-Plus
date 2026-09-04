using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SC3RGBController.Services.Updates;

namespace SC3RGBController.UI;

public partial class SettingsView : UserControl
{
    public event EventHandler? RestoreRequested;
    public event EventHandler? BackRequested;
    public event EventHandler? CheckForUpdatesRequested;
    public event EventHandler? UpdateNowRequested;
    public event EventHandler? CancelDownloadRequested;
    public event EventHandler? InstallAndRestartRequested;
    public event EventHandler? AutomaticUpdateCheckChanged;

    private bool _syncingUpdateToggle;
    public bool AutomaticUpdateCheckEnabled => AutomaticUpdateCheckToggle.IsChecked == true;

    public SettingsView()
    {
        InitializeComponent();
        SelectTroubleshooting();
    }

    public void ConfigureUpdates(string currentVersion, bool automaticallyCheck)
    {
        CurrentVersionText.Text = currentVersion;
        _syncingUpdateToggle = true;
        AutomaticUpdateCheckToggle.IsChecked = automaticallyCheck;
        _syncingUpdateToggle = false;
    }

    public void SelectUpdates() => SelectSection(updates: true);

    public void SelectTroubleshooting()
    {
        SelectSection(updates: false);
    }

    public void ShowUpdateChecking()
    {
        UpdateStatusText.Text = "Checking...";
        CheckForUpdatesButton.IsEnabled = false;
        UpdateNowButton.Visibility = Visibility.Collapsed;
        UpdateReadyPanel.Visibility = Visibility.Collapsed;
    }

    public void ShowUpdateResult(UpdateCheckResult result)
    {
        CheckForUpdatesButton.IsEnabled = true;
        UpdateStatusText.Text = result.Message;
        UpdateNowButton.Visibility = result.Candidate is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateNowButton.IsEnabled = result.Candidate?.HasIntegrityMetadata == true;
        DownloadPanel.Visibility = Visibility.Collapsed;

        if (result.Status != UpdateCheckStatus.UpdateAvailable)
            UpdateReadyPanel.Visibility = Visibility.Collapsed;
    }

    public void ShowDownloadStarted()
    {
        UpdateDownloadProgress.Value = 0;
        DownloadPercentText.Text = "0%";
        DownloadStatusText.Text = "Downloading update...";
        DownloadPanel.Visibility = Visibility.Visible;
        UpdateReadyPanel.Visibility = Visibility.Collapsed;
        UpdateNowButton.IsEnabled = false;
        CheckForUpdatesButton.IsEnabled = false;
    }

    public void SetDownloadProgress(int percent)
    {
        int value = Math.Clamp(percent, 0, 100);
        UpdateDownloadProgress.Value = value;
        DownloadPercentText.Text = $"{value}%";
    }

    public void ShowUpdateReady(SemanticVersion version)
    {
        DownloadPanel.Visibility = Visibility.Collapsed;
        CheckForUpdatesButton.IsEnabled = true;
        UpdateNowButton.IsEnabled = true;
        UpdateReadyDetailText.Text = $"FIFINE SC3 RGB+ v{version} is verified and ready.";
        UpdateReadyPanel.Visibility = Visibility.Visible;
        UpdateStatusText.Text = $"v{version} ready to install";
    }

    public void ShowUpdateError(string message)
    {
        DownloadPanel.Visibility = Visibility.Collapsed;
        UpdateReadyPanel.Visibility = Visibility.Collapsed;
        CheckForUpdatesButton.IsEnabled = true;
        UpdateNowButton.IsEnabled = true;
        UpdateStatusText.Text = message;
    }

    public void ShowInstalling()
    {
        DownloadPanel.Visibility = Visibility.Collapsed;
        UpdateReadyPanel.Visibility = Visibility.Visible;
        UpdateReadyDetailText.Text = "Closing RGB activity and starting the verified installer...";
        UpdateStatusText.Text = "Installing update...";
        CheckForUpdatesButton.IsEnabled = false;
        UpdateNowButton.IsEnabled = false;
    }

    private void SelectSection(bool updates)
    {
        UpdatesContent.Visibility = updates ? Visibility.Visible : Visibility.Collapsed;
        TroubleshootingContent.Visibility = updates ? Visibility.Collapsed : Visibility.Visible;
        ApplyNavigationState(UpdatesNavigationButton, updates);
        ApplyNavigationState(TroubleshootingNavigationButton, !updates);
    }

    private void ApplyNavigationState(Button button, bool selected)
    {
        Brush active = (Brush)FindResource("ConnectedBrush");
        button.BorderBrush = selected ? active : new SolidColorBrush(Color.FromRgb(52, 52, 52));
        button.Background = selected ? new SolidColorBrush(Color.FromRgb(21, 27, 23)) : new SolidColorBrush(Color.FromRgb(17, 17, 17));
    }

    private void UpdatesNavigationButton_Click(object sender, RoutedEventArgs e) => SelectUpdates();
    private void TroubleshootingNavigationButton_Click(object sender, RoutedEventArgs e) => SelectTroubleshooting();

    private void BackButton_Click(object sender, RoutedEventArgs e) =>
        BackRequested?.Invoke(this, EventArgs.Empty);

    private void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e) =>
        CheckForUpdatesRequested?.Invoke(this, EventArgs.Empty);

    private void UpdateNowButton_Click(object sender, RoutedEventArgs e) =>
        UpdateNowRequested?.Invoke(this, EventArgs.Empty);

    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e) =>
        CancelDownloadRequested?.Invoke(this, EventArgs.Empty);

    private void InstallAndRestartButton_Click(object sender, RoutedEventArgs e) =>
        InstallAndRestartRequested?.Invoke(this, EventArgs.Empty);

    private void ReadyLaterButton_Click(object sender, RoutedEventArgs e) =>
        UpdateReadyPanel.Visibility = Visibility.Collapsed;

    private void AutomaticUpdateCheckToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingUpdateToggle) return;
        AutomaticUpdateCheckChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e) =>
        RestoreRequested?.Invoke(this, EventArgs.Empty);
}
