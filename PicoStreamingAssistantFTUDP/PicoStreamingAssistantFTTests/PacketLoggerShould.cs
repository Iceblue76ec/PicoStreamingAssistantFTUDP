using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pico4SAFTExtTrackingModule;
using Pico4SAFTExtTrackingModule.PacketLogger;

namespace PicoStreamingAssistantFTTests;

[TestClass]
public class PacketLoggerShould
{
    [TestMethod]
    public void KeepOnlyTheLatestPendingSampleWithoutBlockingUpdateAndDrainOnClose()
    {
        string path = Path.GetTempFileName();
        using var writing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var extractor = new TestExtractor(value =>
        {
            if (value == 1)
            {
                writing.Set();
                Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5)));
            }
            return value.ToString();
        });
        var logger = new PacketLogger<int>(path, extractor);
        try
        {
            logger.UpdateValue(1);
            Assert.IsTrue(writing.Wait(TimeSpan.FromSeconds(2)));
            var timer = Stopwatch.StartNew();
            for (int value = 2; value <= 10_000; value++) logger.UpdateValue(value);
            Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(1), "Update must not wait for the blocked writer.");
            release.Set();
            logger.Dispose();
            CollectionAssert.AreEqual(new[] { "value", "1", "10000" }, File.ReadAllLines(path));
            logger.UpdateValue(10001); // Closed writers silently reject further samples.
            logger.Dispose();
        }
        finally { release.Set(); logger.Dispose(); File.Delete(path); }
    }

    [TestMethod]
    public void WaitForNewDataAndFlushTheLastSampleOnNormalClose()
    {
        string path = Path.GetTempFileName();
        using var header = new ManualResetEventSlim();
        var extractor = new TestExtractor(value => value.ToString(), () => header.Set());
        var logger = new PacketLogger<int>(path, extractor);
        try
        {
            Assert.IsTrue(header.Wait(TimeSpan.FromSeconds(2)));
            logger.UpdateValue(42);
            logger.Dispose();
            CollectionAssert.AreEqual(new[] { "value", "42" }, File.ReadAllLines(path));
        }
        finally { logger.Dispose(); File.Delete(path); }
    }

    [TestMethod]
    public void StopCsvAndReportOneErrorWhenCreatingTheFileFails()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var output = new DiagnosticTestLogger();
        var logger = new PacketLogger<int>(directory, new TestExtractor(value => value.ToString()), output);
        try
        {
            logger.UpdateValue(1);
            logger.Dispose();
            for (int i = 0; i < 1000; i++) logger.UpdateValue(i);
            Assert.AreEqual(1, output.Entries.Count);
            Assert.AreEqual(LogLevel.Error, output.Entries[0].Level);
            StringAssert.Contains(output.Entries[0].Text, "tracking remains active");
        }
        finally { logger.Dispose(); Directory.Delete(directory); }
    }

    [TestMethod]
    public void IsolateFailuresDuringRowWritingAndKeepTheFirstExceptionDetail()
    {
        string path = Path.GetTempFileName();
        var output = new DiagnosticTestLogger();
        var extractor = new TestExtractor(_ => throw new IOException("Injected row write failure"));
        var logger = new PacketLogger<int>(path, extractor, output);
        try
        {
            logger.UpdateValue(42);
            logger.Dispose();
            logger.UpdateValue(43);
            Assert.AreEqual(1, output.Entries.Count);
            StringAssert.Contains(output.Entries[0].Text, "Injected row write failure");
        }
        finally { logger.Dispose(); File.Delete(path); }
    }

    private sealed class TestExtractor(Func<int, string> format, Action? header = null) : IDataExtractor<int>
    {
        public void Clone(in int obj, ref int ret) => ret = obj;
        public string GetCSVHeader(char delimiter) { header?.Invoke(); return "value"; }
        public string ToCSV(in int obj, char delimiter) => format(obj);
    }
}
