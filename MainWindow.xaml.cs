using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using SC3RGBController.Models;
using SC3RGBController.Services;
using SC3RGBController.Services.Updates;
using SC3RGBController.Services.Actions;
using SC3RGBController.UI.Controls;
using SC3RGBController.UI;
using SC3FirmwareTool.Core;

namespace SC3RGBController;

public partial class MainWindow : Window
{
    private enum MainPage
    {
        Lighting,
        Settings
    }

    private MainPage _currentPage = MainPage.Lighting;
    private readonly HidDeviceClient _hid = new();
    private readonly CustomButtonShortcutController _customButtons;
    private readonly TrayCommandRouter _trayCommands = new();
    private readonly SystemTrayService _tray;
    private readonly FirmwareService _firmwareService = new();
    private readonly Services.Audio.AudioControlService _audio = new();
    private readonly ApplicationUpdateService _applicationUpdateService = ApplicationUpdateService.CreateDefault(AppVersionInfo.Current);
    private readonly object _colorGate = new();
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly DispatcherTimer _liveApplyTimer;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<ColorPreset> _presets;
    private CancellationTokenSource? _streamCancellation;
    private CancellationTokenSource? _applyFeedbackCancellation;
    private CancellationTokenSource? _updateDownloadCancellation;
    private Task? _streamTask;
    private Color _selectedColor = Color.FromRgb(255, 120, 0);
    private Color _appliedColor = Color.FromRgb(255, 120, 0);
    private LightingEffect _selectedEffect = LightingEffect.Static;
    private DateTime _effectStartedAt = DateTime.UtcNow;
    private bool _syncingFields;
    private bool _syncingEffectSpeed;
    private bool _isStreaming;
    private bool _isConnected;
    private bool _liveApplyPending;
    private bool _syncingNavigation;
    private string? _lastStreamFailure;
    private bool _syncingEffect;
    private bool _syncingPresetName;
    private (ColorPreset Preset, int Index, bool WasSelected)? _lastDeletedPreset;
    private CancellationTokenSource? _undoCancellation;
    private bool _suppressPresetDirty;
    private bool _stopRestoredStockMode;
    private bool _firmwareChecked;
    private bool _modInstalled;
    private DeviceStatus? _firmwareStatus;
    private Sc3FirmwareFlavor _firmwareFlavor = Sc3FirmwareFlavor.Unknown;
    private bool _finalMod15Capability;
    private bool _firmwareUpdateOfferShown;
    private bool _firmwareOperationActive;
    private bool _recoveryModeDetected;
    private bool _recoveryPromptShown;
    private bool _updateCheckInProgress;
    private UpdateCandidate? _availableUpdate;
    private DownloadedUpdate? _downloadedUpdate;
    private bool _applicationUpdateInstalling;
    private string? _selectedPresetId;
    private bool _backgroundMode;
    private bool _allowWindowClose;
    private bool _exitInProgress;
    private readonly bool _isStartupLaunch;
    private bool _restoreStockModeForStability;

    public MainWindow(bool? isStartupLaunch = null)
    {
        _isStartupLaunch = isStartupLaunch ?? Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));
        InitializeComponent();
        _settings = SettingsStore.Load();
        _restoreStockModeForStability = _settings.CustomShortcutsEnabled &&
            !CustomShortcutStabilityPolicy.EventDrivenTransportEnabled;
        if (_restoreStockModeForStability)
        {
            _settings.CustomShortcutsEnabled = false;
            SettingsStore.Save(_settings);
        }
        UpdateStartupRegistration();
        AppVersionText.Text = $"App version {AppVersionInfo.Current}";
        IntegratedSettingsView.ConfigureUpdates(AppVersionInfo.DisplayVersion, _settings.AutomaticallyCheckForUpdates);
        DescribeLegacyCustomAssignments();
        IntegratedSettingsView.ConfigureCustomButtons(_settings);
        IntegratedSettingsView.CurrentActionProvider = GetCustomButtonPath;
        IntegratedSettingsView.AudioAppsProvider = () => _audio.GetActiveSessions()
            .Select(s => (s.ProcessName, s.DisplayName)).ToList();
        _ = Services.Apps.InstalledAppCatalog.Shared.GetAppsAsync(); // warm the picker's app list
        _customButtons = new CustomButtonShortcutController(_hid, GetCustomButtonPath);
        _customButtons.StateChanged += CustomButtons_StateChanged;
        _customButtons.ActionStatus += (_, message) => Dispatcher.BeginInvoke(() => ShowInlineStatus(message));
        _tray = new SystemTrayService(_trayCommands);
        _trayCommands.OpenRequested += Tray_OpenRequested;
        _trayCommands.DisableShortcutsRequested += Tray_DisableShortcutsRequested;
        _trayCommands.ExitRequested += Tray_ExitRequested;
        IntegratedSettingsView.CustomShortcutPreferenceChanged += IntegratedSettingsView_CustomShortcutPreferenceChanged;
        IntegratedSettingsView.CustomAppChosen += IntegratedSettingsView_CustomAppChosen;
        IntegratedSettingsView.CustomAppCleared += IntegratedSettingsView_CustomAppCleared;
        IntegratedSettingsView.FirmwareUpdateRequested += IntegratedSettingsView_FirmwareUpdateRequested;
        _selectedEffect = ParseLightingEffect(_settings.Effect);
        _settings.Effect = _selectedEffect.ToString();
        EnsureEditablePresets();
        _presets = new ObservableCollection<ColorPreset>(_settings.Presets.OrderBy(p => p.Order));
        PresetItemsControl.ItemsSource = _presets;
        _selectedPresetId = _settings.SelectedPresetId;
        if (string.IsNullOrWhiteSpace(_selectedPresetId) && !string.IsNullOrWhiteSpace(_settings.SelectedPresetName))
        {
            _selectedPresetId = _presets.FirstOrDefault(p =>
                string.Equals(p.DisplayName, _settings.SelectedPresetName, StringComparison.OrdinalIgnoreCase))?.Id;
        }

        if (_isStartupLaunch)
        {
            WindowState = WindowState.Minimized;
        }

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += async (_, _) => await RefreshDeviceStatusAsync();

        _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _settingsSaveTimer.Tick += (_, _) => SaveSettingsNow();

        // Always-on live application: rapid changes are coalesced to the newest state
        // every 40 ms, then emitted through the pre-existing guarded 30 Hz stream.
        _liveApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _liveApplyTimer.Tick += (_, _) =>
        {
            if (_firmwareOperationActive)
            {
                _liveApplyPending = false;
                _liveApplyTimer.Stop();
                return;
            }

            if (!_liveApplyPending)
            {
                _liveApplyTimer.Stop();
                return;
            }

            _liveApplyPending = false;
            if (_isConnected && _firmwareChecked && _modInstalled && _settings.IsLightingEnabled)
            {
                ApplySelectedColor();
            }
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {

        _syncingFields = true;
        BrightnessSlider.Value = Math.Clamp(_settings.Brightness, 0, 100);
        IntegratedSettingsView.SetStartWithWindows(_settings.StartWithWindows);
        _syncingFields = false;

        if (!TrySetFromHex(_settings.LastHex, false))
        {
            Color fallback = Color.FromRgb(
                (byte)Math.Clamp(_settings.Red, 0, 255),
                (byte)Math.Clamp(_settings.Green, 0, 255),
                (byte)Math.Clamp(_settings.Blue, 0, 255));
            SetSelectedColor(fallback, false);
        }

        BrightnessText.Text = $"{(int)BrightnessSlider.Value}%";
        RefreshEffectVisualState();
        RefreshPresetVisualState();
        UpdateLightingVisual();
        ShowMainPage(MainPage.Lighting);
        SaveSettingsNow();

        _statusTimer.Start();
        await RefreshDeviceStatusAsync();
        if (_settings.CustomShortcutsEnabled)
            await _customButtons.SetPreferenceAsync(true);
        else
            IntegratedSettingsView.SetCustomRuntimeState(CustomShortcutRuntimeState.Stock);
        if (_settings.AutomaticallyCheckForUpdates)
            _ = CheckForApplicationUpdatesAsync(userInitiated: false);

        if (_isStartupLaunch && _settings.CustomShortcutsEnabled)
            await EnterBackgroundModeAsync();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowWindowClose) return;
        if (App.IsSessionEnding)
        {
            SaveSettingsNow();
            _tray.Dispose();
            return;
        }

        e.Cancel = true;
        if (CustomShortcutHostPolicy.KeepRunningOnWindowClose(_settings.CustomShortcutsEnabled))
        {
            await EnterBackgroundModeAsync();
            return;
        }

        await ExitApplicationAsync();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        // A maximized WindowChrome window extends past the monitor by the resize border.
        WindowFrame.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaximizeButton.Content = maximized ? "" : "";
        string label = maximized ? "Restore" : "Maximize";
        AutomationProperties.SetName(MaximizeButton, label);
        MaximizeButton.ToolTip = label;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        bool typing = Keyboard.FocusedElement is TextBox;
        if (ctrl && e.Key == Key.D1) { ShowMainPage(MainPage.Lighting); e.Handled = true; }
        else if (ctrl && e.Key == Key.OemComma) { ShowMainPage(MainPage.Settings); e.Handled = true; }
        else if (_currentPage == MainPage.Settings && e.Key == Key.Escape) { ShowMainPage(MainPage.Lighting); e.Handled = true; }
        else if (_currentPage != MainPage.Lighting) return;
        else if (ctrl && e.Key == Key.S) { if (ApplyButton.IsVisible) ApplyButton_Click(ApplyButton, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.N) { AddPresetButton_Click(AddPresetButton, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.R) { RandomColorButton_Click(RandomColorButton, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.L && LightingSwitch.IsEnabled) { ToggleLightingState(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z && !typing && _lastDeletedPreset is not null) { UndoDeletePreset(); e.Handled = true; }
        else if (e.Key == Key.F2 && PresetEditorPanel.IsVisible) { PresetNameBox.Focus(); PresetNameBox.SelectAll(); e.Handled = true; }
    }

    private void ColorPicker_SelectedColorChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded || _syncingFields) return;
        SetSelectedColor(ColorPicker.SelectedColor, true, updatePicker: false);
    }

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _syncingFields) return;
        if (ColorText.TryParseHex(HexBox.Text, out Color color))
        {
            HexBox.ClearValue(BorderBrushProperty);
            SetSelectedColor(color, true);
        }
    }

    private void HexBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_syncingFields) return;
        if (!TrySetFromHex(HexBox.Text, true))
        {
            HexBox.BorderBrush = (Brush)FindResource("DisconnectedBrush");
            SyncFieldsFromColor();
            ShowInlineStatus("That isn't a valid color code. Use # and six digits, like #FF7800.");
        }
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void RgbBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _syncingFields) return;
        if (ColorText.TryParseRgb(RedBox.Text, GreenBox.Text, BlueBox.Text, out Color color))
        {
            SetRgbBorders(false);
            SetSelectedColor(color, true);
        }
    }

    private void RgbBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_syncingFields) return;
        if (TryClampRgbFields(out Color color))
        {
            SetRgbBorders(false);
            SetSelectedColor(color, true);
        }
        else
        {
            SetRgbBorders(true);
            SyncFieldsFromColor();
            ShowInlineStatus("RGB values must be between 0 and 255");
        }
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessText is null) return;
        int brightness = (int)Math.Round(e.NewValue);
        BrightnessText.Text = $"{brightness}%";
        UpdateMixerPreview();
        if (!IsLoaded || _syncingFields) return;

        _settings.Brightness = brightness;
        RefreshPresetVisualState();
        QueueSettingsSave();
        QueueLiveApply();
    }

    private void EffectSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (EffectSpeedText is null || _syncingEffectSpeed) return;

        int speed = EffectSpeedPolicy.Normalize((int)Math.Round(e.NewValue));
        EffectSpeedText.Text = $"{speed}%";
        if (!IsLoaded || !EffectSpeedPolicy.SupportsSpeed(_selectedEffect)) return;

        lock (_colorGate)
        {
            SetEffectSpeed(_selectedEffect, speed);
        }
        QueueSettingsSave();
        // The existing stream reads this value every frame, so the change applies
        // live without restarting a loop or adding another HID writer.
    }

    private void PresetCard_Click(object sender, RoutedEventArgs e)
    {
        // Checked also fires when RefreshPresetVisualState re-selects via binding; ignore that echo.
        if (sender is not FrameworkElement { Tag: ColorPreset preset } || preset.Id == _selectedPresetId) return;
        SelectPreset(preset);
    }

    // Clicking the already-selected preset discards unsaved edits (Checked doesn't fire then).
    private void PresetTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ColorPreset preset } && preset.Id == _selectedPresetId && preset.IsDirty)
        {
            SelectPreset(preset);
            ShowInlineStatus($"Reverted to the saved \"{preset.DisplayName}\".");
        }
    }

    private void SelectPreset(ColorPreset preset)
    {
        if (!ColorText.TryParseHex(preset.Hex, out Color color)) return;

        _suppressPresetDirty = true;
        _selectedPresetId = preset.Id;
        _settings.SelectedPresetId = preset.Id;
        _syncingFields = true;
        BrightnessSlider.Value = Math.Clamp(preset.Brightness, 0, 100);
        _syncingFields = false;
        _settings.Brightness = preset.Brightness;
        SetSelectedColor(color, true);
        _suppressPresetDirty = false;
        RefreshPresetVisualState();
        QueueSettingsSave();
    }

    // Save as new: create the preset immediately and put the cursor in its name field.
    private void AddPresetButton_Click(object sender, RoutedEventArgs e)
    {
        ColorPreset preset = new()
        {
            Name = $"My color {_presets.Count(p => p.Name.StartsWith("My color", StringComparison.Ordinal)) + 1}",
            Hex = CurrentHex,
            Brightness = CurrentBrightness,
            Order = _presets.Count
        };
        _presets.Add(preset);
        _selectedPresetId = preset.Id;
        _settings.SelectedPresetId = preset.Id;
        NormalizeOrders();
        RefreshPresetVisualState();
        SaveSettingsNow();
        ShowInlineStatus($"Saved new preset \"{preset.DisplayName}\". Type a name to rename it.");
        Dispatcher.BeginInvoke(() => { PresetNameBox.Focus(); PresetNameBox.SelectAll(); }, DispatcherPriority.Input);
    }

    private void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ColorPreset preset }) return;
        DeletePreset(preset);
        e.Handled = true;
    }

    private void PresetTile_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || sender is not FrameworkElement { Tag: ColorPreset preset }) return;
        DeletePreset(preset);
        e.Handled = true;
    }

    private void DeletePreset(ColorPreset preset)
    {
        int index = _presets.IndexOf(preset);
        if (index < 0) return;
        bool wasSelected = preset.Id == _selectedPresetId;
        _presets.Remove(preset);
        if (wasSelected)
        {
            _selectedPresetId = null;
            _settings.SelectedPresetId = null;
        }
        NormalizeOrders();
        RefreshPresetVisualState();
        SaveSettingsNow();

        _lastDeletedPreset = (preset, index, wasSelected);
        UndoDeleteButton.Visibility = Visibility.Visible;
        ShowInlineStatus($"Deleted \"{preset.DisplayName}\".");
        _ = HideUndoLaterAsync();

        // Keep keyboard users in the list: focus the neighbouring tile, or Save as new.
        Dispatcher.BeginInvoke(() =>
        {
            if (_presets.Count == 0) { AddPresetButton.Focus(); return; }
            ColorPreset next = _presets[Math.Min(index, _presets.Count - 1)];
            if (PresetItemsControl.ItemContainerGenerator.ContainerFromItem(next) is DependencyObject container &&
                FindDescendant<RadioButton>(container) is { } tile)
                tile.Focus();
        }, DispatcherPriority.Loaded);
    }

    private async Task HideUndoLaterAsync()
    {
        _undoCancellation?.Cancel();
        _undoCancellation = new CancellationTokenSource();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), _undoCancellation.Token);
            UndoDeleteButton.Visibility = Visibility.Collapsed;
            _lastDeletedPreset = null;
        }
        catch (OperationCanceledException) { }
    }

    private void UndoDeleteButton_Click(object sender, RoutedEventArgs e) => UndoDeletePreset();

    private void UndoDeletePreset()
    {
        if (_lastDeletedPreset is not { } deleted) return;
        _undoCancellation?.Cancel();
        _presets.Insert(Math.Min(deleted.Index, _presets.Count), deleted.Preset);
        _lastDeletedPreset = null;
        UndoDeleteButton.Visibility = Visibility.Collapsed;
        NormalizeOrders();
        if (deleted.WasSelected) SelectPreset(deleted.Preset);
        else RefreshPresetVisualState();
        SaveSettingsNow();
        ShowInlineStatus($"Restored \"{deleted.Preset.DisplayName}\".");
    }

    private void PresetNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingPresetName) return;
        if (_presets.FirstOrDefault(p => p.Id == _selectedPresetId) is not { } preset) return;
        preset.Name = PresetNameBox.Text.Trim();
        QueueSettingsSave();
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    // Save changes to the selected preset (or save as a new one when none is selected).
    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        ColorPreset? selected = _presets.FirstOrDefault(p => p.Id == _selectedPresetId);
        if (selected is null)
        {
            AddPresetButton_Click(sender, e);
            return;
        }

        selected.Hex = CurrentHex;
        selected.Brightness = CurrentBrightness;
        NormalizeOrders();
        RefreshPresetVisualState();
        SaveSettingsNow();
        ShowInlineStatus($"Saved changes to \"{selected.DisplayName}\".");
        await ShowApplyFeedbackAsync("Saved");
    }

    // Stop remains the existing stock-lighting restore path. It is intentionally
    // separate from Lighting Off, which only transmits black while retaining state.
    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopStreamingAsync();
        if (!_firmwareChecked || !_modInstalled)
        {
            ShowInlineStatus("RGB control isn't set up on this SC3 yet.");
            return;
        }
        if (_hid.TryOpen(out string openDetail) && _hid.TryDisableCustomRgb(out _))
        {
            ShowInlineStatus("Showing FIFINE's default lighting. Change a color to take over again.");
            _stopRestoredStockMode = true;
        }
        else
        {
            ShowInlineStatus($"Couldn't switch to the default lighting ({openDetail}). Try Reconnect.");
        }
        await RefreshDeviceStatusAsync();
    }

    private async void ReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_firmwareOperationActive) return;
        ShowInlineStatus("Reconnecting…");
        await _customButtons.StopAsync(sendOff: true);
        if (_isStreaming) await StopStreamingAsync();
        _hid.Close();
        _firmwareChecked = false;
        _firmwareFlavor = Sc3FirmwareFlavor.Unknown;
        _stopRestoredStockMode = false;
        await RefreshDeviceStatusAsync();
        ShowInlineStatus(_isConnected ? "Reconnected to your SC3." : "SC3 not found. Check the USB cable and try a port on the PC itself.");
    }

    // Checked/Unchecked (not Click) so mouse, keyboard and UI Automation Toggle all work.
    private void LightingSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || (LightingSwitch.IsChecked == true) == _settings.IsLightingEnabled) return;
        ToggleLightingState();
    }

    private void TurnLightingOnButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.IsLightingEnabled) ToggleLightingState();
        LightingSwitch.Focus();
    }

    private void ToggleLightingState()
    {
        if (_firmwareOperationActive) { UpdateLightingVisual(); return; }
        _settings.IsLightingEnabled = !_settings.IsLightingEnabled;
        UpdateLightingVisual();
        SaveSettingsNow();

        if (_settings.IsLightingEnabled)
        {
            _stopRestoredStockMode = false;
            QueueLiveApply();
            ShowInlineStatus("RGB lighting on.");
        }
        else
        {
            // Keep the desired base color, brightness, and selected preset intact.
            // Only the output target becomes black through the existing sender.
            ApplySelectedColor();
            ShowInlineStatus("RGB lighting off.");
        }
    }

    private void IntegratedSettingsView_StartWithWindowsChanged(object? sender, EventArgs e)
    {
        bool enabled = IntegratedSettingsView.StartWithWindowsEnabled;
        if (_settings.StartWithWindows == enabled) return;

        _settings.StartWithWindows = enabled;
        UpdateStartupRegistration();
        SaveSettingsNow();
    }

    private void EffectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingEffect || sender is not FrameworkElement { Tag: string effectName } ||
            !Enum.TryParse(effectName, ignoreCase: true, out LightingEffect effect)) return;

        if (_selectedEffect == effect) return;
        lock (_colorGate)
        {
            _selectedEffect = effect;
            _effectStartedAt = DateTime.UtcNow;
        }
        _settings.Effect = effect.ToString();
        RefreshEffectVisualState();
        QueueSettingsSave();
        QueueLiveApply();
    }

    private void ApplySelectedColor()
    {
        if (_firmwareOperationActive) return;
        if (!_firmwareChecked || !_modInstalled)
        {
            ShowInlineStatus("RGB control isn't set up on this SC3 yet.");
            return;
        }
        if (!HidDeviceClient.RgbWritesEnabled)
        {
            ShowInlineStatus("RGB output is paused for safety. Try Reconnect.");
            return;
        }

        Color currentOutput = GetEffectColor(DateTime.UtcNow);
        lock (_colorGate) _appliedColor = currentOutput;

        _settings.LastHex = CurrentHex;
        StartStreaming();
    }

    private void StartStreaming()
    {
        if (_firmwareOperationActive || !_firmwareChecked || !_modInstalled) return;
        if (_streamTask is { IsCompleted: false })
        {
            return;
        }

        _streamCancellation?.Dispose();
        _streamCancellation = new CancellationTokenSource();
        _isStreaming = true;
        StopButton.IsEnabled = true;
        _streamTask = Task.Run(() => StreamLoopAsync(_streamCancellation.Token));

    }

    // Existing HID stream logic preserved: it writes only changed colors and retries safely.
    private async Task StreamLoopAsync(CancellationToken cancellationToken)
    {
        DateTime nextReconnect = DateTime.MinValue;
        DateTime nextUiUpdate = DateTime.MinValue;
        string lastDetail = "Starting";
        bool lastSuccess = false;
        Color? lastSentColor = null;
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(1000.0 / 30.0));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!_hid.IsConnected && DateTime.UtcNow >= nextReconnect)
                {
                    lastSuccess = _hid.TryOpen(out lastDetail);
                    nextReconnect = DateTime.UtcNow.AddSeconds(1);
                    if (lastSuccess) lastSentColor = null;
                }

                if (_hid.IsConnected)
                {
                    Color color = GetEffectColor(DateTime.UtcNow);
                    lock (_colorGate) _appliedColor = color;
                    if (lastSentColor is null || lastSentColor.Value != color)
                    {
                        lastSuccess = _hid.TryWriteColor(color.R, color.G, color.B, out lastDetail);
                        if (lastSuccess) lastSentColor = color;
                        else
                        {
                            lastSentColor = null;
                            nextReconnect = DateTime.UtcNow.AddSeconds(1);
                        }
                    }
                }

                if (DateTime.UtcNow >= nextUiUpdate)
                {
                    bool success = lastSuccess;
                    string detail = lastDetail;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        UpdateConnectionVisual(success || _hid.IsConnected, detail);
                        // Routine writes stay silent so user messages remain visible and
                        // screen readers are not flooded; only failures and recovery are reported.
                        if (!success && detail != _lastStreamFailure)
                        {
                            _lastStreamFailure = detail;
                            ShowInlineStatus($"Lighting can't reach the SC3 ({detail}). Retrying…");
                        }
                        else if (success && _lastStreamFailure is not null)
                        {
                            _lastStreamFailure = null;
                            ShowInlineStatus("Lighting is reaching the SC3 again.");
                        }
                    });
                    nextUiUpdate = DateTime.UtcNow.AddMilliseconds(500);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _isStreaming = false;
        }
    }

    private async Task StopStreamingAsync()
    {
        CancellationTokenSource? cancellation = _streamCancellation;
        Task? task = _streamTask;
        _streamCancellation = null;
        _streamTask = null;
        cancellation?.Cancel();
        if (task is not null)
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }
        cancellation?.Dispose();
        _isStreaming = false;
        StopButton.IsEnabled = _isConnected;
    }

    private async Task RefreshDeviceStatusAsync()
    {
        if (_isStreaming || _firmwareOperationActive) return;

        (bool connected, string detail) = await Task.Run(() =>
        {
            bool connected = _hid.Probe(out string detail);
            return (connected, detail);
        });
        bool newlyConnected = connected && !_isConnected;

        RestoreDetection? restoreDetection = null;
        if (!connected)
            restoreDetection = await Task.Run(_firmwareService.DetectRestore);

        _recoveryModeDetected = restoreDetection?.RecoveryMode == true;
        if (_recoveryModeDetected)
        {
            _firmwareChecked = false;
            _modInstalled = false;
            _firmwareFlavor = Sc3FirmwareFlavor.Unknown;
            _finalMod15Capability = false;
            _firmwareStatus = restoreDetection!.NormalStatus;
            FirmwareVersionText.Text = "Firmware: recovery mode";
            SetRgbSetupRequired(false);
        }
        else if (!connected)
        {
            _recoveryPromptShown = false;
            _firmwareChecked = false;
            _modInstalled = false;
            _firmwareFlavor = Sc3FirmwareFlavor.Unknown;
            _finalMod15Capability = false;
            _firmwareUpdateOfferShown = false;
            _firmwareStatus = null;
            FirmwareVersionText.Text = "Firmware: not detected";
            SetRgbSetupRequired(false);
        }
        else if (!_firmwareChecked)
        {
            _recoveryPromptShown = false;
            DeviceStatus firmware = await Task.Run(_firmwareService.Detect);
            Sc3QueryReply? queryReply = null;
            if (firmware.ValidatedProfile && _hid.TryOpen(out _) &&
                _hid.TryQuerySc3(out Sc3QueryReply parsedReply, out _))
            {
                queryReply = parsedReply;
            }
            Sc3FirmwareFlavor flavor = Sc3FirmwareClassificationPolicy.Resolve(
                firmware.ValidatedProfile, firmware.ModInstalled, queryReply);

            bool rgbMod = firmware.ModInstalled ||
                flavor is Sc3FirmwareFlavor.Mod14 or Sc3FirmwareFlavor.DiagnosticMod14 or Sc3FirmwareFlavor.Mod15;
            _firmwareChecked = true;
            _firmwareFlavor = flavor;
            _finalMod15Capability = queryReply?.SupportsFinalCustomShortcuts == true;
            _modInstalled = rgbMod;
            _firmwareStatus = rgbMod && !firmware.ModInstalled
                ? firmware with { ModInstalled = true, Message = FirmwareReadyMessage(flavor) }
                : firmware;
            FirmwareVersionText.Text = FirmwareVersionLabel(flavor);
            SetRgbSetupRequired(firmware.ValidatedProfile && flavor == Sc3FirmwareFlavor.Stock);
        }

        RefreshFirmwareUpdatePresentation(connected);
        UpdateConnectionVisual(connected, detail);

        if (_recoveryModeDetected && !_recoveryPromptShown)
        {
            _recoveryPromptShown = true;
            _ = Dispatcher.BeginInvoke(async () => await ShowRecoveryModePromptAsync());
            return;
        }

        if (connected && _firmwareFlavor == Sc3FirmwareFlavor.Mod14 && !_firmwareUpdateOfferShown && !_isStartupLaunch)
        {
            _firmwareUpdateOfferShown = true;
            _ = Dispatcher.BeginInvoke(async () => await ShowMixerFirmwareUpdateOfferAsync());
        }

        if (connected && _modInstalled && (newlyConnected || !_stopRestoredStockMode))
        {
            if (_settings.IsLightingEnabled)
                QueueLiveApply();
            else
                ApplySelectedColor();
        }

        if (connected && _firmwareFlavor == Sc3FirmwareFlavor.Mod15 && _restoreStockModeForStability)
        {
            _hid.TrySetCustomButtonMode(false, out _);
            _restoreStockModeForStability = false;
        }

        if (connected && _firmwareFlavor == Sc3FirmwareFlavor.Mod15 &&
            _customButtons.DesiredEnabled && !_customButtons.IsActive)
        {
            await _customButtons.StartAsync();
        }
    }

    private void UpdateConnectionVisual(bool connected, string detail)
    {
        if (_isConnected != connected && SystemParameters.ClientAreaAnimation)
        {
            DoubleAnimation fade = new(0.35, 1, TimeSpan.FromMilliseconds(180));
            StatusDot.BeginAnimation(OpacityProperty, fade);
            SidebarConnectionText.BeginAnimation(OpacityProperty, fade);
            FooterConnectionText.BeginAnimation(OpacityProperty, fade);
        }
        bool changed = _isConnected != connected;
        _isConnected = connected;
        bool recovery = _recoveryModeDetected;
        Brush stateBrush = (Brush)FindResource(connected ? "ConnectedBrush" : recovery ? "WarningBrush" : "DisconnectedBrush");
        bool rgbReady = connected && !_firmwareOperationActive && _firmwareChecked && _modInstalled && _firmwareStatus?.ValidatedProfile == true;
        StatusDot.Fill = stateBrush;
        SidebarConnectionText.Text = recovery ? "Recovery mode" : connected ? "Connected" : "Not connected";
        SidebarConnectionText.Foreground = stateBrush;
        DeviceHealthDetail.Text = _firmwareOperationActive ? "Firmware operation in progress. Keep the mixer plugged in."
            : recovery ? "The mixer is waiting for firmware. Restore it from Settings › Troubleshooting."
            : !connected ? "Plug the SC3 into a USB port on your PC."
            : !_firmwareChecked ? "Checking firmware…"
            : _firmwareStatus?.ValidatedProfile == true && _modInstalled ? "Ready for RGB control."
            : _firmwareStatus?.ValidatedProfile == true ? "Working normally. RGB control needs a one-time setup."
            : _firmwareStatus?.Message ?? "Firmware state could not be verified.";
        FooterConnectionText.Text = recovery ? "SC3 in recovery mode" : connected ? "SC3 connected over USB" : "SC3 not connected";
        FooterReadyText.Text = FirmwarePresentationPolicy.ReadyLabel(_firmwareStatus, recovery, _firmwareOperationActive);
        FooterReadyText.Foreground = (Brush)FindResource(rgbReady ? "ConnectedBrush" : "SecondaryTextBrush");
        ApplyButton.IsEnabled = !_firmwareOperationActive; // Presets remain saved; output writes stay blocked during firmware operations.
        StopButton.IsEnabled = !_firmwareOperationActive && (rgbReady || _isStreaming);
        FooterReconnectButton.IsEnabled = !_firmwareOperationActive && !recovery;
        LightingSwitch.IsEnabled = !_firmwareOperationActive && !recovery;
        SettingsNavItem.IsEnabled = !_firmwareOperationActive;
        LightingNavItem.IsEnabled = !_firmwareOperationActive;
        if (changed) Announce(SidebarConnectionText);
    }

    private async void FirmwareSetupButton_Click(object sender, RoutedEventArgs e)
    {
        await RunMixerFirmwareUpdateAsync();
    }

    private void RefreshFirmwareUpdatePresentation(bool connected)
    {
        bool validatedProfile = _firmwareStatus?.ValidatedProfile == true;
        MixerFirmwarePresentation presentation = MixerFirmwarePresentation.Create(
            connected,
            validatedProfile,
            _firmwareFlavor,
            _finalMod15Capability);
        IntegratedSettingsView.SetMixerFirmwarePresentation(presentation);

        bool eventTransportAvailable = CustomShortcutStabilityPolicy.EventDrivenTransportEnabled;
        string requirement = presentation.CustomButtonsAvailable && !eventTransportAvailable
            ? CustomShortcutStabilityPolicy.Message
            : !connected
            ? "Connect your SC3 to verify Firmware 1.5 before enabling Custom Button Shortcuts."
            : _firmwareFlavor switch
            {
                Sc3FirmwareFlavor.Mod14 => "Firmware 1.5 is required for Custom Button Shortcuts. Your existing RGB+ 1.4 features remain available until you update.",
                Sc3FirmwareFlavor.Stock => "Firmware 1.5 is required for Custom Button Shortcuts. Install RGB+ Firmware 1.5 from Updates.",
                Sc3FirmwareFlavor.DiagnosticMod14 => "Production RGB+ Firmware 1.5 is required before Custom Button Shortcuts can be enabled.",
                Sc3FirmwareFlavor.Mod15 when !_finalMod15Capability => "Install the event-driven Firmware 1.5 update to use Custom Button Shortcuts without continuous USB polling.",
                _ => "Custom Button Shortcuts require a verified RGB+ Firmware 1.5 device."
            };
        IntegratedSettingsView.SetCustomFirmwareAvailability(
            presentation.CustomButtonsAvailable && eventTransportAvailable,
            _settings.CustomShortcutsEnabled,
            requirement);
    }

    private async Task ShowMixerFirmwareUpdateOfferAsync()
    {
        if (_firmwareOperationActive || !_isConnected || _firmwareFlavor != Sc3FirmwareFlavor.Mod14)
            return;

        FirmwareUpdateAvailableWindow offer = new() { Owner = this };
        if (offer.ShowDialog() == true)
        {
            ShowMainPage(MainPage.Settings);
            IntegratedSettingsView.SelectUpdates();
            await RunMixerFirmwareUpdateAsync();
        }
    }

    private async void IntegratedSettingsView_FirmwareUpdateRequested(object? sender, EventArgs e)
    {
        await RunMixerFirmwareUpdateAsync();
    }

    private async Task RunMixerFirmwareUpdateAsync()
    {
        if (_firmwareOperationActive) return;

        if (!_firmwareChecked)
            await RefreshDeviceStatusAsync();

        if (_firmwareFlavor == Sc3FirmwareFlavor.Mod15 && _finalMod15Capability)
        {
            RefreshFirmwareUpdatePresentation(_isConnected);
            ShowInlineStatus("RGB+ Firmware 1.5 is already up to date");
            return;
        }

        if (!_isConnected || _firmwareStatus?.ValidatedProfile != true ||
            _firmwareFlavor is not (Sc3FirmwareFlavor.Stock or Sc3FirmwareFlavor.Mod14))
        {
            MessageBox.Show(this,
                "A supported Stock V22 or RGB+ Firmware 1.4 SC3 is required for this update.",
                "Update SC3 Firmware", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBoxResult answer = MessageBox.Show(this,
            "Your FIFINE SC3 mixer firmware will be updated to RGB+ Firmware 1.5.\n\n" +
            "Keep the SC3 connected during the update. Audio may temporarily disconnect, and the device will reboot automatically.",
            "Update SC3 Firmware", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        await BeginFirmwareOperationAsync("Preparing mixer firmware update");
        FirmwareSetupWindow progress = new(_firmwareService) { Owner = this };
        bool? success = progress.ShowDialog();
        await EndFirmwareOperationAsync();

        if (success == true && _firmwareFlavor == Sc3FirmwareFlavor.Mod15 && _finalMod15Capability)
        {
            ShowMainPage(MainPage.Settings);
            IntegratedSettingsView.SelectUpdates();
            ShowInlineStatus("Firmware updated successfully");
            MessageBox.Show(this, "Firmware updated successfully.", "Update SC3 Firmware",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (progress.Outcome == UpdaterState.SetupFailedDeviceHealthy)
            ShowInlineStatus("Firmware update failed · SC3 is working normally");
        else if (progress.Outcome == UpdaterState.SetupFailedBootloaderAvailable)
            ShowInlineStatus("Firmware update incomplete · update bootloader detected");
        else if (progress.Outcome == UpdaterState.RecoveryRequired)
            ShowInlineStatus("SC3 recovery required");
    }

    private void LightingNavItem_Checked(object sender, RoutedEventArgs e)
    {
        if (!_syncingNavigation && IsLoaded) ShowMainPage(MainPage.Lighting);
    }

    private void SettingsNavItem_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingNavigation || !IsLoaded) return;
        if (_firmwareOperationActive) { ShowMainPage(_currentPage); return; }
        ShowMainPage(MainPage.Settings);
    }

    private async void IntegratedSettingsView_CustomShortcutPreferenceChanged(object? sender, EventArgs e)
    {
        _settings.CustomShortcutsEnabled = IntegratedSettingsView.CustomShortcutsEnabled;
        SaveSettingsNow();
        UpdateStartupRegistration();
        await _customButtons.SetPreferenceAsync(_settings.CustomShortcutsEnabled);
        if (!_settings.CustomShortcutsEnabled && !_backgroundMode)
            _tray.Hide();
    }

    private void IntegratedSettingsView_CustomAppChosen(object? sender, CustomAppChosenEventArgs e)
    {
        SetCustomButtonAssignment(e.Button, e.Path, e.Name);
        IntegratedSettingsView.SetCustomAssignment(e.Button, e.Name);
        SaveSettingsNow();
    }

    private void IntegratedSettingsView_CustomAppCleared(object? sender, CustomButtonEventArgs e)
    {
        SetCustomButtonAssignment(e.Button, null, null);
        IntegratedSettingsView.SetCustomAssignment(e.Button, null);
        SaveSettingsNow();
    }

    private void CustomButtons_StateChanged(object? sender, CustomShortcutState e)
    {
        Dispatcher.BeginInvoke(() => IntegratedSettingsView.SetCustomRuntimeState(e.State, e.Message));
    }

    private void Tray_OpenRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => _ = OpenFromTrayAsync()));

    private void Tray_DisableShortcutsRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => _ = DisableShortcutsFromTrayAsync()));

    private void Tray_ExitRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => _ = ExitApplicationAsync()));

    private async Task EnterBackgroundModeAsync()
    {
        if (_backgroundMode || _exitInProgress) return;

        SaveSettingsNow();
        _statusTimer.Stop();
        _settingsSaveTimer.Stop();
        _liveApplyTimer.Stop();
        _liveApplyPending = false;
        _applyFeedbackCancellation?.Cancel();
        await StopStreamingAsync();

        _backgroundMode = true;
        ShowInTaskbar = false;
        Hide();
        _tray.Show();
    }

    internal Task OpenFromExternalLaunchAsync() => OpenFromTrayAsync();

    private async Task OpenFromTrayAsync()
    {
        if (_exitInProgress) return;

        _backgroundMode = false;
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _tray.Hide();
        _statusTimer.Start();
        await RefreshDeviceStatusAsync();
        if (_settings.CustomShortcutsEnabled)
            await _customButtons.StartAsync();
    }

    private async Task DisableShortcutsFromTrayAsync()
    {
        if (_exitInProgress) return;

        _settings.CustomShortcutsEnabled = false;
        IntegratedSettingsView.SetCustomPreference(false);
        SaveSettingsNow();
        UpdateStartupRegistration();
        await _customButtons.SetPreferenceAsync(false);
        IntegratedSettingsView.SetCustomRuntimeState(CustomShortcutRuntimeState.Stock);
    }

    private async Task ExitApplicationAsync()
    {
        if (_exitInProgress) return;
        _exitInProgress = true;

        _statusTimer.Stop();
        _settingsSaveTimer.Stop();
        _liveApplyTimer.Stop();
        _liveApplyPending = false;
        _applyFeedbackCancellation?.Cancel();
        _updateDownloadCancellation?.Cancel();
        SaveSettingsNow();

        await _customButtons.StopAsync(sendOff: true);
        await StopStreamingAsync();
        await _customButtons.DisposeAsync();
        _hid.Dispose();
        _tray.Dispose();

        _allowWindowClose = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void UpdateStartupRegistration() =>
        StartupManager.SetEnabled(CustomShortcutHostPolicy.ShouldRegisterStartup(
            _settings.StartWithWindows, _settings.CustomShortcutsEnabled));

    private string? GetCustomButtonPath(CustomButtonId button) => button switch
    {
        CustomButtonId.A => _settings.CustomAPath,
        CustomButtonId.B => _settings.CustomBPath,
        CustomButtonId.C => _settings.CustomCPath,
        _ => _settings.CustomDPath
    };

    // Assignments saved before actions existed store a bare file name ("notepad"); show them as "Open notepad".
    private void DescribeLegacyCustomAssignments()
    {
        foreach (CustomButtonId button in Enum.GetValues<CustomButtonId>())
        {
            string? path = GetCustomButtonPath(button);
            CustomButtonAction action = CustomButtonAction.Parse(path);
            string? name = button switch
            {
                CustomButtonId.A => _settings.CustomAName,
                CustomButtonId.B => _settings.CustomBName,
                CustomButtonId.C => _settings.CustomCName,
                _ => _settings.CustomDName
            };
            if (action.Kind == CustomActionKind.LaunchProgram && !string.IsNullOrWhiteSpace(name) &&
                !name.StartsWith("Open ", StringComparison.Ordinal))
                SetCustomButtonAssignment(button, path, action.Describe(name));
        }
    }

    private void SetCustomButtonAssignment(CustomButtonId button, string? path, string? name)
    {
        switch (button)
        {
            case CustomButtonId.A: _settings.CustomAPath = path; _settings.CustomAName = name; break;
            case CustomButtonId.B: _settings.CustomBPath = path; _settings.CustomBName = name; break;
            case CustomButtonId.C: _settings.CustomCPath = path; _settings.CustomCName = name; break;
            case CustomButtonId.D: _settings.CustomDPath = path; _settings.CustomDName = name; break;
        }
    }
    private async void IntegratedSettingsView_RestoreRequested(object? sender, EventArgs e)
    {
        await RestoreOriginalFirmwareAsync();
    }

    private void IntegratedSettingsView_BackRequested(object? sender, EventArgs e)
    {
        if (_firmwareOperationActive) return;
        ShowMainPage(MainPage.Lighting);
    }

    private async void IntegratedSettingsView_CheckForUpdatesRequested(object? sender, EventArgs e)
    {
        await CheckForApplicationUpdatesAsync(userInitiated: true);
    }

    private async void IntegratedSettingsView_UpdateNowRequested(object? sender, EventArgs e)
    {
        await DownloadApplicationUpdateAsync();
    }

    private void IntegratedSettingsView_CancelDownloadRequested(object? sender, EventArgs e)
    {
        _updateDownloadCancellation?.Cancel();
    }

    private async void IntegratedSettingsView_InstallAndRestartRequested(object? sender, EventArgs e)
    {
        await InstallApplicationUpdateAsync();
    }

    private void IntegratedSettingsView_AutomaticUpdateCheckChanged(object? sender, EventArgs e)
    {
        _settings.AutomaticallyCheckForUpdates = IntegratedSettingsView.AutomaticUpdateCheckEnabled;
        SaveSettingsNow();
    }

    private async void UpdateBannerButton_Click(object sender, RoutedEventArgs e)
    {
        ShowMainPage(MainPage.Settings);
        IntegratedSettingsView.SelectUpdates();
        if (_availableUpdate is null)
            await CheckForApplicationUpdatesAsync(userInitiated: true);
        else if (_availableUpdate.HasIntegrityMetadata)
            await DownloadApplicationUpdateAsync();
    }

    private void UpdateBannerLaterButton_Click(object sender, RoutedEventArgs e) => HideUpdateBanner();

    private async Task CheckForApplicationUpdatesAsync(bool userInitiated)
    {
        if (_updateCheckInProgress || _applicationUpdateInstalling) return;
        _updateCheckInProgress = true;

        if (userInitiated)
        {
            ShowMainPage(MainPage.Settings);
            IntegratedSettingsView.SelectUpdates();
        }
        IntegratedSettingsView.ShowUpdateChecking();

        try
        {
            UpdateCheckResult result = await _applicationUpdateService.CheckForUpdatesAsync(AppVersionInfo.Current);
            _availableUpdate = result.Candidate;
            IntegratedSettingsView.ShowUpdateResult(result);

            if (result.Candidate is not null)
            {
                UpdateBannerText.Text = $"Update available: v{result.Candidate.Version}";
                FooterStatusPanel.Visibility = Visibility.Collapsed;
                UpdateBanner.Visibility = Visibility.Visible;
            }
            else if (result.Status is UpdateCheckStatus.UpToDate or UpdateCheckStatus.NoReleases)
            {
                HideUpdateBanner();
            }
        }
        catch (OperationCanceledException)
        {
            if (userInitiated) IntegratedSettingsView.ShowUpdateError("Update check cancelled");
        }
        catch
        {
            if (userInitiated) IntegratedSettingsView.ShowUpdateError("Unable to check for updates");
        }
        finally
        {
            _updateCheckInProgress = false;
        }
    }

    private async Task DownloadApplicationUpdateAsync()
    {
        if (_firmwareOperationActive || _applicationUpdateInstalling) return;
        UpdateCandidate? candidate = _availableUpdate;
        if (candidate is null) return;

        ShowMainPage(MainPage.Settings);
        IntegratedSettingsView.SelectUpdates();
        if (!candidate.HasIntegrityMetadata)
        {
            IntegratedSettingsView.ShowUpdateError("Update verification metadata is missing");
            return;
        }

        _updateDownloadCancellation?.Cancel();
        _updateDownloadCancellation?.Dispose();
        _updateDownloadCancellation = new CancellationTokenSource();
        CancellationToken token = _updateDownloadCancellation.Token;
        _downloadedUpdate = null;
        HideUpdateBanner();
        IntegratedSettingsView.ShowDownloadStarted();
        Progress<int> progress = new(percent => IntegratedSettingsView.SetDownloadProgress(percent));

        try
        {
            _downloadedUpdate = await _applicationUpdateService.DownloadAndVerifyAsync(candidate, progress, token);
            IntegratedSettingsView.ShowUpdateReady(candidate.Version);
        }
        catch (OperationCanceledException)
        {
            IntegratedSettingsView.ShowUpdateError("Download cancelled");
        }
        catch (UpdateVerificationException)
        {
            IntegratedSettingsView.ShowUpdateError("Update verification failed");
        }
        catch (HttpRequestException)
        {
            IntegratedSettingsView.ShowUpdateError("Update download failed");
        }
        catch
        {
            IntegratedSettingsView.ShowUpdateError("Update download failed");
        }
        finally
        {
            _updateDownloadCancellation?.Dispose();
            _updateDownloadCancellation = null;
        }
    }

    private async Task InstallApplicationUpdateAsync()
    {
        if (_firmwareOperationActive || _applicationUpdateInstalling || _downloadedUpdate is null) return;
        _applicationUpdateInstalling = true;
        IntegratedSettingsView.ShowInstalling();
        SaveSettingsNow();
        _liveApplyPending = false;
        _liveApplyTimer.Stop();
        _statusTimer.Stop();
        await _customButtons.StopAsync(sendOff: true);
        await StopStreamingAsync();
        _hid.Close();

        try
        {
            await _applicationUpdateService.LaunchInstallerAsync(_downloadedUpdate, CustomShortcutHostPolicy.ShouldRegisterStartup(_settings.StartWithWindows, _settings.CustomShortcutsEnabled));
            await ExitApplicationAsync();
        }
        catch (UpdateVerificationException)
        {
            _applicationUpdateInstalling = false;
            _statusTimer.Start();
            IntegratedSettingsView.ShowUpdateError("Update verification failed");
            await RefreshDeviceStatusAsync();
        }
        catch
        {
            _applicationUpdateInstalling = false;
            _statusTimer.Start();
            IntegratedSettingsView.ShowUpdateError("Unable to start the update installer");
            await RefreshDeviceStatusAsync();
        }
    }

    private void HideUpdateBanner()
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
        FooterStatusPanel.Visibility = Visibility.Visible;
    }

    private void ShowMainPage(MainPage page)
    {
        _currentPage = page;
        RgbControlView.Visibility = page == MainPage.Lighting ? Visibility.Visible : Visibility.Collapsed;
        IntegratedSettingsView.Visibility = page == MainPage.Settings ? Visibility.Visible : Visibility.Collapsed;
        RefreshMainNavigationVisuals();
    }

    private void RefreshMainNavigationVisuals()
    {
        _syncingNavigation = true;
        LightingNavItem.IsChecked = _currentPage == MainPage.Lighting;
        SettingsNavItem.IsChecked = _currentPage == MainPage.Settings;
        _syncingNavigation = false;
    }

    private void SetRgbSetupRequired(bool required)
    {
        FirmwareSetupButton.Visibility = required ? Visibility.Visible : Visibility.Collapsed;
        SetupBar.Visibility = FirmwareSetupButton.Visibility;
    }

    private async Task RestoreOriginalFirmwareAsync()
    {
        if (_firmwareOperationActive) return;
        await BeginFirmwareOperationAsync("Preparing original firmware restore");

        bool success = false;
        UpdaterState outcome = UpdaterState.Idle;
        try
        {
            RestoreDetection detection = await Task.Run(_firmwareService.DetectRestore);
            if (!detection.CanRestore)
            {
                MessageBox.Show(this, detection.Message, "Restore Original Firmware", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Full preflight validates exact device state, package SHA/MVA layout,
            // transition plan, transfer/ACK/finalization and verification plan.
            // It performs no bootloader-enter, erase, firmware-write or finalize command.
            await Task.Run(() => _firmwareService.RestoreStockDryRun());

            RestoreConfirmationWindow confirmation = new() { Owner = this };
            if (confirmation.ShowDialog() != true)
            {
                ShowInlineStatus("Restore cancelled");
                return;
            }

            FirmwareSetupWindow progress = new(_firmwareService, FirmwareWindowMode.RestoreStock) { Owner = this };
            success = progress.ShowDialog() == true;
            outcome = progress.Outcome;
            if (outcome == UpdaterState.RestoreFailedBootloaderAvailable) _recoveryPromptShown = true;
            if (success)
                _stopRestoredStockMode = true;
        }
        catch (Exception ex)
        {
            outcome = ex is FirmwareUpdateException firmwareError ? firmwareError.Outcome : UpdaterState.Failed;
            if (outcome == UpdaterState.RestoreFailedBootloaderAvailable) _recoveryPromptShown = true;
            MessageBox.Show(this, ex.Message, "Restore Original Firmware", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            await EndFirmwareOperationAsync();
        }

        if (success)
        {
            MessageBox.Show(this,
                "SC3 restored successfully\n\nOriginal firmware is installed.",
                "Restore Original Firmware", MessageBoxButton.OK, MessageBoxImage.Information);
            ShowInlineStatus("RGB control isn't set up on this SC3 yet.");
            return;
        }

        if (outcome == UpdaterState.RestoreFailedDeviceHealthy)
            ShowInlineStatus("Restore failed, but your SC3 is working normally.");
        else if (outcome == UpdaterState.RestoreFailedBootloaderAvailable)
        {
            ShowInlineStatus("SC3 is still in recovery mode.");
            _recoveryPromptShown = true;
            await ShowRecoveryModePromptAsync();
        }
        else if (outcome == UpdaterState.RestoreRecoveryRequired)
            ShowInlineStatus("SC3 recovery is required");
    }

    private async Task BeginFirmwareOperationAsync(string status)
    {
        _firmwareOperationActive = true;
        _statusTimer.Stop();
        _liveApplyPending = false;
        _liveApplyTimer.Stop();
        await _customButtons.StopAsync(sendOff: true);
        await StopStreamingAsync();
        _hid.Close();
        _firmwareChecked = false;
        _modInstalled = false;
        _firmwareFlavor = Sc3FirmwareFlavor.Unknown;
        _firmwareStatus = null;
        SetRgbSetupRequired(false);
        UpdateConnectionVisual(_isConnected, status);
        ShowInlineStatus(status);
    }

    private async Task EndFirmwareOperationAsync()
    {
        _firmwareOperationActive = false;
        _firmwareChecked = false;
        _modInstalled = false;
        _firmwareFlavor = Sc3FirmwareFlavor.Unknown;
        _firmwareStatus = null;
        _statusTimer.Start();
        await RefreshDeviceStatusAsync();
    }

    private async Task ShowRecoveryModePromptAsync()
    {
        if (_firmwareOperationActive || !_recoveryModeDetected) return;
        RecoveryModeWindow recovery = new() { Owner = this };
        if (recovery.ShowDialog() == true)
            await RestoreOriginalFirmwareAsync();
    }
    private void UpdateLightingVisual()
    {
        bool on = _settings.IsLightingEnabled;
        LightingSwitch.IsChecked = on;
        LightingOffBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        LightingStatusText.Text = on
            ? "Changes show on your SC3 instantly. Save a preset to keep a look."
            : "Lighting is off. Edits are kept and will show when you turn it on.";
        UpdateMixerPreview();
    }

    private bool TrySetFromHex(string? input, bool liveEligible)
    {
        if (!ColorText.TryParseHex(input, out Color color)) return false;
        SetSelectedColor(color, liveEligible);
        return true;
    }

    private void SetSelectedColor(Color color, bool liveEligible, bool updatePicker = true)
    {
        lock (_colorGate)
        {
            _selectedColor = color;
            _effectStartedAt = DateTime.UtcNow;
        }
        _settings.LastHex = CurrentHex;
        if (updatePicker)
        {
            _syncingFields = true;
            ColorPicker.SelectedColor = color;
            _syncingFields = false;
        }
        UpdateDynamicAccent(color);
        SyncFieldsFromColor();
        RefreshPresetVisualState();
        QueueSettingsSave();
        if (liveEligible) QueueLiveApply();
    }

    private void QueueLiveApply()
    {
        if (_firmwareOperationActive || !_settings.IsLightingEnabled || !_firmwareChecked || !_modInstalled) return;
        _stopRestoredStockMode = false;
        _liveApplyPending = true;
        if (!_liveApplyTimer.IsEnabled) _liveApplyTimer.Start();
    }

    private void RefreshPresetVisualState()
    {
        ColorPreset? selected = _presets.FirstOrDefault(p => p.Id == _selectedPresetId);
        foreach (ColorPreset preset in _presets)
        {
            preset.IsSelected = preset == selected;
            preset.IsDirty = !_suppressPresetDirty && preset == selected &&
                (!string.Equals(preset.Hex, CurrentHex, StringComparison.OrdinalIgnoreCase) || preset.Brightness != CurrentBrightness);
        }

        bool dirty = selected?.IsDirty == true;
        ApplyButton.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        PresetEditorPanel.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        if (selected is not null && !PresetNameBox.IsKeyboardFocusWithin && PresetNameBox.Text != selected.Name)
        {
            _syncingPresetName = true;
            PresetNameBox.Text = selected.Name;
            _syncingPresetName = false;
        }
        PresetHintText.Text = selected is null
            ? "No preset selected. Pick one, or use Save as new to keep this color."
            : dirty ? $"You changed \"{selected.DisplayName}\". Save changes to keep them, or Save as new for a copy."
            : $"Using \"{selected.DisplayName}\".";
    }

    private void EnsureEditablePresets()
    {
        _settings.Presets ??= [];
        if (!_settings.PresetsInitialized)
        {
            _settings.Presets = StarterPresets.Select((s, i) => CreateStarter(s.Name, s.Hex, 100, i)).ToList();
            _settings.PresetsInitialized = true;
            _settings.StarterPresetsVersion = StarterPresetsVersion;
            return;
        }

        // Existing users: append starters added in later versions once, skipping
        // colors they already have and never re-adding ones they deleted before.
        if (_settings.StarterPresetsVersion >= StarterPresetsVersion) return;
        int order = _settings.Presets.Count == 0 ? 0 : _settings.Presets.Max(p => p.Order) + 1;
        foreach ((string name, string hex, int version) in StarterPresets)
        {
            if (version <= _settings.StarterPresetsVersion || version == 1) continue;
            if (_settings.Presets.Any(p => string.Equals(p.Hex, hex, StringComparison.OrdinalIgnoreCase))) continue;
            _settings.Presets.Add(CreateStarter(name, hex, 100, order++));
        }
        _settings.StarterPresetsVersion = StarterPresetsVersion;
    }

    private const int StarterPresetsVersion = 2;

    private static readonly (string Name, string Hex, int Version)[] StarterPresets =
    [
        ("White", "#FFFFFF", 1),
        ("Red", "#FF0000", 1),
        ("Green", "#00FF00", 1),
        ("Blue", "#0000FF", 1),
        ("Orange", "#FF7800", 1),
        ("Purple", "#9D00FF", 1),
        ("Warm White", "#FFB46B", 2),
        ("Yellow", "#FFD000", 2),
        ("Lime", "#A6FF00", 2),
        ("Teal", "#00C2A8", 2),
        ("Cyan", "#00FFFF", 2),
        ("Ice Blue", "#6EC8FF", 2),
        ("Magenta", "#FF00FF", 2),
        ("Pink", "#FF2D95", 2)
    ];

    private void RandomColorButton_Click(object sender, RoutedEventArgs e)
    {
        // Vivid, fully bright colors read best on the mixer LEDs; keep the new
        // hue at least 40° from the current one so each click visibly changes.
        double currentHue = HueOf(_selectedColor);
        double hue;
        do hue = Random.Shared.NextDouble() * 360;
        while (Math.Abs(((hue - currentHue + 540) % 360) - 180) < 40);
        Color color = UI.Controls.HsvColorPicker.ColorFromHsv(hue, 0.75 + Random.Shared.NextDouble() * 0.25, 1);

        _selectedPresetId = null;
        _settings.SelectedPresetId = null;
        SetSelectedColor(color, true);
        RefreshPresetVisualState();
        PresetHintText.Text = $"Random color {CurrentHex}. Use Save as new to keep it.";
        ShowInlineStatus($"Random color {CurrentHex}.");
        QueueSettingsSave();
    }

    private static double HueOf(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), delta = max - Math.Min(r, Math.Min(g, b));
        if (delta == 0) return 0;
        double hue = max == r ? (g - b) / delta % 6 : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
        return (hue * 60 + 360) % 360;
    }

    private static ColorPreset CreateStarter(string name, string hex, int brightness, int order) => new()
    {
        Name = name,
        Hex = hex,
        Brightness = brightness,
        Order = order
    };

    private void NormalizeOrders()
    {
        for (int i = 0; i < _presets.Count; i++) _presets[i].Order = i;
    }

    private void QueueSettingsSave()
    {
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void SaveSettingsNow()
    {
        _settingsSaveTimer.Stop();
        _settings.LastHex = CurrentHex;
        _settings.Red = _selectedColor.R;
        _settings.Green = _selectedColor.G;
        _settings.Blue = _selectedColor.B;
        _settings.Brightness = CurrentBrightness;
        _settings.Effect = _selectedEffect.ToString();
        _settings.SelectedPresetId = _selectedPresetId;
        _settings.SelectedPresetName = _presets.FirstOrDefault(p => p.Id == _selectedPresetId)?.DisplayName;
        _settings.Presets = _presets.ToList();
        SettingsStore.Save(_settings);
    }

    private string CurrentHex => $"#{_selectedColor.R:X2}{_selectedColor.G:X2}{_selectedColor.B:X2}";
    private int CurrentBrightness => (int)Math.Round(BrightnessSlider.Value);

    private async Task ShowApplyFeedbackAsync(string text)
    {
        _applyFeedbackCancellation?.Cancel();
        _applyFeedbackCancellation = new CancellationTokenSource();
        CancellationToken token = _applyFeedbackCancellation.Token;
        ApplyButtonText.Text = text;
        try
        {
            await Task.Delay(850, token);
            ApplyButtonText.Text = "Save changes";
        }
        catch (OperationCanceledException) { }
    }

    private void SyncFieldsFromColor()
    {
        _syncingFields = true;
        HexBox.Text = CurrentHex;
        RedBox.Text = _selectedColor.R.ToString(CultureInfo.InvariantCulture);
        GreenBox.Text = _selectedColor.G.ToString(CultureInfo.InvariantCulture);
        BlueBox.Text = _selectedColor.B.ToString(CultureInfo.InvariantCulture);
        ColorPreview.Background = new SolidColorBrush(_selectedColor);
        _syncingFields = false;
    }

    private void UpdateDynamicAccent(Color color)
    {
        // In Windows High Contrast the accent stays on system colors.
        if (UI.Themes.ThemeManager.AllowDynamicAccent)
        {
            Application.Current.Resources["AccentBrush"] = new SolidColorBrush(color);
            Application.Current.Resources["AccentForegroundBrush"] = new SolidColorBrush(ColorText.ForegroundOn(color));
            Application.Current.Resources["AccentTextBrush"] = new SolidColorBrush(ColorText.VisibleOnDark(color));
        }
        PreviewGlow.Color = color;
        RefreshEffectVisualState();
        UpdateMixerPreview();
    }

    private void RefreshEffectVisualState()
    {
        if (StaticEffectButton is null) return;

        // Selection visuals come from the SegmentedRadio style; just sync IsChecked.
        _syncingEffect = true;
        foreach ((RadioButton button, LightingEffect effect) in new[]
        {
            (StaticEffectButton, LightingEffect.Static),
            (BreathingEffectButton, LightingEffect.Breathing),
            (RainbowEffectButton, LightingEffect.Rainbow),
            (PulseEffectButton, LightingEffect.Pulse),
            (ColorCycleEffectButton, LightingEffect.ColorCycle)
        })
        {
            button.IsChecked = effect == _selectedEffect;
        }
        _syncingEffect = false;

        RefreshEffectSpeedVisualState();
    }

    private void RefreshEffectSpeedVisualState()
    {
        if (EffectSpeedPanel is null || EffectSpeedSlider is null || EffectSpeedText is null) return;

        bool supported = EffectSpeedPolicy.SupportsSpeed(_selectedEffect);
        EffectSpeedPanel.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        if (!supported) return;

        int speed = GetEffectSpeed(_selectedEffect);
        _syncingEffectSpeed = true;
        EffectSpeedSlider.Value = speed;
        EffectSpeedText.Text = $"{speed}%";
        _syncingEffectSpeed = false;
    }

    private Color GetEffectColor(DateTime now)
    {
        SavedLightingState state;
        DateTime startedAt;
        lock (_colorGate)
        {
            startedAt = _effectStartedAt;
            state = new SavedLightingState(
                _selectedColor,
                _settings.Brightness,
                _settings.IsLightingEnabled,
                _selectedEffect,
                GetEffectSpeed(_selectedEffect));
        }
        return state.GetOutput(startedAt, now);
    }

    private static LightingEffect ParseLightingEffect(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out LightingEffect effect) ? effect : LightingEffect.Static;

    private int GetEffectSpeed(LightingEffect effect) => effect switch
    {
        LightingEffect.Breathing => EffectSpeedPolicy.Normalize(_settings.BreathingSpeed),
        LightingEffect.Rainbow => EffectSpeedPolicy.Normalize(_settings.RainbowSpeed),
        LightingEffect.Pulse => EffectSpeedPolicy.Normalize(_settings.PulseSpeed),
        LightingEffect.ColorCycle => EffectSpeedPolicy.Normalize(_settings.ColorCycleSpeed),
        _ => EffectSpeedPolicy.DefaultSpeed
    };

    private void SetEffectSpeed(LightingEffect effect, int speed)
    {
        speed = EffectSpeedPolicy.Normalize(speed);
        switch (effect)
        {
            case LightingEffect.Breathing: _settings.BreathingSpeed = speed; break;
            case LightingEffect.Rainbow: _settings.RainbowSpeed = speed; break;
            case LightingEffect.Pulse: _settings.PulseSpeed = speed; break;
            case LightingEffect.ColorCycle: _settings.ColorCycleSpeed = speed; break;
        }
    }

    // UI-only layered preview: base hardware remains untouched while the separate
    // lighting and glow masks receive the selected base colour and visual intensity.
    private void UpdateMixerPreview()
    {
        if (!IsLoaded || MixerLightingBrush is null || MixerGlowBrush is null ||
            MixerLightingOverlay is null || MixerGlowOverlay is null) return;

        MixerLightingBrush.Color = _selectedColor;
        MixerGlowBrush.Color = _selectedColor;
        double intensity = _settings.IsLightingEnabled
            ? Math.Pow(Math.Clamp(CurrentBrightness, 0, 100) / 100.0, 0.72)
            : 0;
        MixerLightingOverlay.Opacity = intensity;
        MixerGlowOverlay.Opacity = intensity * 0.22;
    }

    private static bool TryClampChannel(string? text, out byte value)
    {
        value = 0;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) return false;
        value = (byte)Math.Clamp(parsed, 0, 255);
        return true;
    }

    private bool TryClampRgbFields(out Color color)
    {
        color = default;
        if (!TryClampChannel(RedBox.Text, out byte red) || !TryClampChannel(GreenBox.Text, out byte green) || !TryClampChannel(BlueBox.Text, out byte blue)) return false;
        color = Color.FromRgb(red, green, blue);
        return true;
    }

    private void SetRgbBorders(bool error)
    {
        Brush brush = new SolidColorBrush(error ? Color.FromRgb(255, 77, 94) : Color.FromRgb(56, 56, 56));
        RedBox.BorderBrush = brush;
        GreenBox.BorderBrush = brush;
        BlueBox.BorderBrush = brush;
    }

    private static string FirmwareVersionLabel(Sc3FirmwareFlavor flavor) => flavor switch
    {
        Sc3FirmwareFlavor.Stock => "Firmware: FIFINE original (V22)",
        Sc3FirmwareFlavor.Mod14 => "Firmware: RGB+ 1.4",
        Sc3FirmwareFlavor.DiagnosticMod14 => "Firmware: RGB+ 1.4 (diagnostic)",
        Sc3FirmwareFlavor.Mod15 => "Firmware: RGB+ 1.5",
        _ => "Firmware: unknown"
    };

    private static string FirmwareReadyMessage(Sc3FirmwareFlavor flavor) => flavor switch
    {
        Sc3FirmwareFlavor.Mod15 => "RGB+ Mod 1.5 ready.",
        Sc3FirmwareFlavor.DiagnosticMod14 => "Diagnostic Mod 1.4 ready.",
        _ => "RGB+ Mod 1.4 ready."
    };

    private void ShowInlineStatus(string message)
    {
        TransferStatusText.Text = message;
        TransferStatusText.ToolTip = string.IsNullOrEmpty(message) ? null : message;
        Announce(TransferStatusText);
    }

    // Tell screen readers that a LiveSetting region changed so it is read aloud.
    private static void Announce(FrameworkElement element) =>
        (UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
}
