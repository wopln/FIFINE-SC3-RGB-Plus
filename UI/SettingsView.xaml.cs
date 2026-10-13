using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SC3RGBController.Models;
using SC3RGBController.Services;
using SC3RGBController.Services.Actions;
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
    private const int GeneralSection = 0;
    private const int CustomButtonsSection = 1;
    private const int UpdatesSection = 2;
    private const int TroubleshootingSection = 3;

    public event EventHandler? RestoreRequested;
    public event EventHandler? BackRequested;
    public event EventHandler? CheckForUpdatesRequested;
    public event EventHandler? UpdateNowRequested;
    public event EventHandler? CancelDownloadRequested;
    public event EventHandler? InstallAndRestartRequested;
    public event EventHandler? AutomaticUpdateCheckChanged;
    public event EventHandler? CustomShortcutPreferenceChanged;
    public event EventHandler? FirmwareUpdateRequested;
    public event EventHandler? StartWithWindowsChanged;
    public event EventHandler<CustomAppChosenEventArgs>? CustomAppChosen;
    public event EventHandler<CustomButtonEventArgs>? CustomAppCleared;

    private bool _syncingUpdateToggle;
    private bool _syncingCustomToggle;
    private bool _syncingStartupToggle;
    private bool _firmwareDeferred;
    private bool _restoreFocusToCheckButton;
    private string? _lastFirmwareFingerprint;

    public bool AutomaticUpdateCheckEnabled => AutomaticUpdateCheckToggle.IsChecked == true;
    public bool CustomShortcutsEnabled => CustomShortcutsToggle.IsChecked == true;
    public bool StartWithWindowsEnabled => StartWithWindowsToggle.IsChecked == true;

    private RadioButton[] NavigationItems =>
    [
        GeneralNavigationButton,
        CustomButtonsNavigationButton,
        UpdatesNavigationButton,
        TroubleshootingNavigationButton
    ];

    public SettingsView()
    {
        InitializeComponent();
        SelectGeneral();
    }

    /// <summary>Sets the Start with Windows toggle without raising <see cref="StartWithWindowsChanged"/>.</summary>
    public void SetStartWithWindows(bool enabled)
    {
        _syncingStartupToggle = true;
        StartWithWindowsToggle.IsChecked = enabled;
        _syncingStartupToggle = false;
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
        string text = string.IsNullOrWhiteSpace(name) ? "Not assigned" : name;
        target.Text = text;
        // Pair the assignment with its row label so screen readers read e.g. "Custom A: Notepad".
        AutomationProperties.SetName(target, $"Custom {button}: {text}");
    }

    public void SetCustomRuntimeState(CustomShortcutRuntimeState state, string? message = null) =>
        SetLiveText(CustomShortcutStatusText, message ?? CustomShortcutStatusPolicy.For(state));

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
        SetLiveText(MixerFirmwareStatusText, presentation.Status);
        MixerFirmwareDescriptionText.Text = presentation.Description;
        bool showActions = presentation.UpdateAvailable && !_firmwareDeferred;
        if (!showActions) MoveFocusOutOf(MixerFirmwareActionPanel, CheckForUpdatesButton);
        MixerFirmwareActionPanel.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SelectGeneral() => SelectSection(GeneralSection);
    public void SelectCustomButtons() => SelectSection(CustomButtonsSection);
    public void SelectUpdates() => SelectSection(UpdatesSection);
    public void SelectTroubleshooting() => SelectSection(TroubleshootingSection);

    private void SelectSection(int section)
    {
        GeneralContent.Visibility = section == GeneralSection ? Visibility.Visible : Visibility.Collapsed;
        CustomButtonsContent.Visibility = section == CustomButtonsSection ? Visibility.Visible : Visibility.Collapsed;
        UpdatesContent.Visibility = section == UpdatesSection ? Visibility.Visible : Visibility.Collapsed;
        TroubleshootingContent.Visibility = section == TroubleshootingSection ? Visibility.Visible : Visibility.Collapsed;

        // Selection state is the radio button's IsChecked (styled by NavItem, announced as "selected").
        // Setting IsChecked on an already-checked item raises no Checked event, so this cannot recurse.
        RadioButton selected = NavigationItems[section];
        if (selected.IsChecked != true) selected.IsChecked = true;
        SectionScrollViewer.ScrollToTop();
    }

    public void ShowUpdateChecking()
    {
        LatestVersionText.Text = "Checking...";
        SetLiveText(UpdateStatusText, "Checking...");
        // Disabling a focused button drops keyboard focus; remember to put it back when the check ends.
        _restoreFocusToCheckButton = CheckForUpdatesButton.IsKeyboardFocused;
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
        SetLiveText(UpdateStatusText, result.Message, force: true);
        UpdateNowButton.Visibility = result.Candidate is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateNowButton.IsEnabled = result.Candidate?.HasIntegrityMetadata == true;
        MoveFocusOutOf(DownloadPanel, CheckForUpdatesButton);
        DownloadPanel.Visibility = Visibility.Collapsed;
        if (result.Status != UpdateCheckStatus.UpdateAvailable)
        {
            MoveFocusOutOf(UpdateReadyPanel, CheckForUpdatesButton);
            UpdateReadyPanel.Visibility = Visibility.Collapsed;
        }
        RestoreCheckButtonFocus();
    }

    public void ShowDownloadStarted()
    {
        UpdateDownloadProgress.Value = 0;
        DownloadPercentText.Text = "0%";
        bool moveFocusToCancel = UpdateNowButton.IsKeyboardFocused || UpdateReadyPanel.IsKeyboardFocusWithin;
        DownloadPanel.Visibility = Visibility.Visible;
        SetLiveText(DownloadStatusText, "Downloading update...", force: true);
        UpdateReadyPanel.Visibility = Visibility.Collapsed;
        UpdateNowButton.IsEnabled = false;
        CheckForUpdatesButton.IsEnabled = false;
        if (moveFocusToCancel) FocusWhenReady(CancelDownloadButton);
    }

    public void SetDownloadProgress(int percent)
    {
        // The ProgressBar exposes its value to assistive technology; no per-tick announcements.
        int value = Math.Clamp(percent, 0, 100);
        UpdateDownloadProgress.Value = value;
        DownloadPercentText.Text = $"{value}%";
    }

    public void ShowUpdateReady(SemanticVersion version)
    {
        LatestVersionText.Text = $"v{version}";
        bool focusWasInDownload = DownloadPanel.IsKeyboardFocusWithin;
        DownloadPanel.Visibility = Visibility.Collapsed;
        CheckForUpdatesButton.IsEnabled = true;
        UpdateNowButton.IsEnabled = true;
        UpdateReadyDetailText.Text = $"FIFINE SC3 RGB+ v{version} is verified and ready.";
        UpdateReadyPanel.Visibility = Visibility.Visible;
        SetLiveText(UpdateStatusText, $"v{version} ready to install");
        if (focusWasInDownload) FocusWhenReady(InstallAndRestartButton);
    }

    public void ShowUpdateError(string message)
    {
        MoveFocusOutOf(DownloadPanel, CheckForUpdatesButton);
        DownloadPanel.Visibility = Visibility.Collapsed;
        MoveFocusOutOf(UpdateReadyPanel, CheckForUpdatesButton);
        UpdateReadyPanel.Visibility = Visibility.Collapsed;
        CheckForUpdatesButton.IsEnabled = true;
        UpdateNowButton.IsEnabled = true;
        SetLiveText(UpdateStatusText, message, force: true);
        RestoreCheckButtonFocus();
    }

    public void ShowInstalling()
    {
        DownloadPanel.Visibility = Visibility.Collapsed;
        UpdateReadyPanel.Visibility = Visibility.Visible;
        UpdateReadyDetailText.Text = "Closing RGB activity and starting the verified installer...";
        SetLiveText(UpdateStatusText, "Installing update...");
        CheckForUpdatesButton.IsEnabled = false;
        UpdateNowButton.IsEnabled = false;
    }

    // ---------- Keyboard ----------

    // Escape leaves Settings (the main window decides where to go).
    private void SettingsView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || BackRequested is null) return;
        BackRequested.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    // Up/Down move through the section list and select it (selection follows focus), wrapping at the ends.
    private void SettingsNavigationPanel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down)) return;
        RadioButton[] items = NavigationItems;
        int current = Array.FindIndex(items, item => item.IsKeyboardFocused);
        if (current < 0) return;
        int next = (current + (e.Key == Key.Down ? 1 : -1) + items.Length) % items.Length;
        items[next].IsChecked = true;
        items[next].Focus();
        e.Handled = true;
    }

    private void GeneralNavigationButton_Checked(object sender, RoutedEventArgs e) => SelectGeneral();
    private void CustomButtonsNavigationButton_Checked(object sender, RoutedEventArgs e) => SelectCustomButtons();
    private void UpdatesNavigationButton_Checked(object sender, RoutedEventArgs e) => SelectUpdates();
    private void TroubleshootingNavigationButton_Checked(object sender, RoutedEventArgs e) => SelectTroubleshooting();

    // ---------- Actions ----------

    private void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e) => CheckForUpdatesRequested?.Invoke(this, EventArgs.Empty);
    private void UpdateNowButton_Click(object sender, RoutedEventArgs e) => UpdateNowRequested?.Invoke(this, EventArgs.Empty);
    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e) => CancelDownloadRequested?.Invoke(this, EventArgs.Empty);
    private void InstallAndRestartButton_Click(object sender, RoutedEventArgs e) => InstallAndRestartRequested?.Invoke(this, EventArgs.Empty);

    private void ReadyLaterButton_Click(object sender, RoutedEventArgs e)
    {
        MoveFocusOutOf(UpdateReadyPanel, UpdateNowButton.IsVisible && UpdateNowButton.IsEnabled ? UpdateNowButton : CheckForUpdatesButton);
        UpdateReadyPanel.Visibility = Visibility.Collapsed;
    }

    private void AutomaticUpdateCheckToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncingUpdateToggle) AutomaticUpdateCheckChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartWithWindowsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncingStartupToggle) StartWithWindowsChanged?.Invoke(this, EventArgs.Empty);
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
        MoveFocusOutOf(MixerFirmwareActionPanel, CheckForUpdatesButton);
        MixerFirmwareActionPanel.Visibility = Visibility.Collapsed;
        MixerFirmwareDescriptionText.Text += " You can update later from this page.";
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e) => RestoreRequested?.Invoke(this, EventArgs.Empty);

    private void ChooseCustomApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !Enum.TryParse(button.Tag?.ToString(), out CustomButtonId id)) return;
        CustomButtonAction current = CustomButtonAction.Parse(CurrentActionProvider?.Invoke(id));
        CustomActionPickerWindow picker = new($"Custom {id}", current, AudioAppsProvider)
        {
            Owner = Window.GetWindow(this)
        };
        if (picker.ShowDialog() != true) { button.Focus(); return; }

        if (picker.SelectedAction.Kind == CustomActionKind.None)
            CustomAppCleared?.Invoke(this, new(id));
        else
            CustomAppChosen?.Invoke(this, new(id, picker.SelectedAction.Serialize(), picker.SelectedDisplayName));
        button.Focus();
    }

    /// <summary>Returns the stored action string for a button (set by the host window).</summary>
    public Func<CustomButtonId, string?>? CurrentActionProvider { get; set; }

    /// <summary>Apps currently playing audio, for the picker's App audio list (set by the host window).</summary>
    public Func<IReadOnlyList<(string ProcessName, string DisplayName)>>? AudioAppsProvider { get; set; }

    private void ClearCustomApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && Enum.TryParse(button.Tag?.ToString(), out CustomButtonId id))
            CustomAppCleared?.Invoke(this, new(id));
    }

    // ---------- Accessibility helpers ----------

    /// <summary>Sets status text and announces it through the element's live region when it changes (or when forced).</summary>
    private static void SetLiveText(TextBlock target, string text, bool force = false)
    {
        bool changed = !string.Equals(target.Text, text, StringComparison.Ordinal);
        target.Text = text;
        if (changed || force) Announce(target);
    }

    /// <summary>Raises LiveRegionChanged so Narrator/NVDA read the element's current text.
    /// The element must have AutomationProperties.LiveSetting set (done in XAML).</summary>
    private static void Announce(FrameworkElement element)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        AutomationPeer? peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    /// <summary>If keyboard focus is inside a panel that is about to be hidden, move it to a sensible target so focus is not lost.</summary>
    private static void MoveFocusOutOf(FrameworkElement panel, Control target)
    {
        if (panel.IsKeyboardFocusWithin) FocusWhenReady(target);
    }

    private static void FocusWhenReady(Control target) =>
        target.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (target.IsVisible && target.IsEnabled) target.Focus();
        }));

    private void RestoreCheckButtonFocus()
    {
        if (!_restoreFocusToCheckButton) return;
        _restoreFocusToCheckButton = false;
        FocusWhenReady(CheckForUpdatesButton);
    }
}
