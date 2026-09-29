using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Pico4SAFTExtTrackingModule.PicoConnectors.ProgramChecker;
using Pico4SAFTExtTrackingModule.PicoConnectors.ConfigChecker;

namespace Pico4SAFTExtTrackingModule.PicoConnectors;

public sealed class ConnectorFactory
{
    public static IPicoConnector? build(ILogger Logger, IProgramChecker programChecker, IConfigChecker configChecker)
    {
        bool using_sa = programChecker.Check(PicoPrograms.StreamingAssistant);
        bool using_pc = programChecker.Check(PicoPrograms.PicoConnect);
        bool using_bs = programChecker.Check(PicoPrograms.BusinessStreaming);

        // Streaming Assistant and Business Streaming select the legacy decoder directly;
        // their settings and the actual packet format are not validated by this factory.
        if (using_sa) return new LegacyConnector(Logger, PicoPrograms.StreamingAssistant);
        else if (using_bs) return new LegacyConnector(Logger, PicoPrograms.BusinessStreaming);
        else if (using_pc)
        {
            try {
                Logger.LogInformation("Got PICO Connect; checking settings.json to choose what protocol to use...");
                // For PICO Connect, the setup guide requires lab.faceTrackingTransferProtocol=2
                // to select legacy transfer. We then reuse the decoder/layout in LegacyConnector
                // and Pxr.cs; checking this setting does not negotiate or verify a packet version.
                // Other values or unreadable settings select PicoConnectConnector, whose Connect()
                // returns false and logs configuration instructions instead of receiving data.
                // https://docs.vrcft.io/docs/hardware/vr/pico/pico4pe
                if (configChecker.GetTransferProtocolNumber(PicoPrograms.PicoConnect) == 2) return new LegacyConnector(Logger, PicoPrograms.PicoConnect);
            } catch (Exception ex) {
                Logger.LogWarning("Exception while trying to get the config protocol number: " + ex.ToString);
            }
            return new PicoConnectConnector(Logger); // Unsupported or unreadable protocol setting.
        }

        return null; // none found
    }
}
