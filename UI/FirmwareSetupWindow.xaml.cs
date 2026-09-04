using System.ComponentModel;
using System.Windows;
using SC3FirmwareTool.Core;

namespace SC3RGBController.UI;

public partial class FirmwareSetupWindow : Window
{
    private readonly FirmwareService _service;
    private bool _running = true;
    private bool _success;
    public UpdaterState Outcome { get; private set; } = UpdaterState.Idle;
    public FirmwareSetupWindow(FirmwareService service)
    {
        InitializeComponent();
        _service = service;
        _service.ProgressChanged += OnProgress;
        Loaded += async (_, _) => await InstallAsync();
        Closing += OnClosing;
    }

    private void OnProgress(UpdateProgress value) => Dispatcher.Invoke(() =>
    {
        if (value.State is UpdaterState.SetupFailedDeviceHealthy or UpdaterState.SetupFailedBootloaderAvailable or
            UpdaterState.RecoveryRequired or UpdaterState.SetupSucceeded)
            Outcome = value.State;
        Progress.Value = value.Percent;
        StageText.Text = value.State switch
        {
            UpdaterState.ValidatingDevice or UpdaterState.ValidatingFirmware or UpdaterState.EnteringBootloader => "Preparing",
            UpdaterState.Erasing or UpdaterState.PreparingUpdate or UpdaterState.Transferring or UpdaterState.Finalizing => "Installing firmware",
            UpdaterState.WaitingForBootloader or UpdaterState.WaitingForReboot => "Restarting SC3",
            UpdaterState.VerifyingDevice => "Verifying",
            UpdaterState.Success => "Ready",
            UpdaterState.SetupSucceeded => "Ready",
            UpdaterState.SetupFailedDeviceHealthy => "RGB setup failed",
            UpdaterState.SetupFailedBootloaderAvailable => "RGB setup incomplete",
            UpdaterState.RecoveryRequired => "SC3 recovery required",
            _ => value.Message
        };
        DetailText.Text = value.TotalBlocks > 0
            ? $"{value.CurrentBlock:N0} / {value.TotalBlocks:N0} blocks · Do not disconnect the mixer."
            : value.Message;
    });

    private async Task InstallAsync()
    {
        try
        {
            await _service.InstallRgbAsync(ReleasePolicy.BuildId);
            _success = true;
        }
        catch (Exception ex)
        {
            if (ex is FirmwareUpdateException firmwareError) Outcome = firmwareError.Outcome;
            DetailText.Text = Outcome switch
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
