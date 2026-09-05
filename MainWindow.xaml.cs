using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using SC3RGBController.Models;
using SC3RGBController.Services;
using SC3RGBController.Services.Updates;
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
    private bool _isAddingPreset;
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
    private readonly bool _isStartupLaunch = Environment.GetCommandLineArgs()
        .Any(argument => string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        UpdateStartupRegistration();
        AppVersionText.Text = $"App: {AppVersionInfo.Current}";
        IntegratedSettingsView.ConfigureUpdates(AppVersionInfo.DisplayVersion, _settings.AutomaticallyCheckForUpdates);
        IntegratedSettingsView.ConfigureCustomButtons(_settings);
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
        StartWithWindowsToggle.IsChecked = _settings.StartWithWindows;
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

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        WindowFrame.CornerRadius = new CornerRadius(maximized ? 0 : 20);
        MaximizeButton.Content = maximized ? "❐" : "□";
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
            HexBox.BorderBrush = new SolidColorBrush(Color.FromRgb(56, 56, 56));
            SetSelectedColor(color, true);
        }
    }

    private void HexBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_syncingFields) return;
        if (!TrySetFromHex(HexBox.Text, true))
        {
            HexBox.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 77, 94));
            SyncFieldsFromColor();
            ShowInlineStatus("Invalid HEX value · use #RRGGBB");
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
        if (sender is not Button { Tag: ColorPreset preset }) return;
        if (!ColorText.TryParseHex(preset.Hex, out Color color)) return;

        _suppressPresetDirty = true;
        _isAddingPreset = false;
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

    private void AddPresetButton_Click(object sender, RoutedEventArgs e)
    {
        _isAddingPreset = true;
        _selectedPresetId = null;
        _settings.SelectedPresetId = null;
        RefreshPresetVisualState();
        PresetHintText.Text = "New preset ready · choose Apply to save";
    }

    private void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ColorPreset preset }) return;
        bool wasSelected = preset.Id == _selectedPresetId;
        _presets.Remove(preset);
        if (wasSelected)
        {
            _selectedPresetId = null;
            _settings.SelectedPresetId = null;
        }
        _isAddingPreset = false;
        NormalizeOrders();
        RefreshPresetVisualState();
        SaveSettingsNow();
        ShowInlineStatus("Preset deleted");
        e.Handled = true;
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        ColorPreset? selected = _presets.FirstOrDefault(p => p.Id == _selectedPresetId);
        if (selected is null)
        {
            selected = new ColorPreset
            {
                Name = string.Empty,
                Hex = CurrentHex,
                Brightness = CurrentBrightness,
                Order = _presets.Count
            };
            _presets.Add(selected);
            _selectedPresetId = selected.Id;
            _settings.SelectedPresetId = selected.Id;
            _isAddingPreset = false;
        }
        else
        {
            selected.Hex = CurrentHex;
            selected.Brightness = CurrentBrightness;
        }

        NormalizeOrders();
        RefreshPresetVisualState();
        SaveSettingsNow();
        await ShowApplyFeedbackAsync("Saved");
    }

    // Stop remains the existing stock-lighting restore path. It is intentionally
    // separate from Lighting Off, which only transmits black while retaining state.
    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopStreamingAsync();
        if (!_firmwareChecked || !_modInstalled)
        {
            ShowInlineStatus("RGB setup required");
            return;
        }
        if (_hid.TryOpen(out string openDetail) && _hid.TryDisableCustomRgb(out string restoreDetail))
        {
            ShowInlineStatus(restoreDetail);
            _stopRestoredStockMode = true;
        }
        else
        {
            ShowInlineStatus($"Stopped sending · {openDetail}");
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
        ShowInlineStatus(_isConnected ? "Reconnected" : "SC3 not detected");
    }

    private void LightingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_firmwareOperationActive) return;
        ShowMainPage(MainPage.Lighting);
        ToggleLightingState();
    }

    private void ToggleLightingState()
    {
        if (_firmwareOperationActive) return;
        _settings.IsLightingEnabled = !_settings.IsLightingEnabled;
        UpdateLightingVisual();
        SaveSettingsNow();

        if (_settings.IsLightingEnabled)
        {
            _stopRestoredStockMode = false;
            QueueLiveApply();
            ShowInlineStatus("Lighting restored");
        }
        else
        {
            // Keep the desired base color, brightness, and selected preset intact.
            // Only the output target becomes black through the existing sender.
            ApplySelectedColor();
            ShowInlineStatus("Lighting off");
        }
    }

    private void StartWithWindowsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingFields || StartWithWindowsToggle is null) return;

        bool enabled = StartWithWindowsToggle.IsChecked == true;
        if (_settings.StartWithWindows == enabled) return;

        _settings.StartWithWindows = enabled;
        UpdateStartupRegistration();
        SaveSettingsNow();
    }

    private void EffectButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string effectName } ||
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
            ShowInlineStatus("RGB setup required");
            return;
        }
        if (!HidDeviceClient.RgbWritesEnabled)
        {
            ShowInlineStatus("Safety hold · RGB write is unavailable");
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
            ShowInlineStatus(_settings.IsLightingEnabled
                ? $"Updated output · #{_appliedColor.R:X2}{_appliedColor.G:X2}{_appliedColor.B:X2}"
                : "Lighting remains off");
            return;
        }

        _streamCancellation?.Dispose();
        _streamCancellation = new CancellationTokenSource();
        _isStreaming = true;
        StopButton.IsEnabled = true;
        _streamTask = Task.Run(() => StreamLoopAsync(_streamCancellation.Token));
        ShowInlineStatus(_settings.IsLightingEnabled ? "Applying color…" : "Turning lighting off…");
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
                    await Dispatcher.InvokeAsync(() =>
                    {
                        UpdateConnectionVisual(lastSuccess || _hid.IsConnected, lastDetail);
                        ShowInlineStatus(lastDetail);
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
            FirmwareVersionText.Text = "FW: Recovery Mode";
            FirmwareSetupButton.Visibility = Visibility.Collapsed;
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
            FirmwareVersionText.Text = "FW: Not detected";
            FirmwareSetupButton.Visibility = Visibility.Collapsed;
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
            FirmwareSetupButton.Visibility = firmware.ValidatedProfile && flavor == Sc3FirmwareFlavor.Stock
                ? Visibility.Visible : Visibility.Collapsed;
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

        if (connected && _firmwareFlavor == Sc3FirmwareFlavor.Mod15 &&
            _customButtons.DesiredEnabled && !_customButtons.IsActive)
        {
            await _customButtons.StartAsync();
        }
    }

    private void UpdateConnectionVisual(bool connected, string detail)
    {
        if (_isConnected != connected)
        {
            DoubleAnimation fade = new(0.35, 1, TimeSpan.FromMilliseconds(180));
            StatusDot.BeginAnimation(OpacityProperty, fade);
            SidebarConnectionText.BeginAnimation(OpacityProperty, fade);
            FooterConnectionText.BeginAnimation(OpacityProperty, fade);
        }
        _isConnected = connected;
        bool recovery = _recoveryModeDetected;
        Brush stateBrush = (Brush)FindResource(connected ? "ConnectedBrush" : recovery ? "MutedBrush" : "DisconnectedBrush");
        bool rgbReady = connected && !_firmwareOperationActive && _firmwareChecked && _modInstalled && _firmwareStatus?.ValidatedProfile == true;
        Brush readyBrush = (Brush)FindResource(rgbReady ? "ConnectedBrush" : "MutedBrush");
        StatusDot.Fill = stateBrush;
        DeviceHealthIcon.Stroke = stateBrush;
        DeviceHealthTitle.Text = recovery ? "SC3 Recovery Mode" : "Device Status";
        SidebarConnectionText.Text = recovery ? "Recovery Mode" : connected ? "Connected" : "Disconnected";
        SidebarConnectionText.Foreground = stateBrush;
        DeviceHealthDetail.Text = recovery
            ? "SC3 detected in recovery mode. A firmware update may not have completed."
            : !connected ? "Device not detected."
            : !_firmwareChecked ? "Checking firmware state…"
            : _firmwareStatus?.ValidatedProfile == true && _modInstalled ? FirmwareReadyMessage(_firmwareFlavor)
            : _firmwareStatus?.ValidatedProfile == true ? "SC3 is working normally. RGB setup required."
            : _firmwareStatus?.Message ?? "Firmware state is not verified.";
        FooterConnectionText.Text = recovery ? "FIFINE SC3 recovery bootloader detected" : connected ? "Connected to FIFINE SC3" : "FIFINE SC3 disconnected";
        FooterConnectionText.Foreground = stateBrush;
        FooterReadyText.Text = FirmwarePresentationPolicy.ReadyLabel(_firmwareStatus, recovery, _firmwareOperationActive);
        FooterReadyText.Foreground = readyBrush;
        ApplyButton.IsEnabled = !_firmwareOperationActive; // Presets remain saved; output writes stay blocked during firmware operations.
        StopButton.IsEnabled = !_firmwareOperationActive && (rgbReady || _isStreaming);
        FooterReconnectButton.IsEnabled = !_firmwareOperationActive && !recovery;
        LightingButton.IsEnabled = !_firmwareOperationActive && !recovery;
        SettingsButton.IsEnabled = !_firmwareOperationActive;
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

        string requirement = !connected
            ? "Connect your SC3 to verify Firmware 1.5 before enabling Custom Button Shortcuts."
            : _firmwareFlavor switch
            {
                Sc3FirmwareFlavor.Mod14 => "Firmware 1.5 is required for Custom Button Shortcuts. Your existing RGB+ 1.4 features remain available until you update.",
                Sc3FirmwareFlavor.Stock => "Firmware 1.5 is required for Custom Button Shortcuts. Install RGB+ Firmware 1.5 from Updates.",
                Sc3FirmwareFlavor.DiagnosticMod14 => "Production RGB+ Firmware 1.5 is required before Custom Button Shortcuts can be enabled.",
                Sc3FirmwareFlavor.Mod15 when !_finalMod15Capability => "Firmware 1.5 was detected, but CBTN v2 could not be verified.",
                _ => "Custom Button Shortcuts require a verified RGB+ Firmware 1.5 device."
            };
        IntegratedSettingsView.SetCustomFirmwareAvailability(
            presentation.CustomButtonsAvailable,
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

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_firmwareOperationActive) return;
        IntegratedSettingsView.SelectCustomButtons();
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
        Brush activeBrush = (Brush)FindResource("ConnectedBrush");
        Brush inactiveBrush = new SolidColorBrush(Color.FromRgb(38, 38, 38));
        Brush activeBackground = new SolidColorBrush(Color.FromRgb(21, 27, 23));

        bool lightingOn = _settings.IsLightingEnabled;
        bool settingsActive = _currentPage == MainPage.Settings;
        LightingButton.BorderBrush = lightingOn ? activeBrush : inactiveBrush;
        LightingButton.Background = lightingOn ? activeBackground : Brushes.Transparent;
        LightingTitleText.Foreground = lightingOn ? activeBrush : (Brush)FindResource("TextBrush");
        SettingsButton.BorderBrush = settingsActive ? activeBrush : inactiveBrush;
        SettingsButton.Background = settingsActive ? activeBackground : Brushes.Transparent;
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
            ShowInlineStatus("RGB setup required");
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
        FirmwareSetupButton.Visibility = Visibility.Collapsed;
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
        Brush brush = (Brush)FindResource(on ? "ConnectedBrush" : "MutedBrush");
        LightingIconPath.Stroke = brush;
        LightingTitleText.Text = "Lighting";
        LightingDetailText.Text = on ? "RGB lighting is on." : "RGB lighting is disabled.";
        LightingDetailText.Foreground = brush;
        RefreshMainNavigationVisuals();
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

        AddPresetButton.BorderBrush = _isAddingPreset ? new SolidColorBrush(_selectedColor) : new SolidColorBrush(Color.FromRgb(80, 80, 80));
        AddPresetButton.BorderThickness = _isAddingPreset ? new Thickness(2) : new Thickness(1);
        PresetHintText.Text = _isAddingPreset ? "New preset ready · choose Apply to save" :
            selected?.IsDirty == true ? "Unsaved changes · choose Apply to save" : "Changes save with Apply";
    }

    private void EnsureEditablePresets()
    {
        _settings.Presets ??= [];
        if (_settings.PresetsInitialized) return;

        _settings.Presets =
        [
            CreateStarter("White", "#FFFFFF", 100, 0),
            CreateStarter("Red", "#FF0000", 100, 1),
            CreateStarter("Green", "#00FF00", 100, 2),
            CreateStarter("Blue", "#0000FF", 100, 3),
            CreateStarter("Orange", "#FF7800", 100, 4),
            CreateStarter("Purple", "#9D00FF", 100, 5)
        ];
        _settings.PresetsInitialized = true;
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
            ApplyButtonText.Text = "Apply";
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
        Application.Current.Resources["AccentBrush"] = new SolidColorBrush(color);
        PreviewGlow.Color = color;
        RefreshEffectVisualState();
        UpdateMixerPreview();
    }

    private void RefreshEffectVisualState()
    {
        if (StaticEffectButton is null) return;

        Color borderColor = _selectedColor;
        SolidColorBrush selectedBrush = new(borderColor);
        SolidColorBrush idleBrush = new(Color.FromRgb(52, 52, 52));
        foreach ((Button button, LightingEffect effect) in new[]
        {
            (StaticEffectButton, LightingEffect.Static),
            (BreathingEffectButton, LightingEffect.Breathing),
            (RainbowEffectButton, LightingEffect.Rainbow),
            (PulseEffectButton, LightingEffect.Pulse),
            (ColorCycleEffectButton, LightingEffect.ColorCycle)
        })
        {
            bool selected = effect == _selectedEffect;
            button.BorderBrush = selected ? selectedBrush : idleBrush;
            button.BorderThickness = selected ? new Thickness(1.5) : new Thickness(1);
            button.Effect = selected
                ? new DropShadowEffect { Color = borderColor, BlurRadius = 9, ShadowDepth = 0, Opacity = 0.28 }
                : null;
        }

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
        Color baseColor;
        LightingEffect effect;
        DateTime startedAt;
        int speed;
        lock (_colorGate)
        {
            baseColor = _selectedColor;
            effect = _selectedEffect;
            startedAt = _effectStartedAt;
            speed = GetEffectSpeed(effect);
        }

        if (!_settings.IsLightingEnabled) return Colors.Black;

        double brightness = Math.Clamp(_settings.Brightness, 0, 100) / 100.0;
        double cycle = (now - startedAt).TotalSeconds * EffectSpeedPolicy.CyclesPerSecond(effect, speed);
        Color effectColor = baseColor;
        double level = 1;

        switch (effect)
        {
            case LightingEffect.Static:
                break;
            case LightingEffect.Breathing:
                level = 0.16 + 0.84 * (0.5 + 0.5 * Math.Sin(cycle * Math.PI * 2));
                break;
            case LightingEffect.Rainbow:
                effectColor = HsvColorPicker.ColorFromHsv(cycle * 360, 1, 1);
                break;
            case LightingEffect.Pulse:
                level = 0.12 + 0.88 * Math.Pow(0.5 + 0.5 * Math.Sin(cycle * Math.PI * 2), 4);
                break;
            case LightingEffect.ColorCycle:
                (double hue, double saturation, double value) = HsvColorPicker.RgbToHsv(baseColor);
                Color[] palette =
                [
                    baseColor,
                    HsvColorPicker.ColorFromHsv(hue + 120, Math.Max(saturation, 0.85), Math.Max(value, 0.85)),
                    HsvColorPicker.ColorFromHsv(hue + 240, Math.Max(saturation, 0.85), Math.Max(value, 0.85))
                ];
                double position = cycle % palette.Length;
                if (position < 0) position += palette.Length;
                int index = (int)Math.Floor(position);
                double transition = position - index;
                transition = transition * transition * (3 - 2 * transition);
                effectColor = BlendColors(palette[index], palette[(index + 1) % palette.Length], transition);
                break;
        }

        return ScaleColor(effectColor, brightness * level);
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

    private static Color BlendColors(Color first, Color second, double amount) => Color.FromRgb(
        (byte)Math.Round(first.R + (second.R - first.R) * amount),
        (byte)Math.Round(first.G + (second.G - first.G) * amount),
        (byte)Math.Round(first.B + (second.B - first.B) * amount));

    private static Color ScaleColor(Color color, double amount) => Color.FromRgb(
        (byte)Math.Clamp((int)Math.Round(color.R * amount), 0, 255),
        (byte)Math.Clamp((int)Math.Round(color.G * amount), 0, 255),
        (byte)Math.Clamp((int)Math.Round(color.B * amount), 0, 255));

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
        Sc3FirmwareFlavor.Stock => "FW: Stock V22",
        Sc3FirmwareFlavor.Mod14 => "FW: RGB+ Mod 1.4",
        Sc3FirmwareFlavor.DiagnosticMod14 => "FW: Diagnostic Mod 1.4",
        Sc3FirmwareFlavor.Mod15 => "FW: RGB+ Mod 1.5",
        _ => "FW: Unknown"
    };

    private static string FirmwareReadyMessage(Sc3FirmwareFlavor flavor) => flavor switch
    {
        Sc3FirmwareFlavor.Mod15 => "RGB+ Mod 1.5 ready.",
        Sc3FirmwareFlavor.DiagnosticMod14 => "Diagnostic Mod 1.4 ready.",
        _ => "RGB+ Mod 1.4 ready."
    };

    private void ShowInlineStatus(string message) => TransferStatusText.Text = message;
}
