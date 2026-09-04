using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using SC3RGBController.Models;
using SC3RGBController.Services;
using SC3RGBController.UI.Controls;
using SC3RGBController.UI;
using SC3FirmwareTool.Core;

namespace SC3RGBController;

public partial class MainWindow : Window
{
    private readonly HidDeviceClient _hid = new();
    private readonly FirmwareService _firmwareService = new();
    private readonly object _colorGate = new();
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly DispatcherTimer _liveApplyTimer;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<ColorPreset> _presets;
    private CancellationTokenSource? _streamCancellation;
    private CancellationTokenSource? _applyFeedbackCancellation;
    private Task? _streamTask;
    private Color _selectedColor = Color.FromRgb(255, 120, 0);
    private Color _appliedColor = Color.FromRgb(255, 120, 0);
    private LightingEffect _selectedEffect = LightingEffect.Static;
    private DateTime _effectStartedAt = DateTime.UtcNow;
    private const double EffectSpeed = 0.55; // medium speed, kept intentionally UI-free
    private bool _syncingFields;
    private bool _isStreaming;
    private bool _isConnected;
    private bool _liveApplyPending;
    private bool _isAddingPreset;
    private bool _suppressPresetDirty;
    private bool _stopRestoredStockMode;
    private bool _firmwareChecked;
    private bool _modInstalled;
    private DeviceStatus? _firmwareStatus;
    private string? _selectedPresetId;
    private readonly bool _isStartupLaunch = Environment.GetCommandLineArgs()
        .Any(argument => string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        StartupManager.SetEnabled(_settings.StartWithWindows);
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
        SaveSettingsNow();

        if (_isStartupLaunch)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        _statusTimer.Start();
        await RefreshDeviceStatusAsync();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        _statusTimer.Stop();
        _settingsSaveTimer.Stop();
        _liveApplyTimer.Stop();
        _applyFeedbackCancellation?.Cancel();
        SaveSettingsNow();
        await StopStreamingAsync();
        _hid.Dispose();
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
        _hid.Close();
        await RefreshDeviceStatusAsync();
    }

    private async void ReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        ShowInlineStatus("Reconnecting…");
        if (_isStreaming) await StopStreamingAsync();
        _stopRestoredStockMode = false;
        await RefreshDeviceStatusAsync();
        ShowInlineStatus(_isConnected ? "Reconnected" : "SC3 not detected");
    }

    private void LightingButton_Click(object sender, RoutedEventArgs e)
    {
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
        StartupManager.SetEnabled(enabled);
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
        if (!_firmwareChecked || !_modInstalled) return;
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
            _hid.Close();
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
        _hid.Close();
        _isStreaming = false;
        StopButton.IsEnabled = _isConnected;
    }

    private async Task RefreshDeviceStatusAsync()
    {
        if (_isStreaming) return;

        (bool connected, string detail) = await Task.Run(() =>
        {
            bool connected = _hid.Probe(out string detail);
            return (connected, detail);
        });
        bool newlyConnected = connected && !_isConnected;
        UpdateConnectionVisual(connected, detail);

        if (!connected)
        {
            _firmwareChecked = false;
            _modInstalled = false;
            _firmwareStatus = null;
            FirmwareSetupButton.Visibility = Visibility.Collapsed;
        }
        else if (!_firmwareChecked)
        {
            DeviceStatus firmware = await Task.Run(_firmwareService.Detect);
            _firmwareChecked = true;
            _modInstalled = firmware.ModInstalled;
            _firmwareStatus = firmware;
            FirmwareVersionText.Text = firmware.ModInstalled ? "FW: RGB+ Mod 1.4" : "FW: Stock / unverified";
            FirmwareSetupButton.Visibility = firmware.ValidatedProfile && !firmware.ModInstalled
                ? Visibility.Visible : Visibility.Collapsed;
            DeviceHealthDetail.Text = firmware.ModInstalled ? "RGB+ Mod 1.4 ready." : firmware.Message;
        }

        UpdateConnectionVisual(connected, detail);

        if (connected && _modInstalled && (newlyConnected || !_stopRestoredStockMode))
        {
            if (_settings.IsLightingEnabled)
            {
                QueueLiveApply();
            }
            else
            {
                ApplySelectedColor();
            }
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
        Brush stateBrush = (Brush)FindResource(connected ? "ConnectedBrush" : "DisconnectedBrush");
        bool rgbReady = connected && _firmwareChecked && _modInstalled && _firmwareStatus?.ValidatedProfile == true;
        Brush readyBrush = (Brush)FindResource(rgbReady ? "ConnectedBrush" : "MutedBrush");
        StatusDot.Fill = stateBrush;
        DeviceHealthIcon.Stroke = stateBrush;
        SidebarConnectionText.Text = connected ? "Connected" : "Disconnected";
        SidebarConnectionText.Foreground = stateBrush;
        DeviceHealthDetail.Text = !connected ? "Device not detected." :
            !_firmwareChecked ? "Checking firmware state…" :
            _firmwareStatus?.ValidatedProfile == true && _modInstalled ? "RGB+ Mod 1.4 ready." :
            _firmwareStatus?.ValidatedProfile == true ? "SC3 is working normally. RGB setup required." :
            _firmwareStatus?.Message ?? "Firmware state is not verified.";
        FooterConnectionText.Text = connected ? "Connected to FIFINE SC3" : "FIFINE SC3 disconnected";
        FooterConnectionText.Foreground = stateBrush;
        FooterReadyText.Text = rgbReady ? "RGB Ready" : connected ? "RGB setup required" : "RGB unavailable";
        FooterReadyText.Foreground = readyBrush;
        ApplyButton.IsEnabled = true; // Saving presets works while disconnected.
        StopButton.IsEnabled = rgbReady || _isStreaming;
    }

    private async void FirmwareSetupButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult answer = MessageBox.Show(this,
            "RGB control requires installing compatible firmware on your SC3.\n\nDo not disconnect the mixer during setup.",
            "Enable RGB Control", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        _statusTimer.Stop();
        await StopStreamingAsync();
        _hid.Close();
        _firmwareChecked = false;
        _modInstalled = false;
        _firmwareStatus = null;
        UpdateConnectionVisual(_isConnected, "Firmware setup in progress");
        FirmwareSetupButton.Visibility = Visibility.Collapsed;
        ShowInlineStatus("Firmware setup in progress");
        FirmwareSetupWindow progress = new(_firmwareService) { Owner = this };
        bool? success = progress.ShowDialog();
        _firmwareChecked = false;
        _statusTimer.Start();
        await RefreshDeviceStatusAsync();
        if (success == true) ShowInlineStatus("RGB control ready");
        else if (progress.Outcome == UpdaterState.SetupFailedDeviceHealthy) ShowInlineStatus("RGB setup failed · SC3 is working normally");
        else if (progress.Outcome == UpdaterState.SetupFailedBootloaderAvailable) ShowInlineStatus("RGB setup incomplete · setup bootloader detected");
        else if (progress.Outcome == UpdaterState.RecoveryRequired) ShowInlineStatus("SC3 recovery required");
    }

    private void UpdateLightingVisual()
    {
        bool on = _settings.IsLightingEnabled;
        Brush brush = (Brush)FindResource(on ? "ConnectedBrush" : "MutedBrush");
        LightingIconPath.Stroke = brush;
        LightingTitleText.Text = on ? "Lighting" : "Lighting Off";
        LightingDetailText.Text = on ? "RGB lighting is on." : "RGB lighting is disabled.";
        LightingDetailText.Foreground = brush;
        LightingButton.Opacity = on ? 1 : 0.7;
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
        if (!_settings.IsLightingEnabled || !_firmwareChecked || !_modInstalled) return;
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
    }

    private Color GetEffectColor(DateTime now)
    {
        Color baseColor;
        LightingEffect effect;
        DateTime startedAt;
        lock (_colorGate)
        {
            baseColor = _selectedColor;
            effect = _selectedEffect;
            startedAt = _effectStartedAt;
        }

        if (!_settings.IsLightingEnabled) return Colors.Black;

        double brightness = Math.Clamp(_settings.Brightness, 0, 100) / 100.0;
        double cycle = (now - startedAt).TotalSeconds * EffectSpeed;
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

    private void ShowInlineStatus(string message) => TransferStatusText.Text = message;
}
