using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SC3RGBController.Models;
using SC3RGBController.Services;
using SC3RGBController.Services.Updates;

namespace SC3RGBController.UI;

public sealed class CustomAppChosenEventArgs(CustomButtonId button, string path, string name) : EventArgs
{
    public CustomButtonId Button { get; } = button;
    public string Path { get; } = path;
    public string Name { get; } = name;
}

public sealed class CustomButtonEventArgs(CustomButtonId button) : EventArgs
{
    public CustomButtonId Button { get; } = button;
}

public partial class SettingsView : UserControl
{
    public event EventHandler? RestoreRequested;
    public event EventHandler? BackRequested;
    public event EventHandler? CheckForUpdatesRequested;
    public event EventHandler? UpdateNowRequested;
    public event EventHandler? CancelDownloadRequested;
    public event EventHandler? InstallAndRestartRequested;
    public event EventHandler? AutomaticUpdateCheckChanged;
    public event EventHandler? CustomShortcutPreferenceChanged;
    public event EventHandler? FirmwareUpdateRequested;
    public event EventHandler<CustomAppChosenEventArgs>? CustomAppChosen;
    public event EventHandler<CustomButtonEventArgs>? CustomAppCleared;

    private bool _syncingUpdateToggle;
    private bool _syncingCustomToggle;
    private bool _firmwareDeferred;
    private string? _lastFirmwareFingerprint;

    public bool AutomaticUpdateCheckEnabled => AutomaticUpdateCheckToggle.IsChecked == true;
    public bool CustomShortcutsEnabled => CustomShortcutsToggle.IsChecked == true;

    public SettingsView()
    {
        InitializeComponent();
        SelectCustomButtons();
    }

    public void ConfigureUpdates(string currentVersion, bool automaticallyCheck)
    {
        CurrentVersionText.Text = currentVersion;
        LatestVersionText.Text = "Not checked";
        _syncingUpdateToggle = true;
        AutomaticUpdateCheckToggle.IsChecked = automaticallyCheck;
        _syncingUpdateToggle = false;
    }

    public void ConfigureCustomButtons(AppSettings settings)
    {
        _syncingCustomToggle = true;
        CustomShortcutsToggle.IsChecked = settings.CustomShortcutsEnabled;
        _syncingCustomToggle = false;
        CustomBackgroundNoteText.Visibility = settings.CustomShortcutsEnabled ? Visibility.Visible : Visibility.Collapsed;
        SetCustomAssignment(CustomButtonId.A, settings.CustomAName);
        SetCustomAssignment(CustomButtonId.B, settings.CustomBName);
        SetCustomAssignment(CustomButtonId.C, settings.CustomCName);
        SetCustomAssignment(CustomButtonId.D, settings.CustomDName);
        SetCustomRuntimeState(settings.CustomShortcutsEnabled ? CustomShortcutRuntimeState.Unavailable : CustomShortcutRuntimeState.Stock);
    }

    public void SetCustomAssignment(CustomButtonId button, string? name)
    {
        TextBlock target = button switch
        {
            CustomButtonId.A => CustomANameText,
            CustomButtonId.B => CustomBNameText,
            CustomButtonId.C => CustomCNameText,
            _ => CustomDNameText
        };
        target.Text = string.IsNullOrWhiteSpace(name) ? "Not assigned" : name;
    }

    public void SetCustomRuntimeState(CustomShortcutRuntimeState state, string? message = null) =>
        CustomShortcutStatusText.Text = message ?? CustomShortcutStatusPolicy.For(state);

    public void SetCustomPreference(bool enabled)
    {
        _syncingCustomToggle = true;
        CustomShortcutsToggle.IsChecked = enabled;
        CustomBackgroundNoteText.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        _syncingCustomToggle = false;
    }

    public void SetCustomFirmwareAvailability(bool available, bool preferredEnabled, string? requirementMessage = null)
    {
        _syncingCustomToggle = true;
        CustomShortcutsToggle.IsEnabled = available;
        CustomShortcutsToggle.IsChecked = available && preferredEnabled;
        CustomBackgroundNoteText.Visibility = available && preferredEnabled ? Visibility.Visible : Visibility.Collapsed;
        _syncingCustomToggle = false;

        CustomFirmwareRequirementPanel.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        if (!available)
        {
            CustomFirmwareRequirementText.Text = string.IsNullOrWhiteSpace(requirementMessage)
                ? "Firmware 1.5 is required for Custom Button Shortcuts."
                : requirementMessage;
            SetCustomRuntimeState(CustomShortcutRuntimeState.Unavailable, CustomFirmwareRequirementText.Text);
        }
    }

    public void SetMixerFirmwarePresentation(MixerFirmwarePresentation presentation)
    {
        string fingerprint = $"{presentation.Current}|{presentation.Status}|{presentation.UpdateAvailable}";
        if (!string.Equals(_lastFirmwareFingerprint, fingerprint, StringComparison.Ordinal))
        {
            _lastFirmwareFingerprint = fingerprint;
            _firmwareDeferred = false;
        }

        MixerFirmwareCurrentText.Text = presentation.Current;
        MixerFirmwareLatestText.Text = presentation.Latest;
        MixerFirmwareStatusText.Text = presentation.Status;
        MixerFirmwareDescriptionText.Text = presentation.Description;
        MixerFirmwareActionPanel.Visibility = presentation.UpdateAvailable && !_firmwareDeferred
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public void SelectCustomButtons() => SelectSection(0);
    public void SelectUpdates() => SelectSection(1);
    public void SelectTroubleshooting() => SelectSection(2);

    private void SelectSection(int section)
    {
        CustomButtonsContent.Visibility = section == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatesContent.Visibility = section == 1 ? Visibility.Visible : Visibility.Collapsed;
        TroubleshootingContent.Visibility = section == 2 ? Visibility.Visible : Visibility.Collapsed;
        ApplyNavigationState(CustomButtonsNavigationButton, section == 0);
        ApplyNavigationState(UpdatesNavigationButton, section == 1);
        ApplyNavigationState(TroubleshootingNavigationButton, section == 2);
    }

    private void ApplyNavigationState(Button button, bool selected)
    {
        Brush active = (Brush)FindResource("ConnectedBrush");
        button.BorderBrush = selected ? active : new SolidColorBrush(Color.FromRgb(52, 52, 52));
        button.Background = selected ? new SolidColorBrush(Color.FromRgb(21, 27, 23)) : new SolidColorBrush(Color.FromRgb(17, 17, 17));
    }

    public void ShowUpdateChecking()
    {
        LatestVersionText.Text = "Checking...";
        UpdateStatusText.Text = "Checking...";
        CheckForUpdatesButton.IsEnabled = false;
        UpdateNowButton.Visibility = Visibility.Collapsed;
        UpdateReadyPanel.Visibility = Visibility.Collapsed;
    }

    public void ShowUpdateResult(UpdateCheckResult result)
    {
        CheckForUpdatesButton.IsEnabled = true;
        LatestVersionText.Text = result.Candidate is not null
            ? $"v{result.Candidate.Version}"
            : result.Status == UpdateCheckStatus.UpToDate
                ? CurrentVersionText.Text
                : "Unknown";
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
        LatestVersionText.Text = $"v{version}";
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

    private void CustomButtonsNavigationButton_Click(object sender, RoutedEventArgs e) => SelectCustomButtons();
    private void UpdatesNavigationButton_Click(object sender, RoutedEventArgs e) => SelectUpdates();
    private void TroubleshootingNavigationButton_Click(object sender, RoutedEventArgs e) => SelectTroubleshooting();
    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
    private void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e) => CheckForUpdatesRequested?.Invoke(this, EventArgs.Empty);
    private void UpdateNowButton_Click(object sender, RoutedEventArgs e) => UpdateNowRequested?.Invoke(this, EventArgs.Empty);
    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e) => CancelDownloadRequested?.Invoke(this, EventArgs.Empty);
    private void InstallAndRestartButton_Click(object sender, RoutedEventArgs e) => InstallAndRestartRequested?.Invoke(this, EventArgs.Empty);
    private void ReadyLaterButton_Click(object sender, RoutedEventArgs e) => UpdateReadyPanel.Visibility = Visibility.Collapsed;
    private void AutomaticUpdateCheckToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncingUpdateToggle) AutomaticUpdateCheckChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CustomShortcutsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingCustomToggle) return;
        CustomBackgroundNoteText.Visibility = CustomShortcutsEnabled ? Visibility.Visible : Visibility.Collapsed;
        CustomShortcutPreferenceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateFirmwareButton_Click(object sender, RoutedEventArgs e)
    {
        _firmwareDeferred = false;
        FirmwareUpdateRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CustomFirmwareUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        _firmwareDeferred = false;
        SelectUpdates();
        FirmwareUpdateRequested?.Invoke(this, EventArgs.Empty);
    }

    private void FirmwareLaterButton_Click(object sender, RoutedEventArgs e)
    {
        _firmwareDeferred = true;
        MixerFirmwareActionPanel.Visibility = Visibility.Collapsed;
        MixerFirmwareDescriptionText.Text += " You can update later from this page.";
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e) => RestoreRequested?.Invoke(this, EventArgs.Empty);

    private void ChooseCustomApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !Enum.TryParse(button.Tag?.ToString(), out CustomButtonId id)) return;
        OpenFileDialog dialog = new()
        {
            Title = $"Choose application for Custom {id}",
            Filter = "Applications (*.exe;*.lnk)|*.exe;*.lnk",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
            CustomAppChosen?.Invoke(this, new(id, dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName)));
    }

    private void ClearCustomApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && Enum.TryParse(button.Tag?.ToString(), out CustomButtonId id))
            CustomAppCleared?.Invoke(this, new(id));
    }
}