using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ApexLab.Application.Capture;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Telemetry.Udp;

internal delegate ValueTask<SocketReceiveMessageFromResult> DatagramReceiveAsync(
    Socket socket,
    Memory<byte> buffer,
    EndPoint remoteEndpoint,
    CancellationToken cancellationToken);

public sealed class UdpDatagramSource : IDatagramSource
{
    private readonly object _lifecycleGate = new();
    private readonly object _counterGate = new();
    private readonly UdpDatagramSourceOptions _options;
    private readonly Func<Socket> _socketFactory;
    private readonly DatagramReceiveAsync _receiveAsync;
    private readonly Channel<DatagramEnvelope> _channel;
    private SourceState _state;
    private bool _disposeRequested;
    private Socket? _socket;
    private CancellationTokenSource? _stopCancellation;
    private Task? _receiveTask;
    private Task? _stopTask;
    private Task? _disposeTask;
    private IPEndPoint? _localEndpoint;
    private Exception? _terminalError;
    private long _nextSequence;
    private long _sourceEnqueued;
    private long _sourceDroppedFull;
    private long _sourceRejectedOversized;
    private long _socketErrors;

    public UdpDatagramSource(UdpDatagramSourceOptions? options = null)
        : this(
            options ?? new UdpDatagramSourceOptions(),
            CreateSocket,
            ReceiveFromSocketAsync)
    {
    }

    internal UdpDatagramSource(
        UdpDatagramSourceOptions options,
        Func<Socket> socketFactory)
        : this(options, socketFactory, ReceiveFromSocketAsync)
    {
    }

    internal UdpDatagramSource(
        UdpDatagramSourceOptions options,
        Func<Socket> socketFactory,
        DatagramReceiveAsync receiveAsync)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(socketFactory);
        ArgumentNullException.ThrowIfNull(receiveAsync);

        _options = options;
        _socketFactory = socketFactory;
        _receiveAsync = receiveAsync;
        _channel = Channel.CreateBounded<DatagramEnvelope>(
            new BoundedChannelOptions(_options.ChannelCapacity)
            {
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true,
            });
    }

    public ChannelReader<DatagramEnvelope> Output => _channel.Reader;

    public IPEndPoint? LocalEndpoint
    {
        get
        {
            lock (_lifecycleGate)
            {
                return CopyEndpoint(_localEndpoint);
            }
        }
    }

    public DatagramSourceCounters Counters
    {
        get
        {
            lock (_counterGate)
            {
                var datagramsObserved = checked(
                    _sourceEnqueued
                    + _sourceDroppedFull
                    + _sourceRejectedOversized);
                return new DatagramSourceCounters(
                    datagramsObserved,
                    _sourceEnqueued,
                    _sourceDroppedFull,
                    _sourceRejectedOversized,
                    _socketErrors);
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        lock (_lifecycleGate)
        {
            if (_disposeRequested)
            {
                return Task.FromException(
                    new ObjectDisposedException(nameof(UdpDatagramSource)));
            }

            if (_state == SourceState.Started)
            {
                return Task.CompletedTask;
            }

            if (_state is SourceState.Stopping
                or SourceState.Stopped
                or SourceState.Faulted)
            {
                return Task.FromException(
                    new InvalidOperationException(
                        "A stopped or faulted UDP datagram source cannot be restarted.",
                        _terminalError));
            }

            Socket? socket = null;
            try
            {
                socket = _socketFactory()
                    ?? throw new InvalidOperationException(
                        "The UDP socket factory returned no socket.");
                socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(IPAddress.Loopback, _options.Port));
            }
            catch (Exception exception)
            {
                socket?.Dispose();
                return Task.FromException(exception);
            }

            var stopCancellation = new CancellationTokenSource();
            _socket = socket;
            _stopCancellation = stopCancellation;
            _localEndpoint = CopyEndpoint((IPEndPoint?)socket.LocalEndPoint);
            _state = SourceState.Started;
            _receiveTask = ReceiveLoopAsync(
                socket,
                stopCancellation);
            return Task.CompletedTask;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task stopTask;
        lock (_lifecycleGate)
        {
            if (_stopTask is not null)
            {
                stopTask = _stopTask;
            }
            else if (_state == SourceState.Created)
            {
                _state = SourceState.Stopped;
                _channel.Writer.TryComplete();
                stopTask = Task.CompletedTask;
                _stopTask = stopTask;
            }
            else if (_state is SourceState.Stopped
                     or SourceState.Faulted
                     or SourceState.Disposed)
            {
                stopTask = Task.CompletedTask;
                _stopTask = stopTask;
            }
            else
            {
                _state = SourceState.Stopping;
                stopTask = StopCoreAsync(
                    _socket!,
                    _stopCancellation!,
                    _receiveTask!);
                _stopTask = stopTask;
            }
        }

        return cancellationToken.CanBeCanceled
            ? stopTask.WaitAsync(cancellationToken)
            : stopTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            _disposeRequested = true;
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private static Socket CreateSocket() =>
        new(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);

    private async Task ReceiveLoopAsync(
        Socket socket,
        CancellationTokenSource stopCancellation)
    {
        await Task.Yield();
        var cancellationToken = stopCancellation.Token;
        var sentinelLength = checked(_options.MaximumDatagramBytes + 1);
        byte[]? buffer = null;
        Exception? completionError = null;
        try
        {
            buffer = ArrayPool<byte>.Shared.Rent(sentinelLength);
            while (!cancellationToken.IsCancellationRequested)
            {
                SocketReceiveMessageFromResult result;
                try
                {
                    result = await _receiveAsync(
                        socket,
                        buffer.AsMemory(0, sentinelLength),
                        new IPEndPoint(IPAddress.Any, 0),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException exception)
                    when (exception.SocketErrorCode == SocketError.MessageSize)
                {
                    _ = NextSequence();
                    RecordOversized();
                    continue;
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException exception)
                    when (cancellationToken.IsCancellationRequested &&
                          IsExpectedStopError(exception.SocketErrorCode))
                {
                    break;
                }
                var sequence = NextSequence();
                if (result.ReceivedBytes >= sentinelLength ||
                    (result.SocketFlags & SocketFlags.Truncated) != 0)
                {
                    RecordOversized();
                    continue;
                }

                if (result.RemoteEndPoint is not IPEndPoint senderEndpoint)
                {
                    RecordSocketError();
                    completionError = new SocketException(
                        (int)SocketError.AddressFamilyNotSupported);
                    break;
                }

                var envelope = DatagramEnvelope.CopyFrom(
                    sequence,
                    Stopwatch.GetTimestamp(),
                    DateTimeOffset.UtcNow,
                    new DatagramSender(senderEndpoint.Address, senderEndpoint.Port),
                    buffer.AsSpan(0, result.ReceivedBytes));
                RecordAdmission(envelope);
            }
        }
        catch (Exception exception)
        {
            if (exception is SocketException or ObjectDisposedException)
            {
                RecordSocketError();
            }

            completionError = exception;
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            RetireReceiveLoop(socket, stopCancellation, completionError);
        }
    }

    private async Task StopCoreAsync(
        Socket socket,
        CancellationTokenSource stopCancellation,
        Task receiveTask)
    {
        try
        {
            stopCancellation.Cancel();
            socket.Dispose();
            await receiveTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lock (_lifecycleGate)
            {
                _terminalError ??= exception;
            }

            throw;
        }
        finally
        {
            socket.Dispose();
            lock (_lifecycleGate)
            {
                if (ReferenceEquals(_socket, socket))
                {
                    _socket = null;
                    _stopCancellation = null;
                    _receiveTask = null;
                    _localEndpoint = null;
                }

                if (_state != SourceState.Disposed)
                {
                    _state = _terminalError is null
                        ? SourceState.Stopped
                        : SourceState.Faulted;
                }
            }

            stopCancellation.Dispose();
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_lifecycleGate)
            {
                _state = SourceState.Disposed;
            }
        }
    }

    private long NextSequence()
    {
        lock (_counterGate)
        {
            _nextSequence = checked(_nextSequence + 1);
            return _nextSequence;
        }
    }

    private void RecordAdmission(DatagramEnvelope envelope)
    {
        lock (_counterGate)
        {
            if (_sourceEnqueued == long.MaxValue ||
                _sourceDroppedFull == long.MaxValue)
            {
                throw new OverflowException(
                    "UDP source admission counters reached Int64 capacity.");
            }

            if (_channel.Writer.TryWrite(envelope))
            {
                _sourceEnqueued++;
            }
            else
            {
                _sourceDroppedFull++;
            }
        }
    }

    private void RecordOversized()
    {
        lock (_counterGate)
        {
            _sourceRejectedOversized = checked(_sourceRejectedOversized + 1);
        }
    }

    private void RecordSocketError()
    {
        lock (_counterGate)
        {
            _socketErrors = checked(_socketErrors + 1);
        }
    }

    private void RetireReceiveLoop(
        Socket socket,
        CancellationTokenSource stopCancellation,
        Exception? terminalError)
    {
        lock (_lifecycleGate)
        {
            _terminalError ??= terminalError;
            if (ReferenceEquals(_socket, socket) &&
                _state == SourceState.Started)
            {
                socket.Dispose();
                stopCancellation.Dispose();
                _channel.Writer.TryComplete(terminalError);
                _socket = null;
                _stopCancellation = null;
                _receiveTask = null;
                _localEndpoint = null;
                _state = terminalError is null
                    ? SourceState.Stopped
                    : SourceState.Faulted;
                return;
            }

            _channel.Writer.TryComplete(terminalError);
        }
    }

    private static bool IsExpectedStopError(SocketError error) =>
        error is SocketError.Interrupted
            or SocketError.OperationAborted
            or SocketError.NotSocket;

    private static ValueTask<SocketReceiveMessageFromResult> ReceiveFromSocketAsync(
        Socket socket,
        Memory<byte> buffer,
        EndPoint remoteEndpoint,
        CancellationToken cancellationToken) =>
        socket.ReceiveMessageFromAsync(
            buffer,
            SocketFlags.None,
            remoteEndpoint,
            cancellationToken);

    private static IPEndPoint? CopyEndpoint(IPEndPoint? endpoint) =>
        endpoint is null
            ? null
            : new IPEndPoint(
                new IPAddress(endpoint.Address.GetAddressBytes()),
                endpoint.Port);

    private enum SourceState
    {
        Created,
        Started,
        Stopping,
        Stopped,
        Faulted,
        Disposed,
    }
}
