using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DAMeter.Mac.Platform;
using Xunit;

namespace DAMeter.Mac.Tests;

public sealed class WindowInteractionLogTests
{
    [AvaloniaFact]
    public void RequestsAndNativeGeometryCanBeComparedWithoutWritingDuringTheDrag()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dameter-interaction-{Guid.NewGuid():N}");
        var window = new Window { Width = 400, Height = 200 };
        try
        {
            window.Show();
            var log = new WindowInteractionLog(window, "0.1.7-test", directory);
            log.Begin("resize", "HorizontalPeak", true);
            log.Record("requested", 100, new Point(510, 310), new Size(450, 225), true, true, true);
            window.Width = 460; // Deliberately different from the requested width.
            for (int i = 0; i < 200; i++) log.Record("geometry", (ulong)i);
            Assert.False(File.Exists(log.FilePath));
            log.End("released");

            using var document = JsonDocument.Parse(File.ReadAllText(log.FilePath));
            JsonElement entry = document.RootElement;
            Assert.Equal("0.1.7-test", entry.GetProperty("Session").GetProperty("Version").GetString());
            Assert.Equal(201, entry.GetProperty("SampleCount").GetInt32());
            Assert.Equal(73, entry.GetProperty("DroppedSamples").GetInt32());
            JsonElement samples = entry.GetProperty("Samples");
            Assert.Equal(128, samples.GetArrayLength());
            JsonElement last = samples[127];
            Assert.Equal(199, last.GetProperty("InputTimestamp").GetInt32());
            Assert.Equal(450, last.GetProperty("RequestedSize").GetProperty("X").GetDouble());
            Assert.Equal(460, last.GetProperty("StyledSize").GetProperty("X").GetDouble());
            Assert.Equal(510, last.GetProperty("DesktopPointer").GetProperty("X").GetDouble());
            Assert.False(last.GetProperty("PointerSampled").GetBoolean());
        }
        finally
        {
            window.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public void RotationAndAnUnwritableDestinationCannotInterruptWindowHandling()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dameter-interaction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var window = new Window(); // Width/Height are NaN before an explicit size.
        try
        {
            var log = new WindowInteractionLog(window, "test", directory);
            File.WriteAllText(log.FilePath, new string('x', 1024 * 1024));
            log.Begin("resize", "HorizontalPeak", true);
            log.End("released");
            Assert.Equal(1024 * 1024, new FileInfo(Path.Combine(directory, "interactions.previous.jsonl")).Length);
            using var document = JsonDocument.Parse(File.ReadAllText(log.FilePath));
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("End").GetProperty("StyledSize").GetProperty("X").ValueKind);

            string blockedDirectory = Path.Combine(directory, "regular-file");
            File.WriteAllText(blockedDirectory, "This file cannot be used as a directory.");
            var blocked = new WindowInteractionLog(window, "test", blockedDirectory);
            Exception? error = Record.Exception(() =>
            {
                blocked.Begin("resize", "HorizontalPeak", true);
                blocked.Record("requested", requestedSize: new Size(500, 200));
                blocked.End("released");
            });
            Assert.Null(error);
        }
        finally
        {
            window.Close();
            Directory.Delete(directory, recursive: true);
        }
    }
}
