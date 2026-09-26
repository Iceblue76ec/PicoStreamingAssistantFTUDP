using Microsoft.Extensions.Logging;

namespace Pico4SAFTExtTrackingModule.PicoConnectors;

/**
 * Connector class for PICO Connect
 **/
public sealed class PicoConnectConnector : IPicoConnector
{
    private static long lastProtocolNoticeTicks;
    private static readonly long ProtocolNoticeIntervalTicks = TimeSpan.FromMinutes(1).Ticks;
    private ILogger Logger;

    public PicoConnectConnector(ILogger Logger)
    {
        this.Logger = Logger;
    }

    public bool Connect()
    {
        long now = DateTime.UtcNow.Ticks;
        long last = Interlocked.Read(ref lastProtocolNoticeTicks);
        if (now - last >= ProtocolNoticeIntervalTicks &&
            Interlocked.CompareExchange(ref lastProtocolNoticeTicks, now, last) == last)
        {
            Logger.LogWarning("PICO Connect face tracking cannot start with the current protocol or unreadable settings. Open %APPDATA%\\PICO Connect\\settings.json, set lab.faceTrackingTransferProtocol to 2, save the file, then restart PICO Connect and VRCFaceTracking. See https://docs.vrcft.io/docs/hardware/vr/pico/pico4pe");
        }
        return false;
    }

    public unsafe float* GetBlendShapes()
    {
        return null;
    }

    public string GetProcessName()
    {
        return "PICO Connect";
    }

    void IPicoConnector.Teardown()
    {
        
    }
}
