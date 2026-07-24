using System.Runtime.Versioning;
using System.Threading.Channels;
using ApexLab.Application.Capture;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Persistence.Raw;

public enum RawReplayTimingMode
{
    Immediate,
    Recorded,
}

public sealed class RawReplayOptions
{
    public RawReplayOptions(
        RawReplayTimingMode timingMode = RawReplayTimingMode.Immediate,
        int speedPermille = 1_000,
        int channelCapacity = 64)
    {
        if (!Enum.IsDefined(timingMode))
        {
            throw new ArgumentOutOfRangeException(nameof(timingMode));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(
            speedPermille,
            100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            speedPermille,
            100_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            channelCapacity,
            1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            channelCapacity,
            4_096);

        TimingMode = timingMode;
        SpeedPermille = speedPermille;
        ChannelCapacity = channelCapacity;
    }

    public RawReplayTimingMode TimingMode { get; }

    public int SpeedPermille { get; }

    public int ChannelCapacity { get; }
}

[SupportedOSPlatform("windows")]
public sealed class RawReplayDatagramSource : IDatagramSource
{
    private readonly object _gate = new();
    private readonly RawEvidenceCapture _capture;
    private readonly RawReplayOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ReplayDelayAsync _delayAsync;
    private readonly Channel<DatagramEnvelope> _channel;
    private CancellationTokenSource? _stopCancellation;
    private Task? _pumpTask;
    private Task? _stopTask;
    private Task? _disposeTask;
    private ReplayState _state;
    private long _enqueued;

    public RawReplayDatagramSource(
        RawEvidenceCapture capture,
        RawReplayOptions? options = null,
        TimeProvider? timeProvider = null)
        : this(
            capture,
            options ?? new RawReplayOptions(),
            timeProvider ?? TimeProvider.System,
            static (delay, provider, token) =>
                Task.Delay(delay, provider, token))
    {
    }

    internal RawReplayDatagramSource(
        RawEvidenceCapture capture,
        RawReplayOptions options,
        TimeProvider timeProvider,
        ReplayDelayAsync delayAsync)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(delayAsync);
        _capture = capture;
        _options = options;
        _timeProvider = timeProvider;
        _delayAsync = delayAsync;
        _channel = Channel.CreateBounded<DatagramEnvelope>(
            new BoundedChannelOptions(options.ChannelCapacity)
            {
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true,
            });
    }

    public ChannelReader<DatagramEnvelope> Output => _channel.Reader;

    public DatagramSourceCounters Counters
    {
        get
        {
            var enqueued = Interlocked.Read(ref _enqueued);
            return new DatagramSourceCounters(
                enqueued,
                enqueued,
                sourceDroppedFull: 0,
                sourceRejectedOversized: 0,
                socketErrors: 0);
        }
    }

    public Task StartAsync(
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        lock (_gate)
        {
            if (_state == ReplayState.Started)
            {
                return Task.CompletedTask;
            }

            if (_state != ReplayState.Created)
            {
                return Task.FromException(
                    new InvalidOperationException(
                        "A stopped replay source cannot be restarted."));
            }

            _state = ReplayState.Started;
            _stopCancellation = new CancellationTokenSource();
            _pumpTask = PumpAsync(_stopCancellation.Token);
            return Task.CompletedTask;
        }
    }

    public Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        Task stopTask;
        lock (_gate)
        {
            if (_stopTask is not null)
            {
                stopTask = _stopTask;
            }
            else if (_state == ReplayState.Created)
            {
                _state = ReplayState.Stopped;
                _channel.Writer.TryComplete();
                stopTask = Task.CompletedTask;
                _stopTask = stopTask;
            }
            else if (_state is ReplayState.Stopped
                     or ReplayState.Disposed)
            {
                stopTask = Task.CompletedTask;
                _stopTask = stopTask;
            }
            else
            {
                _state = ReplayState.Stopping;
                _stopCancellation!.Cancel();
                stopTask = StopCoreAsync(_pumpTask!);
                _stopTask = stopTask;
            }
        }

        return cancellationToken.CanBeCanceled
            ? stopTask.WaitAsync(cancellationToken)
            : stopTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    internal static long CalculateTargetElapsedTicks(
        long arrivalDelta,
        long recordedStopwatchFrequency,
        int speedPermille)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(arrivalDelta);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            recordedStopwatchFrequency,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            speedPermille,
            100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            speedPermille,
            100_000);
        var numerator = checked(
            (Int128)arrivalDelta
            * TimeSpan.TicksPerSecond
            * 1_000);
        var denominator = checked(
            (Int128)recordedStopwatchFrequency
            * speedPermille);
        var rounded = checked(
            (numerator + (denominator / 2))
            / denominator);
        if (rounded > TimeSpan.MaxValue.Ticks)
        {
            throw new OverflowException(
                "The replay delay exceeds the TimeSpan domain.");
        }

        return checked((long)rounded);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        Exception? failure = null;
        try
        {
            long? firstArrival = null;
            var replayStarted = _timeProvider.GetTimestamp();
            await foreach (var envelope in _capture.ReadAllAsync(
                               cancellationToken)
                               .ConfigureAwait(false))
            {
                if (_options.TimingMode
                    == RawReplayTimingMode.Recorded)
                {
                    firstArrival ??= envelope.MonotonicTimestamp;
                    var targetTicks = CalculateTargetElapsedTicks(
                        checked(
                            envelope.MonotonicTimestamp
                            - firstArrival.Value),
                        _capture.StopwatchFrequency,
                        _options.SpeedPermille);
                    var remaining = TimeSpan.FromTicks(targetTicks)
                        - _timeProvider.GetElapsedTime(replayStarted);
                    if (remaining > TimeSpan.Zero)
                    {
                        await _delayAsync(
                            remaining,
                            _timeProvider,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                await _channel.Writer.WriteAsync(
                    envelope,
                    cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _enqueued);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            _channel.Writer.TryComplete(failure);
            lock (_gate)
            {
                if (_state != ReplayState.Disposed)
                {
                    _state = ReplayState.Stopped;
                }
            }
        }
    }

    private async Task StopCoreAsync(Task pumpTask)
    {
        await pumpTask.ConfigureAwait(false);
        lock (_gate)
        {
            if (_state != ReplayState.Disposed)
            {
                _state = ReplayState.Stopped;
            }
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
            await _capture.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _state = ReplayState.Disposed;
            }

            _stopCancellation?.Dispose();
        }
    }

    private enum ReplayState
    {
        Created,
        Started,
        Stopping,
        Stopped,
        Disposed,
    }
}

internal delegate Task ReplayDelayAsync(
    TimeSpan delay,
    TimeProvider timeProvider,
    CancellationToken cancellationToken);
