using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Pico4SAFTExtTrackingModule.Diagnostics;

namespace Pico4SAFTExtTrackingModule.PicoConnectors;

public sealed partial class LegacyConnector : IPicoConnector
{
    // 16-byte header + skipped 8-byte payload timestamp + 72 float weights = 312.
    // Parse only this prefix; the complete header + PxrFTInfo layout is 908 bytes.
    // See Pxr.cs and docs/connector.md. Fragment assembly is not implemented.
    private const int HeaderSize = 16;
    private const int WeightOffset = HeaderSize + sizeof(long);
    private const int WeightBytes = Pxr.BLEND_SHAPE_NUMS * sizeof(float);
    internal const int MinimumPacketSize = WeightOffset + WeightBytes;
    private const int MaxPacketsPerBatch = 1024;
    private readonly object _lifecycleGate = new();
    private readonly ILogger _logger;
    private readonly PicoPrograms _program;
    private readonly int _port;
    private readonly Func<Socket, Memory<byte>, EndPoint, CancellationToken, ValueTask<SocketReceiveFromResult>> _receive;
    private readonly float[] _consumerWeights = new float[Pxr.BLEND_SHAPE_NUMS];
    private Session? _session;

    public LegacyConnector(ILogger logger, PicoPrograms program)
        : this(logger, program, 29765) { }

    internal LegacyConnector(ILogger logger, PicoPrograms program, int port,
        Func<Socket, Memory<byte>, EndPoint, CancellationToken, ValueTask<SocketReceiveFromResult>>? receive = null)
    {
        _logger = PicoDiagnostics.ForLogger(logger).Logger;
        _program = program;
        _port = port;
        _receive = receive ?? ((socket, buffer, endpoint, token) => socket.ReceiveFromAsync(buffer, endpoint, token));
    }

    internal IPEndPoint? LocalEndPoint => Volatile.Read(ref _session)?.Socket.LocalEndPoint as IPEndPoint;

    public string GetProcessName() => _program switch
    {
        PicoPrograms.StreamingAssistant => "Streaming Assistant",
        PicoPrograms.BusinessStreamingV1 or PicoPrograms.BusinessStreaming => "Business Streaming",
        PicoPrograms.PicoConnect => "PICO Connect",
        _ => "[?]",
    };

    public bool Connect()
    {
        lock (_lifecycleGate)
        {
            if (_session != null) return true;
            Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            Session? session = null;
            try
            {
                // 0.0.0.0:29765 also accepts datagrams from unrelated senders.
                // Length/type checks do not authenticate the sender.
                socket.Bind(new IPEndPoint(IPAddress.Any, _port));
                session = new Session(socket);
                InitializeDiagnostics(session);
                Volatile.Write(ref _session, session);
                LogBound(socket.LocalEndPoint!, GetProcessName());
                session.Task = Task.Run(() => ReceiveLoopAsync(session));
                return true;
            }
            catch (Exception exception)
            {
                socket.Dispose();
                session?.Cancellation.Dispose();
                Volatile.Write(ref _session, null);
                LogBindFailed(exception, _port);
                return false;
            }
        }
    }

    // One Update consumer owns this snapshot until its next call. The receiver only
    // writes Session.Latest, so mapping cannot observe a partially overwritten frame.
    // Mapping, diagnostics and process probes run outside Session.Gate. Pending
    // samples may be replaced during module pause/backoff; no history is queued.
    public ReadOnlySpan<float> GetBlendShapes()
    {
        Session? session = Volatile.Read(ref _session);
        if (session == null) return [];
        bool received = false;
        lock (session.Gate)
        {
            long waitStarted = Stopwatch.GetTimestamp();
            while (!session.HasSample && !session.Closed && session.Failure == null)
            {
                int remaining = 100 - (int)Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds;
                if (remaining <= 0) break;
                Monitor.Wait(session.Gate, remaining);
            }
            if (!session.Closed && session.Failure != null) throw new ReceiveLoopException(session.Failure);
            if (!session.Closed && session.HasSample)
            {
                session.Latest.CopyTo(_consumerWeights, 0);
                session.HasSample = false;
                received = true;
            }
        }
        ObserveIdle(session);
        return received ? _consumerWeights : [];
    }

    private async Task ReceiveLoopAsync(Session session)
    {
        CancellationToken token = session.Cancellation.Token;
        // Large datagrams are harmless suffixes; never allocate an array per packet.
        byte[] buffer = new byte[ushort.MaxValue];
        float[] batchWeights = new float[Pxr.BLEND_SHAPE_NUMS];
        EndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (true)
            {
                int valid = 0;
                int drained = 0;
                for (; drained < MaxPacketsPerBatch; drained++)
                {
                    token.ThrowIfCancellationRequested();
                    if (drained > 0 && session.Socket.Available == 0) break;
                    SocketReceiveFromResult result;
                    try { result = await _receive(session.Socket, buffer, endpoint, token).ConfigureAwait(false); }
                    catch (SocketException exception) when (exception.SocketErrorCode == SocketError.TimedOut)
                    {
                        ObserveTimeout(session);
                        await Task.Delay(100, token).ConfigureAwait(false);
                        break;
                    }
                    int length = result.ReceivedBytes;
                    bool accepted = length >= MinimumPacketSize && buffer[2] == 2;
                    ObservePacket(session, length, length >= 3 ? buffer[2] : (byte)0,
                        accepted, result.RemoteEndPoint as IPEndPoint);
                    if (!accepted) continue;
                    MemoryMarshal.Cast<byte, float>(buffer.AsSpan(WeightOffset, WeightBytes)).CopyTo(batchWeights);
                    valid++;
                }
                bool superseded = false;
                if (valid > 0)
                {
                    lock (session.Gate)
                    {
                        if (session.Closed) return;
                        superseded = session.HasSample;
                        batchWeights.CopyTo(session.Latest, 0);
                        session.HasSample = true;
                        Monitor.PulseAll(session.Gate);
                    }
                }
                ObserveBatch(session, valid, drained == MaxPacketsPerBatch, superseded);
                if (drained == MaxPacketsPerBatch) await Task.Yield();
            }
        }
        catch (Exception exception) when (token.IsCancellationRequested)
        {
            // Closing a socket can surface cancellation, disposal or a socket error.
            _ = exception;
        }
        catch (Exception exception)
        {
            // Write the original detail before exposing the fault to Update, whose
            // recovery message references this entry. No sample lock spans logging.
            try { LogReceiveFailed(exception); } catch { /* Logging must not fault cleanup. */ }
            lock (session.Gate)
            {
                session.Failure = exception;
                Monitor.PulseAll(session.Gate);
            }
        }
        finally
        {
            session.Socket.Dispose();
            session.Cancellation.Dispose();
            FinishDiagnostics(session);
        }
    }

    public void Teardown()
    {
        Session? session;
        lock (_lifecycleGate)
        {
            session = _session;
            if (session == null) return;
            Volatile.Write(ref _session, null);
            lock (session.Gate)
            {
                session.Closed = true;
                Monitor.PulseAll(session.Gate);
            }
            try { session.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
            session.Socket.Dispose();
        }
        if (!session.Task.Wait(TimeSpan.FromSeconds(1))) LogShutdownTimeout();
    }

    private sealed partial class Session(Socket socket)
    {
        internal readonly object Gate = new();
        internal readonly Socket Socket = socket;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly float[] Latest = new float[Pxr.BLEND_SHAPE_NUMS];
        internal Task Task = Task.CompletedTask;
        internal bool HasSample;
        internal bool Closed;
        internal Exception? Failure;
    }

    partial void InitializeDiagnostics(Session session);
    partial void ObservePacket(Session session, int length, byte type, bool accepted, IPEndPoint? sender);
    partial void ObserveBatch(Session session, int valid, bool limited, bool superseded);
    partial void ObserveIdle(Session session);
    partial void ObserveTimeout(Session session);
    partial void FinishDiagnostics(Session session);

    [LoggerMessage(LogLevel.Information, "UDP listener bound to {endpoint} for {program}; waiting for face data.")]
    private partial void LogBound(EndPoint endpoint, string program);
    [LoggerMessage(LogLevel.Warning, "Could not bind PICO UDP port {port}; connection will be retried.")]
    private partial void LogBindFailed(Exception exception, int port);
    [LoggerMessage(LogLevel.Warning, "PICO UDP receiver stopped unexpectedly.")]
    private partial void LogReceiveFailed(Exception exception);
    [LoggerMessage(LogLevel.Warning, "PICO UDP receiver did not exit within 1000ms; its socket is closed.")]
    private partial void LogShutdownTimeout();
}
