using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pico4SAFTExtTrackingModule.Diagnostics;

namespace Pico4SAFTExtTrackingModule.PacketLogger;

public sealed partial class PacketLogger<T> : IDisposable where T : struct
{
    private const char CSV_DELIMITER = ';';
    private readonly Thread _thread;
    private readonly string _filePath;
    private readonly IDataExtractor<T> _dataExtractor;
    private readonly ILogger _logger;
    private int _disposed;
    // At most one pending sample, in addition to the sample currently being written.
    // Continuations never run on the Update producer's thread.
    private readonly Channel<T> _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
        AllowSynchronousContinuations = false,
    });

    public PacketLogger(string filePath, IDataExtractor<T> dataExtractor)
        : this(filePath, dataExtractor, NullLogger.Instance) { }

    public PacketLogger(string filePath, IDataExtractor<T> dataExtractor, ILogger logger)
    {
        _dataExtractor = dataExtractor;
        _filePath = filePath;
        _logger = PicoDiagnostics.ForLogger(logger).Logger;
        _thread = new(ThreadMethod) { IsBackground = true, Name = "PICO CSV writer" };
        _thread.Start();
    }

    public void UpdateValue(in T obj) => _channel.Writer.TryWrite(obj);

    private void ThreadMethod()
    {
        try
        {
            using var writer = File.CreateText(_filePath);
            writer.WriteLine(_dataExtractor.GetCSVHeader(CSV_DELIMITER));
            while (_channel.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (_channel.Reader.TryRead(out var value))
                    writer.WriteLine(_dataExtractor.ToCSV(value, CSV_DELIMITER));
            }
            // Disposing the writer flushes the last pending sample on normal shutdown.
        }
        catch (Exception exception)
        {
            // Stop accepting samples. CSV failure must never stop face tracking.
            _channel.Writer.TryComplete();
            LogWriteFailed(_logger, exception, _filePath);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel.Writer.TryComplete();
        _thread.Join();
    }

    [LoggerMessage(LogLevel.Error, "PICO CSV logging stopped after a write failure at {path}; tracking remains active.")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception, string path);
}
