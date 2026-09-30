using Microsoft.Extensions.Logging;
using Pico4SAFTExtTrackingModule.Diagnostics;

using Pico4SAFTExtTrackingModule.PicoConnectors.ConfigChecker;
using Pico4SAFTExtTrackingModule.PicoConnectors.ProgramChecker;

namespace Pico4SAFTExtTrackingModule.PicoConnectors;

public static partial class ConnectorFactory
{
    public static IPicoConnector? Build(ILogger logger, IProgramChecker programChecker, IConfigChecker configChecker)
    {
        var diagnostics = PicoDiagnostics.ForLogger(logger);
        logger = diagnostics.Logger;
        bool pc = programChecker.Check(PicoPrograms.PicoConnect);
        bool bs = programChecker.Check(PicoPrograms.BusinessStreaming);
        bool bs1 = programChecker.Check(PicoPrograms.BusinessStreamingV1);
        bool sa = programChecker.Check(PicoPrograms.StreamingAssistant);
        diagnostics.Report(LogLevel.Debug, "StreamingProgramProbe", $"{pc}/{bs}/{bs1}/{sa}", () =>
            $"Streaming programs: PicoConnect={pc}; BusinessStreaming={bs}; BusinessStreamingV1={bs1}; StreamingAssistant={sa}.");
        if (pc)
        {
            logger.LogPicoConnect();
            try
            {
                int protocol = configChecker.GetTransferProtocolNumber(PicoPrograms.PicoConnect);
                diagnostics.Report(LogLevel.Information, "PicoConnectProtocol", protocol == 2 ? "legacy" : "unsupported", () =>
                    $"PicoConnect faceTrackingTransferProtocol={protocol}; decoder={(protocol == 2 ? "legacy" : "unsupported")}.");
                return protocol switch
                {
                    2 => new LegacyConnector(logger, PicoPrograms.PicoConnect), // using legacy protocol
                    _ => new PicoConnectConnector(logger), // couldn't get / using latest protocol
                };
            }
            catch (Exception ex)
            {
                logger.LogException(ex);
            }
        }

        if (bs)
        {
            logger.LogBusinessStreaming();
            try
            {
                int protocol = configChecker.GetTransferProtocolNumber(PicoPrograms.BusinessStreaming);
                diagnostics.Report(LogLevel.Information, "BusinessStreamingProtocol", protocol == 2 ? "legacy" : "unsupported", () =>
                    $"BusinessStreaming faceTrackingTransferProtocol={protocol}; decoder={(protocol == 2 ? "legacy" : "unsupported")}.");
                return protocol switch
                {
                    2 => new LegacyConnector(logger, PicoPrograms.BusinessStreaming), // using legacy protocol

                    // TODO is the protocol the same as PicoConnect? can we use the same connector (once it's implemented)?
                    _ => new PicoConnectConnector(logger, PicoPrograms.BusinessStreaming),// couldn't get / using latest protocol
                };
            }
            catch (Exception ex)
            {
                logger.LogException(ex);
            }
        }

        if (bs1)
            return new LegacyConnector(logger, PicoPrograms.BusinessStreamingV1);

        if (sa)
            return new LegacyConnector(logger, PicoPrograms.StreamingAssistant);

        return null; // none found
    }

    [LoggerMessage(LogLevel.Information, "Got new Business Streaming; checking settings.json to choose what protocol to use...")]
    private static partial void LogBusinessStreaming(this ILogger logger);
    [LoggerMessage(LogLevel.Information, "Got PICO Connect; checking settings.json to choose what protocol to use...")]
    private static partial void LogPicoConnect(this ILogger logger);
    [LoggerMessage(LogLevel.Warning, "Exception while trying to get the config protocol number.")]
    private static partial void LogException(this ILogger logger, Exception exception);
}
