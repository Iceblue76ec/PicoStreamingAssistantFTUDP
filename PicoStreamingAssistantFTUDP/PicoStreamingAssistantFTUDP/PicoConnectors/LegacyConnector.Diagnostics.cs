using System.Net;
using Pico4SAFTExtTrackingModule.Diagnostics;
using Pico4SAFTExtTrackingModule.PicoConnectors.ProgramChecker;

namespace Pico4SAFTExtTrackingModule.PicoConnectors;

public sealed partial class LegacyConnector
{
    private sealed partial class Session
    {
        internal readonly object DiagnosticsGate = new();
        internal ReceiveDiagnostics? Diagnostics;
    }

    partial void InitializeDiagnostics(Session session)
    {
        session.Diagnostics = new ReceiveDiagnostics(PicoDiagnostics.ForLogger(_logger),
            () => new ProcessRunningProgramChecker().Check(_program));
    }
    partial void ObservePacket(Session session, int length, byte type, bool accepted, IPEndPoint? sender)
    {
        lock (session.DiagnosticsGate)
        {
            var diagnostics = session.Diagnostics!;
            diagnostics.Packet(length);
            if (length < MinimumPacketSize) diagnostics.ShortPacket(length, sender);
            else if (!accepted) diagnostics.OtherType(type, length, sender);
            else diagnostics.ValidPacket(length, sender);
        }
    }
    partial void ObserveBatch(Session session, int valid, bool limited, bool superseded)
    {
        lock (session.DiagnosticsGate)
        {
            if (superseded) session.Diagnostics!.SupersededSample();
            session.Diagnostics!.EndBatch(valid, limited);
        }
    }
    partial void ObserveIdle(Session session)
    {
        lock (session.DiagnosticsGate) session.Diagnostics!.EndBatch(0, false);
    }
    partial void ObserveTimeout(Session session)
    {
        lock (session.DiagnosticsGate) session.Diagnostics!.Timeout();
    }
    partial void FinishDiagnostics(Session session)
    {
        lock (session.DiagnosticsGate) session.Diagnostics!.Statistics(final: true);
    }
}
