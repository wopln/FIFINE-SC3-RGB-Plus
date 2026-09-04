using System.ComponentModel;
using System.Windows;
using SC3FirmwareTool.Core;

namespace SC3RGBController.UI;

public enum FirmwareWindowMode
{
    InstallRgb,
    RestoreStock
}

public partial class FirmwareSetupWindow : Window
{
    private readonly FirmwareService _service;
    private readonly FirmwareWindowMode _mode;
    private bool _running = true;
    private bool _success;
    public UpdaterState Outcome { get; private set; } = UpdaterState.Idle;

    public FirmwareSetupWindow(FirmwareService service, FirmwareWindowMode mode = FirmwareWindowMode.InstallRgb)
    {
        InitializeComponent();
        _service = service;
        _mode = mode;
        if (_mode == FirmwareWindowMode.RestoreStock)
        {
            Title = "Restore Original Firmware";
            HeadingText.Text = "Restoring your SC3";
            StageText.Text = "Preparing SC3";
            DetailText.Text = "Do not disconnect the SC3 until restoration is complete.";
        }
        _service.ProgressChanged += OnProgress;
        Loaded += async (_, _) => await RunAsync();
        Closing += OnClosing;
    }

    private void OnProgress(UpdateProgress value) => Dispatcher.Invoke(() =>
    {
        if (value.State is UpdaterState.SetupFailedDeviceHealthy or UpdaterState.SetupFailedBootloaderAvailable or
            UpdaterState.RecoveryRequired or UpdaterState.SetupSucceeded or UpdaterState.RestoreFailedDeviceHealthy or
            UpdaterState.RestoreFailedBootloaderAvailable or UpdaterState.RestoreRecoveryRequired or UpdaterState.RestoreSucceeded)
            Outcome = value.State;

        Progress.Value = value.Percent;
        StageText.Text = _mode == FirmwareWindowMode.RestoreStock
            ? RestoreStage(value)
            : InstallStage(value);
        DetailText.Text = _mode == FirmwareWindowMode.RestoreStock
            ? RestoreDetail(value)
            : value.TotalBlocks > 0
                ? $"{value.CurrentBlock:N0} / {value.TotalBlocks:N0} blocks · Do not disconnect the mixer."
                : value.Message;
    });

    private static string InstallStage(UpdateProgress value) => value.State switch
    {
        UpdaterState.ValidatingDevice or UpdaterState.ValidatingFirmware or UpdaterState.EnteringBootloader => "Preparing",
        UpdaterState.Erasing or UpdaterState.PreparingUpdate or UpdaterState.Transferring or UpdaterState.Finalizing => "Installing firmware",
        UpdaterState.WaitingForBootloader or UpdaterState.WaitingForReboot => "Restarting SC3",
        UpdaterState.VerifyingDevice => "Verifying",
        UpdaterState.Success or UpdaterState.SetupSucceeded => "Ready",
        UpdaterState.SetupFailedDeviceHealthy => "RGB setup failed",
        UpdaterState.SetupFailedBootloaderAvailable => "RGB setup incomplete",
        UpdaterState.RecoveryRequired => "SC3 recovery required",
        _ => value.Message
    };

    private static string RestoreStage(UpdateProgress value) => value.State switch
    {
        UpdaterState.ValidatingDevice or UpdaterState.ValidatingFirmware or UpdaterState.EnteringBootloader or
            UpdaterState.WaitingForBootloader or UpdaterState.BootloaderConnected => "Preparing SC3",
        UpdaterState.Erasing or UpdaterState.PreparingUpdate or UpdaterState.Transferring or UpdaterState.Finalizing => "Restoring original firmware",
        UpdaterState.WaitingForReboot => "Restarting SC3",
        UpdaterState.VerifyingDevice => "Verifying",
        UpdaterState.RestoreSucceeded => "Done",
        UpdaterState.RestoreFailedDeviceHealthy => "Restore failed",
        UpdaterState.RestoreFailedBootloaderAvailable => "Recovery mode",
        UpdaterState.RestoreRecoveryRequired => "Recovery required",
        _ => value.Message
    };

    private static string RestoreDetail(UpdateProgress value) => value.State switch
    {
        UpdaterState.RestoreSucceeded => "Original FIFINE SC3 firmware is installed.",
        UpdaterState.RestoreFailedDeviceHealthy => "Restore failed, but your SC3 is working normally. Details were saved in the log.",
        UpdaterState.RestoreFailedBootloaderAvailable => "SC3 is still in recovery mode. You can retry Restore Original Firmware safely.",
        UpdaterState.RestoreRecoveryRequired => "The SC3 could not be verified in normal or recovery mode. Recovery is required.",
        UpdaterState.VerifyingDevice => "Confirming original firmware and device state.",
        UpdaterState.WaitingForReboot => "The SC3 is restarting. Do not disconnect it.",
        _ => "Do not disconnect the SC3 until restoration is complete."
    };

    private async Task RunAsync()
    {
        try
        {
            if (_mode == FirmwareWindowMode.RestoreStock)
                await _service.RestoreStockFirmwareAsync(StockRecoveryPolicy.Confirmation);
            else
                await _service.InstallRgbAsync(ReleasePolicy.BuildId);
            _success = true;
        }
        catch (Exception ex)
        {
            if (ex is FirmwareUpdateException firmwareError) Outcome = firmwareError.Outcome;
            DetailText.Text = _mode == FirmwareWindowMode.RestoreStock
                ? Outcome switch
                {
                    UpdaterState.RestoreFailedDeviceHealthy => "Restore failed, but your SC3 is working normally. Details were saved in the log.",
                    UpdaterState.RestoreFailedBootloaderAvailable => "SC3 is still in recovery mode. Close this window and choose Retry Restore.",
                    UpdaterState.RestoreRecoveryRequired => "The SC3 could not be verified in normal or recovery mode. Recovery is required.",
                    _ => ex.Message
                }
                : Outcome switch
                {
                    UpdaterState.SetupFailedDeviceHealthy => "RGB setup failed. SC3 is still working normally. Details were saved in the log.",
                    UpdaterState.SetupFailedBootloaderAvailable => "RGB setup did not complete. The known SC3 setup bootloader is still available. No automatic retry was attempted.",
                    UpdaterState.RecoveryRequired => "The update did not complete and the SC3 could not be proven healthy or safely retryable. Recovery is required.",
                    _ => ex.Message
                };
        }
        finally
        {
            _running = false;
            _service.ProgressChanged -= OnProgress;
            if (_success)
                DialogResult = true;
            else
                CloseButton.IsEnabled = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e) { if (_running) e.Cancel = true; }
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (_success) DialogResult = true; else Close();
    }
}