using Microsoft.Extensions.Logging;
using Pico4SAFTExtTrackingModule.Diagnostics;

namespace Pico4SAFTExtTrackingModule.PicoConnectors;

public sealed class PicoConnectConnector : IPicoConnector
{
    private readonly PicoDiagnostics _diagnostics;
    private readonly PicoPrograms _program;
    public PicoConnectConnector(ILogger logger) : this(logger, PicoPrograms.PicoConnect) { }
    internal PicoConnectConnector(ILogger logger, PicoPrograms program)
    {
        _diagnostics = PicoDiagnostics.ForLogger(logger);
        _program = program;
    }
    public bool Connect()
    {
        _diagnostics.Report(LogLevel.Warning, "UnsupportedProtocol", _program.ToString(), () =>
            $"{GetProcessName()} transfer protocol is unsupported or unreadable. Set lab.faceTrackingTransferProtocol=2 " +
            $"in %APPDATA%/{GetProcessName()}/settings.json and restart the streaming program; connection retry in 5000ms. " +
            "https://docs.vrcft.io/docs/hardware/vr/pico/pico4pe");
        return false;
    }
    public ReadOnlySpan<float> GetBlendShapes() => [];
    public string GetProcessName() => _program == PicoPrograms.BusinessStreaming ? "Business Streaming" : "PICO Connect";
    void IPicoConnector.Teardown() { }
}
