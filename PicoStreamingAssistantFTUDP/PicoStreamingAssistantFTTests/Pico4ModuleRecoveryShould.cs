using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Pico4SAFTExtTrackingModule.BlendshapeScaler;
using Pico4SAFTExtTrackingModule.PicoConnectors;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using VRCFaceTracking;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.Params.Expressions;

namespace Pico4SAFTExtTrackingModule;

[TestClass]
public class Pico4ModuleRecoveryShould
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReconnectAfterTransportFailuresWithoutWaitingFiveSeconds(bool disposed)
    {
        var clock = new RetryClock();
        using var first = new RetryConnector { ReadError = TransportError(disposed) };
        using var second = new RetryConnector { ReadError = TransportError(disposed) };
        using var third = new RetryConnector();
        var connectors = new Queue<IPicoConnector>(new[] { first, second, third });
        int attempts = 0;
        var module = CreateModule(clock, () => { attempts++; return connectors.Dequeue(); });

        module.Update();
        Assert.AreEqual(1, first.Teardowns);

        clock.Advance(0.999);
        var wait = Stopwatch.StartNew();
        module.Update();
        Assert.IsTrue(wait.ElapsedMilliseconds >= 80, "Recovery must still let the host thread sleep.");
        Assert.AreEqual(1, attempts);

        clock.Advance(0.001);
        module.Update();
        Assert.AreEqual(2, attempts);
        Assert.AreEqual(1, second.Teardowns);

        clock.Advance(1);
        module.Update();
        Assert.AreEqual(3, attempts);
        Assert.AreEqual(1, third.Reads);
        Assert.AreEqual(0, third.Teardowns);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void KeepTheListenerWhenMappingFailsEvenForTransportExceptionTypes(int errorKind)
    {
        var clock = new RetryClock();
        using var connector = new RetryConnector();
        Exception error = errorKind switch
        {
            0 => new InvalidOperationException("Mapping failed."),
            1 => new SocketException(10054),
            _ => new ObjectDisposedException("scaler"),
        };
        bool fail = true;
        var scaler = PassthroughScaler();
        scaler.Setup(s => s.EyeExpressionShapeScale(It.IsAny<float>(), It.IsAny<EyeExpressions>()))
            .Returns((float value, EyeExpressions _) => fail ? throw error : value);
        int attempts = 0;
        var module = CreateModule(clock, () => { attempts++; return connector; }, scaler.Object);

        module.Update();
        Assert.AreEqual(0, connector.Teardowns);

        clock.Advance(0.999);
        module.Update();
        Assert.AreEqual(1, connector.Reads);

        fail = false;
        clock.Advance(0.001);
        module.Update();
        Assert.AreEqual(2, connector.Reads);
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(1, connector.Connections);
        Assert.AreEqual(0, connector.Teardowns);
        Assert.AreEqual(0.4f, UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.JawOpen].Weight);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WaitFiveSecondsFromTheEndOfAFailedConnectionAttempt(bool throws)
    {
        var clock = new RetryClock();
        using var connector = new RetryConnector
        {
            ConnectAction = () =>
            {
                clock.Advance(2);
                if (throws) throw new InvalidOperationException("Connection failed.");
                return false;
            },
        };
        int attempts = 0;
        var module = CreateModule(clock, () => { attempts++; return connector; });

        module.Update();
        Assert.AreEqual(1, connector.Teardowns);
        Assert.AreEqual(0, connector.Reads);

        clock.Advance(4.999);
        var wait = Stopwatch.StartNew();
        module.Update();
        Assert.IsTrue(wait.ElapsedMilliseconds >= 80, "Connection retry waits must not spin.");
        Assert.AreEqual(1, attempts);

        connector.ConnectAction = () => true;
        clock.Advance(0.001);
        module.Update();
        Assert.AreEqual(2, attempts);
        Assert.AreEqual(1, connector.Reads);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WaitFiveSecondsWhenServiceDiscoveryFails(bool throws)
    {
        var clock = new RetryClock();
        using var connector = new RetryConnector();
        int attempts = 0;
        var module = CreateModule(clock, () =>
        {
            attempts++;
            if (attempts > 1) return connector;
            if (throws) throw new InvalidOperationException("Discovery failed.");
            return null;
        });

        module.Update();
        clock.Advance(4.999);
        module.Update();
        Assert.AreEqual(1, attempts);

        clock.Advance(0.001);
        module.Update();
        Assert.AreEqual(2, attempts);
        Assert.AreEqual(1, connector.Reads);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ContinueRetryingEvenWhenTeardownThrows(bool receiveFailure)
    {
        var clock = new RetryClock();
        using var first = new RetryConnector
        {
            ConnectAction = () => receiveFailure,
            ReadError = TransportError(false),
            TeardownError = new InvalidOperationException("Teardown failed."),
        };
        using var second = new RetryConnector();
        int attempts = 0;
        var module = CreateModule(clock, () => ++attempts == 1 ? first : second);

        module.Update();
        Assert.AreEqual(1, first.Teardowns);
        clock.Advance(receiveFailure ? 1 : 5);
        module.Update();

        Assert.AreEqual(2, attempts);
        Assert.AreEqual(1, second.Reads);
        Assert.AreEqual(1, first.Teardowns);
    }

    [TestMethod]
    public void BackOffWithoutRebuildingForAnUnknownReceiveError()
    {
        var clock = new RetryClock();
        using var connector = new RetryConnector { ReadError = new InvalidOperationException("Receive failed.") };
        int attempts = 0;
        var module = CreateModule(clock, () => { attempts++; return connector; });

        module.Update();
        Assert.AreEqual(0, connector.Teardowns);
        clock.Advance(0.999);
        module.Update();
        Assert.AreEqual(1, connector.Reads);

        connector.ReadError = null;
        clock.Advance(0.001);
        module.Update();
        Assert.AreEqual(2, connector.Reads);
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(0, connector.Teardowns);
    }

    [TestMethod]
    public void KeepTheListenerAndContinueReceivingWhenThereIsNoData()
    {
        var clock = new RetryClock();
        using var connector = new RetryConnector { NoData = true };
        int attempts = 0;
        var module = CreateModule(clock, () => { attempts++; return connector; });

        module.Update();
        module.Update();

        Assert.AreEqual(2, connector.Reads);
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(0, connector.Teardowns);
    }



    [TestMethod]
    public void RebuildWhenABackgroundLoopEndsWithANonSocketException()
    {
        var clock = new RetryClock();
        using var first = new RetryConnector { ReadError = new ReceiveLoopException(new InvalidOperationException("background failed")) };
        using var second = new RetryConnector();
        int attempts = 0;
        using var module = CreateModule(clock, () => ++attempts == 1 ? first : second);
        module.Update();
        Assert.AreEqual(1, first.Teardowns);
        clock.Advance(1);
        module.Update();
        Assert.AreEqual(1, second.Reads);
    }

    private static Exception TransportError(bool disposed) => disposed
        ? new ObjectDisposedException("socket")
        : new SocketException(10054);



    private static Mock<IBlendshapeScaler> PassthroughScaler()
    {
        var scaler = new Mock<IBlendshapeScaler>();
        scaler.Setup(s => s.EyeExpressionShapeScale(It.IsAny<float>(), It.IsAny<EyeExpressions>()))
            .Returns((float value, EyeExpressions _) => value);
        scaler.Setup(s => s.UnifiedExpressionShapeScale(It.IsAny<float>(), It.IsAny<UnifiedExpressions>()))
            .Returns((float value, UnifiedExpressions _) => value);
        return scaler;
    }

    private static Pico4SAFTExtTrackingModule CreateModule(RetryClock clock,
        Func<IPicoConnector?> factory, IBlendshapeScaler? scaler = null) =>
        new(null, scaler ?? PassthroughScaler().Object, factory, () => clock.UtcNow)
        {
            Status = ModuleState.Active,
            Logger = NullLogger.Instance,
            TrackingState = (true, true),
        };

    private sealed class RetryClock
    {
        public DateTime UtcNow { get; private set; } = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

        public void Advance(double seconds) => UtcNow = UtcNow.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
    }

    private sealed class RetryConnector : IPicoConnector, IDisposable
    {
        private readonly float[] shapes = new float[Pxr.BLEND_SHAPE_NUMS];

        public RetryConnector() => shapes[(int)BlendShapeIndex.JawOpen] = 0.4f;

        public Func<bool> ConnectAction { get; set; } = () => true;
        public Exception? ReadError { get; set; }
        public Exception? TeardownError { get; set; }
        public bool NoData { get; set; }
        public int Connections { get; private set; }
        public int Reads { get; private set; }
        public int Teardowns { get; private set; }

        public string GetProcessName() => "Test PICO service";

        public bool Connect()
        {
            Connections++;
            return ConnectAction();
        }

        public ReadOnlySpan<float> GetBlendShapes()
        {
            Reads++;
            if (ReadError != null) throw ReadError;
            return NoData ? [] : shapes;
        }

        public void Teardown()
        {
            Teardowns++;
            if (TeardownError != null) throw TeardownError;
        }

        public void Dispose() { }
    }
}
