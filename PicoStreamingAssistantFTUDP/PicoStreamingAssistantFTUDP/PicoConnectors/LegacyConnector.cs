using Microsoft.Extensions.Logging;
using Pico4SAFTExtTrackingModule.PacketLogger;
using System.Net;
using System.Net.Sockets;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking;

namespace Pico4SAFTExtTrackingModule.PicoConnectors;

/**
 * Connector class for Streaming Assitant & Business Streaming.
 * Also used for PICO Connect on `mergetype=2`
 **/
public sealed class LegacyConnector : IPicoConnector
{
    private const string IP_ADDRESS = "127.0.0.1";
    private const int PORT_NUMBER = 29765;

    private static readonly unsafe int pxrHeaderSize = sizeof(TrackingDataHeader);
    private readonly int PacketIndex = pxrHeaderSize;
    // Minimum accepted length for the legacy layout declared in Pxr.cs:
    //   16 (header) + 8 (uninterpreted timestamp) + 72 * 4 (weights) = 312 bytes.
    // Only weights are used. The 596-byte suffix of the full PxrFTInfo is not copied;
    // this decoder accepts an absent, partial or complete suffix. This acceptance rule
    // does not establish that PICO defines those fields as optional in its wire protocol.
    // PICO Connect legacy setup (configuration guidance, not the source of these sizes):
    // https://docs.vrcft.io/docs/hardware/vr/pico/pico4pe
    private const int BlendShapePayloadSize = sizeof(long) + Pxr.BLEND_SHAPE_NUMS * sizeof(float);
    private static readonly int MinimumPacketSize = pxrHeaderSize + BlendShapePayloadSize;
    private const int MaxPacketsPerUpdate = 1024;

    private bool disposedValue, connecting;
    private object socketLock;
    private ILogger Logger;
    private UdpClient? udpClient;
    private IPEndPoint? endPoint;
    private PxrFTInfo data;

    private string processName;

    public LegacyConnector(ILogger Logger, PicoPrograms program_using)
    {
        this.disposedValue = false;
        this.connecting = false;
        this.socketLock = new object();

        this.Logger = Logger;

        switch (program_using)
        {
            case PicoPrograms.StreamingAssistant:
                this.processName = "Streaming Assistant";
                break;

            case PicoPrograms.BusinessStreaming:
                this.processName = "Business Streaming";
                break;

            case PicoPrograms.PicoConnect:
                this.processName = "PICO Connect";
                break;

            default:
                // shouldn't reach this
                Logger.LogWarning("Couldn't find the name for program " + program_using.ToString());
                this.processName = "[?]";
                break;
        }
    }

    public bool Connect()
    {
        lock (this.socketLock)
        {
            this.disposedValue = false;
            this.connecting = true;
        }

        bool result;
        try
        {
            lock (this.socketLock)
            {
                // UdpClient(port) binds all IPv4 interfaces (0.0.0.0:29765).
                udpClient = new UdpClient(PORT_NUMBER);
                // Receive(ref endPoint) replaces this value with the actual sender;
                // initializing it to 127.0.0.1 does not restrict who can send packets.
                endPoint = new IPEndPoint(IPAddress.Parse(IP_ADDRESS), PORT_NUMBER);
                udpClient.Client.ReceiveTimeout = 5000;
            }

            Logger.LogDebug("Host end-point: {endPoint}", endPoint);
            Logger.LogDebug("Initialization Timeout: {timeout}ms", udpClient.Client.ReceiveTimeout);
            Logger.LogDebug("Client established; waiting for PxrFTInfo during updates.");

            // Binding the port is enough to initialize. Face tracking data may
            // arrive later; Update() will receive it when the service is ready.
            result = true;
            Logger.LogInformation("UDP listener ready for {} data.", this.processName);
        }
        catch (SocketException ex) when (ex.ErrorCode is 10048)
        {
            Logger.LogDebug("PICO UDP port {port} is in use; connection will be retried. {message}", PORT_NUMBER, ex.Message);
            result = false;
        }
        catch (Exception e)
        {
            Logger.LogWarning("{exception}", e);
            result = false;
        }

        lock (this.socketLock)
        {
            this.connecting = false;
        }
        return result;
    }

    public unsafe float* GetBlendShapes()
    {
        lock (this.socketLock)
        {
            if (this.connecting) return null;
        }

        fixed (PxrFTInfo* pData = &data)
            if (ReceivePxrData(pData))
            {
                float* pxrShape = pData->blendShapeWeight;
                return pxrShape;
            }

        return null;
    }

    public void Teardown()
    {
        lock (this.socketLock)
        {
            bool needsTeardown = (!this.disposedValue);
            if (!needsTeardown) return;
            this.disposedValue = true;
        }

        Logger.LogInformation("Disposing of PxrFaceTracking UDP Client.");
        lock (this.socketLock)
        {
            if (udpClient is not null)
            {
                udpClient.Client.Close();
            }
            udpClient?.Dispose();
        }

        lock (this.socketLock)
        {
            udpClient = null;
            endPoint = null;
        }
    }

    private unsafe bool ReceivePxrData(PxrFTInfo* pData)
    {
        if (this.IsDisposed()) return false;

        try
        {
            // Update can run slower than the incoming UDP stream. Drain queued
            // packets so tracking reflects the newest valid sample, not old data.
            var received = false;
            for (var i = 0; i < MaxPacketsPerUpdate; i++)
            {
                if (i > 0 && udpClient!.Available == 0) break;
                byte[] packet = udpClient!.Receive(ref endPoint);
                // Sender endpoints are not checked, so any sender that can reach the bound
                // port can supply a datagram. This length check bounds the memory copies;
                // the tracking_type check below does not authenticate the sender or validate
                // the other header fields, fragmentation flags or floating-point values.
                if (packet.Length < MinimumPacketSize) continue;

                fixed (byte* ptr = packet)
                {
                    TrackingDataHeader tdh;
                    Buffer.MemoryCopy(ptr, &tdh, pxrHeaderSize, pxrHeaderSize);
                    if (tdh.tracking_type != 2) continue;

                    Buffer.MemoryCopy(ptr + PacketIndex, pData, sizeof(PxrFTInfo), BlendShapePayloadSize);
                    received = true;
                }
            }
            return received;
        }
        catch (SocketException ex) when (ex.ErrorCode is 10060)
        {
            // No face data yet. Keep the UDP socket bound so later packets can
            // be consumed without restarting the connector.
            Logger.LogDebug("Data was not sent within the timeout. {msg}", ex.Message);
            return false; // got byte failed
        }
        catch (SocketException ex) when (ex.ErrorCode is 10004)
        {
            // `Teardown()` called
            Logger.LogInformation("Socket closed");
            return false; // got byte failed
        }
    }

    public bool IsDisposed()
    {
        lock (this.socketLock)
        {
            return this.disposedValue;
        }
    }

    public string GetProcessName()
    {
        return this.processName;
    }
}
