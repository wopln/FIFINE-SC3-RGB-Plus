using SC3RGBController.Services.Actions;

namespace SC3RGBController.Services;

public enum CustomShortcutRuntimeState { Stock, Active, Unavailable }
public sealed record CustomShortcutState(CustomShortcutRuntimeState State, string Message);
public sealed record CustomButtonReadResult(bool Success, Sc3QueryReply Reply, string Detail, bool Cancelled = false);
public sealed record CustomButtonDrainResult(bool Success, int DrainedReports, string Detail, bool Cancelled = false);

public interface ISc3CustomButtonTransport
{
    bool TryOpen(out string detail);
    bool TryQuerySc3(out Sc3QueryReply reply, out string detail);
    bool TryBeginCustomButtonEventSession(out Sc3QueryReply reply, out string detail) =>
        TryQuerySc3(out reply, out detail);
    Task<CustomButtonDrainResult> DrainCustomButtonEventsAsync(CancellationToken token);
    Task<CustomButtonReadResult> ReadCustomButtonEventAsync(CancellationToken token);
    bool TrySetCustomButtonMode(bool enabled, out string detail);
}

public sealed class CustomButtonShortcutController : IAsyncDisposable
{
    private readonly ISc3CustomButtonTransport _transport;
    private readonly Func<CustomButtonId, string?> _pathResolver;
    private readonly IApplicationShortcutLauncher _launcher;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _retryInterval;
    private readonly bool _eventDrivenTransportAllowed;
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
        : this(hid, pathResolver, new CustomButtonActionRunner(), TimeSpan.Zero, TimeSpan.FromSeconds(5),
            eventDrivenTransportAllowed: CustomShortcutStabilityPolicy.EventDrivenTransportEnabled)
    {
        // Audio/media actions report their own result ("Discord muted", "System volume 40%").
        if (_launcher is CustomButtonActionRunner runner)
            runner.Feedback += (_, message) => ActionStatus?.Invoke(this, message);
    }

    public CustomButtonShortcutController(
        ISc3CustomButtonTransport transport,
        Func<CustomButtonId, string?> pathResolver,
        IApplicationShortcutLauncher launcher,
        TimeSpan pollInterval,
        TimeSpan retryInterval,
        bool eventDrivenTransportAllowed = true)
    {
        _transport = transport;
        _pathResolver = pathResolver;
        _launcher = launcher;
        _pollInterval = pollInterval;
        _retryInterval = retryInterval;
        _eventDrivenTransportAllowed = eventDrivenTransportAllowed;
    }

    public async Task SetPreferenceAsync(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (enabled && !_eventDrivenTransportAllowed)
        {
            _desiredEnabled = false;
            await StopAsync(sendOff: false);
            SetUnavailable();
            ActionStatus?.Invoke(this, CustomShortcutStabilityPolicy.Message);
            return;
        }
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
            if (!await TryEstablishShortcutSessionAsync(token))
            {
                SetUnavailable();
                if (!await DelayRetryAsync(token)) return;
                continue;
            }

            while (!token.IsCancellationRequested && _desiredEnabled && IsActive)
            {
                CustomButtonReadResult read = await _transport.ReadCustomButtonEventAsync(token);
                if (read.Cancelled)
                    return;
                Sc3QueryReply reply = read.Reply;
                if (!read.Success || !reply.SupportsFinalCustomShortcuts || reply.RuntimeMode != 1)
                {
                    IsActive = false;
                    SetUnavailable();
                    break;
                }

                HandleEventReply(reply);
                // Real interrupt reads wait for a release. This tiny yield also
                // keeps deterministic fake transports from becoming a busy loop.
                if (_pollInterval > TimeSpan.Zero)
                    try { await Task.Delay(_pollInterval, token); }
                    catch (OperationCanceledException) { return; }
            }

            if (!token.IsCancellationRequested && _desiredEnabled && !await DelayRetryAsync(token))
                return;
        }
    }

    private async Task<bool> TryEstablishShortcutSessionAsync(CancellationToken token)
    {
        if (!_transport.TryOpen(out _) ||
            !_transport.TryBeginCustomButtonEventSession(out Sc3QueryReply baseline, out _))
            return false;
        if (!baseline.SupportsFinalCustomShortcuts)
            return false;

        // A process can exit unexpectedly while firmware is still in shortcut
        // mode. Return to STOCK first, then drain any already-buffered interrupt
        // reports before enabling a new session. This avoids endpoint resets in
        // firmware and prevents a stale press from launching on restart.
        if (!_transport.TrySetCustomButtonMode(false, out _))
            return false;
        CustomButtonDrainResult drained = await _transport.DrainCustomButtonEventsAsync(token);
        if (!drained.Success || drained.Cancelled)
            return false;

        _tracker.ArmForInterruptEvents();
        if (!_transport.TrySetCustomButtonMode(true, out _))
            return false;
        _sessionCommandActive = true;

        IsActive = true;
        SetState(CustomShortcutRuntimeState.Active);
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

        string? assignment = _pathResolver(button);
        ShortcutLaunchResult result = _launcher.Launch(
            IsActive && _desiredEnabled,
            assignment);
        // Audio/media actions report their own feedback; these messages are for launches only.
        if (!CustomButtonActionRunner.IsLaunchKind(CustomButtonAction.Parse(assignment).Kind))
            return;
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
