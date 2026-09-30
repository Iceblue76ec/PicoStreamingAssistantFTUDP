using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Pico4SAFTExtTrackingModule.PicoConnectors;

[TestClass]
public class ItLegacyConnectorShould
{
    private static LegacyConnector Connector() => new(NullLogger.Instance, PicoPrograms.PicoConnect, 0);
    private static byte[] Packet(float value, int size = 312, byte type = 2)
    {
        byte[] bytes = new byte[size];
        if (size >= 3) bytes[2] = type;
        if (size >= 312)
            for (int i = 0; i < 72; i++) BitConverter.GetBytes(value).CopyTo(bytes, 24 + i * 4);
        return bytes;
    }
    private static void Send(UdpClient sender, LegacyConnector connector, byte[] bytes) =>
        sender.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, connector.LocalEndPoint!.Port));

    [TestMethod, Timeout(5000)]
    public void BindWithoutFirstPacketAndCloseWithoutCancellationError()
    {
        var connector = Connector();
        var clock = Stopwatch.StartNew();
        Assert.IsTrue(connector.Connect());
        Assert.IsTrue(clock.ElapsedMilliseconds < 500);
        int port = connector.LocalEndPoint!.Port;
        clock.Restart();
        connector.Teardown();
        connector.Teardown();
        Assert.IsTrue(clock.ElapsedMilliseconds < 1000);
        using var socket = new UdpClient(port);
    }

    [TestMethod, Timeout(5000)]
    public void FailPromptlyWhenPortIsOccupied()
    {
        using var occupied = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        occupied.ExclusiveAddressUse = true;
        occupied.Bind(new IPEndPoint(IPAddress.Any, 0));
        var connector = new LegacyConnector(NullLogger.Instance, PicoPrograms.PicoConnect,
            ((IPEndPoint)occupied.LocalEndPoint!).Port);
        var clock = Stopwatch.StartNew();
        Assert.IsFalse(connector.Connect());
        Assert.IsTrue(clock.ElapsedMilliseconds < 500);
        connector.Teardown();
    }

    [TestMethod, Timeout(5000)]
    public void SkipInvalidPacketsAndRetainTheNewestValidSample()
    {
        var connector = Connector();
        using var sender = new UdpClient();
        try
        {
            Assert.IsTrue(connector.Connect());
            foreach (int size in new[] { 0, 3, 311 }) Send(sender, connector, Packet(0, size));
            Send(sender, connector, Packet(0, 312, 1));
            Send(sender, connector, Packet(1));
            Send(sender, connector, Packet(2, 908));
            Send(sender, connector, Packet(3, 8000));
            Send(sender, connector, Packet(0, 3));
            Assert.IsTrue(SpinWait.SpinUntil(() =>
            {
                var sample = connector.GetBlendShapes();
                return !sample.IsEmpty && sample[0] == 3;
            }, 2000));
        }
        finally { connector.Teardown(); }
    }

    [TestMethod, Timeout(5000)]
    public void WaitWhenIdleAndWakeForPacketOrShutdown()
    {
        var connector = Connector();
        using var sender = new UdpClient();
        try
        {
            Assert.IsTrue(connector.Connect());
            var clock = Stopwatch.StartNew();
            Assert.IsTrue(connector.GetBlendShapes().IsEmpty);
            Assert.IsTrue(clock.ElapsedMilliseconds >= 70);
            var waiting = Task.Run(() => connector.GetBlendShapes().ToArray());
            Send(sender, connector, Packet(4));
            Assert.IsTrue(waiting.Wait(1000));
            Assert.AreEqual(4f, waiting.Result[0]);
            var stopping = Task.Run(() => connector.GetBlendShapes().ToArray());
            connector.Teardown();
            Assert.IsTrue(stopping.Wait(1000));
        }
        finally { connector.Teardown(); }
    }

    [TestMethod, Timeout(5000)]
    public void SurfaceBackgroundFailureAndCloseWithoutThrowing()
    {
        var original = new InvalidOperationException("receiver failed");
        var connector = new LegacyConnector(NullLogger.Instance, PicoPrograms.PicoConnect, 0,
            (_, _, _, _) => ValueTask.FromException<SocketReceiveFromResult>(original));
        try
        {
            Assert.IsTrue(connector.Connect());
            var error = Assert.ThrowsException<ReceiveLoopException>(() => connector.GetBlendShapes());
            Assert.AreSame(original, error.InnerException);
        }
        finally { connector.Teardown(); }
    }

    [TestMethod, Timeout(5000)]
    public void WriteTheOriginalReceiverDetailBeforePublishingTheRecoveryFault()
    {
        var trigger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var detailEntered = new ManualResetEventSlim();
        using var releaseDetail = new ManualResetEventSlim();
        int written = 0;
        var output = new Mock<ILogger>();
        output.Setup(logger => logger.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        output.Setup(logger => logger.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(call =>
            {
                if (((EventId)call.Arguments[1]).Name != "LogReceiveFailed") return;
                detailEntered.Set();
                Assert.IsTrue(releaseDetail.Wait(TimeSpan.FromSeconds(2)));
                Interlocked.Exchange(ref written, 1);
            }));
        async ValueTask<SocketReceiveFromResult> Receive(Socket socket, Memory<byte> buffer, EndPoint endpoint, CancellationToken token)
        {
            await trigger.Task.WaitAsync(token);
            throw new InvalidOperationException("Original receiver failure");
        }
        var connector = new LegacyConnector(output.Object, PicoPrograms.PicoConnect, 0, Receive);
        try
        {
            Assert.IsTrue(connector.Connect());
            trigger.SetResult();
            Assert.IsTrue(detailEntered.Wait(TimeSpan.FromSeconds(2)));
            var reader = Task.Run(() =>
            {
                bool premature = false;
                Assert.IsTrue(SpinWait.SpinUntil(() =>
                {
                    try { connector.GetBlendShapes(); return false; }
                    catch (ReceiveLoopException) { premature = Volatile.Read(ref written) == 0; return true; }
                }, 2000));
                return premature;
            });
            Assert.IsFalse(reader.Wait(150));
            releaseDetail.Set();
            Assert.IsTrue(reader.Wait(2000));
            Assert.IsFalse(reader.Result);
        }
        finally { releaseDetail.Set(); connector.Teardown(); }
    }

    [TestMethod, Timeout(5000)]
    public void PublishAfterTheBatchLimitAndCheckCancellationBeforeDrainingAgain()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var nextBatch = new ManualResetEventSlim();
        int received = 0;
        async ValueTask<SocketReceiveFromResult> Receive(Socket socket, Memory<byte> buffer, EndPoint endpoint, CancellationToken token)
        {
            await start.Task.WaitAsync(token);
            int frame = Interlocked.Increment(ref received);
            if (frame > 1024)
            {
                nextBatch.Set();
                await stop.Task.WaitAsync(token);
            }
            Packet(frame).CopyTo(buffer);
            return new SocketReceiveFromResult { ReceivedBytes = 312, RemoteEndPoint = endpoint };
        }
        var connector = new LegacyConnector(NullLogger.Instance, PicoPrograms.PicoConnect, 0, Receive);
        using var sender = new UdpClient();
        try
        {
            Assert.IsTrue(connector.Connect());
            // Keep one datagram queued so Available stays positive during the injected burst.
            Send(sender, connector, [1]);
            start.SetResult();
            Assert.IsTrue(nextBatch.Wait(TimeSpan.FromSeconds(2)));
            var sample = connector.GetBlendShapes();
            Assert.AreEqual(72, sample.Length);
            foreach (float weight in sample) Assert.AreEqual(1024f, weight);
            Assert.AreEqual(1025, Volatile.Read(ref received));
            var timer = Stopwatch.StartNew();
            connector.Teardown();
            Assert.IsTrue(timer.ElapsedMilliseconds < 1000);
        }
        finally { connector.Teardown(); }
    }

    [TestMethod, Timeout(5000)]
    public void KeepReceivingAfterAHandledSocketTimeout()
    {
        int calls = 0;
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<SocketReceiveFromResult> Receive(Socket socket, Memory<byte> buffer, EndPoint endpoint, CancellationToken token)
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 1) throw new SocketException((int)SocketError.TimedOut);
            if (call > 2) await idle.Task.WaitAsync(token);
            Packet(42).CopyTo(buffer);
            return new SocketReceiveFromResult { ReceivedBytes = 312, RemoteEndPoint = endpoint };
        }
        var connector = new LegacyConnector(NullLogger.Instance, PicoPrograms.PicoConnect, 0, Receive);
        try
        {
            Assert.IsTrue(connector.Connect());
            Assert.IsTrue(SpinWait.SpinUntil(() =>
            {
                var sample = connector.GetBlendShapes();
                return !sample.IsEmpty && sample[0] == 42f;
            }, 2000));
        }
        finally { connector.Teardown(); }
    }

    [TestMethod, Timeout(10000)]
    public void KeepConsumerSnapshotStableWhileReceivingOtherFrames()
    {
        var connector = Connector();
        using var sender = new UdpClient();
        try
        {
            Assert.IsTrue(connector.Connect());
            Send(sender, connector, Packet(1));
            var first = connector.GetBlendShapes();
            Assert.AreEqual(72, first.Length);
            Send(sender, connector, Packet(2));
            Thread.Sleep(50);
            foreach (float value in first) Assert.AreEqual(1f, value);
            var sample = connector.GetBlendShapes();
            foreach (float value in sample) Assert.AreEqual(2f, value);
            for (int frame = 3; frame <= 100; frame++)
            {
                Send(sender, connector, Packet(frame));
                var current = connector.GetBlendShapes();
                if (!current.IsEmpty)
                    foreach (float value in current) Assert.AreEqual(current[0], value);
            }
        }
        finally { connector.Teardown(); }
    }
}
