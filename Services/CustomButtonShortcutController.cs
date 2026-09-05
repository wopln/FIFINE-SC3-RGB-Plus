namespace SC3RGBController.Services;

public enum CustomShortcutRuntimeState { Stock, Active, Unavailable }
public sealed record CustomShortcutState(CustomShortcutRuntimeState State, string Message);

public interface ISc3CustomButtonTransport
{
    bool TryOpen(out string detail);
    bool TryQuerySc3(out Sc3QueryReply reply, out string detail);
    bool TrySetCustomButtonMode(bool enabled, out string detail);
}

public sealed class CustomButtonShortcutController : IAsyncDisposable
{
    private readonly ISc3CustomButtonTransport _transport;
    private readonly Func<CustomButtonId, string?> _pathResolver;
    private readonly IApplicationShortcutLauncher _launcher;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _retryInterval;
    private readonly CustomButtonEventTracker _tracker = new();
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private bool _desiredEnabled;
    private bool _sessionCommandActive;
    private bool _disposed;

    public event EventHandler<CustomShortcutState>? StateChanged;
    public event EventHandler<string>? ActionStatus;
    public bool DesiredEnabled => _desiredEnabled;
    public bool IsActive { get; private set; }
    public bool IsRunning => _pollTask is { IsCompleted: false };

    public CustomButtonShortcutController(HidDeviceClient hid, Func<CustomButtonId, string?> pathResolver)
        : this(hid, pathResolver, new ApplicationShortcutLauncher(), TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1))
    {
    }

    public CustomButtonShortcutController(
        ISc3CustomButtonTransport transport,
        Func<CustomButtonId, string?> pathResolver,
        IApplicationShortcutLauncher launcher,
        TimeSpan pollInterval,
        TimeSpan retryInterval)
    {
        _transport = transport;
        _pathResolver = pathResolver;
        _launcher = launcher;
        _pollInterval = pollInterval;
        _retryInterval = retryInterval;
    }

    public async Task SetPreferenceAsync(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _desiredEnabled = enabled;
        if (!enabled)
        {
            await StopAsync(sendOff: true);
            return;
        }

        await StartAsync();
    }

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _stateGate.WaitAsync();
        try
        {
            if (!_desiredEnabled || _pollTask is { IsCompleted: false })
                return;

            _pollTask = null;
            _cts?.Dispose();
            _tracker.Reset();
            CancellationTokenSource cts = new();
            _cts = cts;
            _pollTask = Task.Run(() => RunAsync(cts.Token));
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _desiredEnabled)
        {
            if (!TryEstablishShortcutSession())
            {
                SetUnavailable();
                if (!await DelayRetryAsync(token)) return;
                continue;
            }

            while (!token.IsCancellationRequested && _desiredEnabled && IsActive)
            {
                if (!_transport.TryQuerySc3(out Sc3QueryReply reply, out _) ||
                    !reply.SupportsFinalCustomShortcuts || reply.RuntimeMode != 1)
                {
                    IsActive = false;
                    SetUnavailable();
                    break;
                }

                HandleEventReply(reply);
                try
                {
                    await Task.Delay(_pollInterval, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (!token.IsCancellationRequested && _desiredEnabled && !await DelayRetryAsync(token))
                return;
        }
    }

    private bool TryEstablishShortcutSession()
    {
        if (!_transport.TryOpen(out _) || !_transport.TryQuerySc3(out Sc3QueryReply baseline, out _))
            return false;
        if (!baseline.SupportsFinalCustomShortcuts)
            return false;

        _tracker.SetBaseline(baseline.Counter);
        if (!_transport.TrySetCustomButtonMode(true, out _))
            return false;
        _sessionCommandActive = true;

        if (!_transport.TryQuerySc3(out Sc3QueryReply confirmed, out _) ||
            !confirmed.SupportsFinalCustomShortcuts || confirmed.RuntimeMode != 1)
            return false;

        IsActive = true;
        SetState(CustomShortcutRuntimeState.Active);
        HandleEventReply(confirmed);
        return true;
    }

    private async Task<bool> DelayRetryAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_retryInterval, token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void HandleEventReply(Sc3QueryReply reply)
    {
        if (!_tracker.TryAccept(reply.KeyId, reply.Counter, out CustomButtonId button))
            return;

        ShortcutLaunchResult result = _launcher.Launch(
            IsActive && _desiredEnabled,
            _pathResolver(button));
        if (result == ShortcutLaunchResult.MissingTarget)
            ActionStatus?.Invoke(this, "Application not found");
        else if (result is ShortcutLaunchResult.Failed or ShortcutLaunchResult.UnsupportedTarget)
            ActionStatus?.Invoke(this, "Unable to launch application");
    }

    public async Task StopAsync(bool sendOff)
    {
        CancellationTokenSource? cts;
        Task? task;
        await _stateGate.WaitAsync();
        try
        {
            cts = _cts;
            task = _pollTask;
            _cts = null;
            _pollTask = null;
            cts?.Cancel();
        }
        finally
        {
            _stateGate.Release();
        }

        if (task is not null && task.Id != Task.CurrentId)
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }
        cts?.Dispose();

        if (sendOff && _sessionCommandActive && _transport.TryOpen(out _) &&
            _transport.TrySetCustomButtonMode(false, out _))
            _sessionCommandActive = false;

        IsActive = false;
        _tracker.Reset();
        SetState(_desiredEnabled ? CustomShortcutRuntimeState.Unavailable : CustomShortcutRuntimeState.Stock);
    }

    private void SetUnavailable()
    {
        IsActive = false;
        SetState(CustomShortcutRuntimeState.Unavailable);
    }

    private void SetState(CustomShortcutRuntimeState state) =>
        StateChanged?.Invoke(this, new(state, CustomShortcutStatusPolicy.For(state)));

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _desiredEnabled = false;
        await StopAsync(sendOff: true);
        _disposed = true;
        _stateGate.Dispose();
    }
}
