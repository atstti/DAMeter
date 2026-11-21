using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

namespace DAMeter.Mac.Platform;

/// <summary>
/// Bounded, local diagnostics for requested and actual window geometry. Samples
/// stay in memory during a drag; one JSON line is written when the drag ends.
/// </summary>
public sealed class WindowInteractionLog(Window window, string version, string? directory = null)
{
    private const int SampleLimit = 128;
    private const long FileLimit = 1024 * 1024;
    private readonly Queue<Sample> samples = new(SampleLimit);
    private readonly string logDirectory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DAMeter");
    private Session? session;
    private long started;
    private int sampleCount;
    private Size? lastRequested;
    private Point? lastPointer;

    public string FilePath => Path.Combine(logDirectory, "interactions.jsonl");

    public void Begin(string kind, string meter, bool floating, Point? origin = null, Size? startSize = null)
    {
        try
        {
            End("replaced");
            samples.Clear();
            sampleCount = 0;
            lastRequested = startSize;
            lastPointer = origin;
            started = Stopwatch.GetTimestamp();
            session = new Session(DateTimeOffset.UtcNow, version,
                Environment.OSVersion.VersionString, RuntimeInformation.ProcessArchitecture.ToString(),
                kind, meter, floating, Capture("begin", null, null, null, null, null, null));
        }
        catch (Exception) { session = null; }
    }

    public void Record(string eventName, ulong? inputTimestamp = null, Point? desktopPointer = null,
        Size? requestedSize = null, bool? rawLeftPressed = null, bool? physicalLeftPressed = null,
        bool? pointerCaptured = null)
    {
        if (session is null) return;
        try
        {
            if (desktopPointer.HasValue) lastPointer = desktopPointer;
            if (requestedSize.HasValue) lastRequested = requestedSize;
            Sample sample = Capture(eventName, inputTimestamp, desktopPointer.HasValue, requestedSize.HasValue,
                rawLeftPressed, physicalLeftPressed, pointerCaptured);
            if (samples.Count == SampleLimit) samples.Dequeue();
            samples.Enqueue(sample);
            sampleCount++;
        }
        catch (Exception) { /* Diagnostics must not affect pointer handling. */ }
    }

    public void End(string reason)
    {
        Session? completed = session;
        session = null;
        if (completed is null) return;
        try
        {
            var entry = new
            {
                Schema = 1,
                Session = completed,
                SampleCount = sampleCount,
                DroppedSamples = Math.Max(0, sampleCount - samples.Count),
                Samples = samples.ToArray(),
                End = Capture("end", null, null, null, null, null, null),
                Reason = reason
            };
            string line = JsonSerializer.Serialize(entry) + Environment.NewLine;
            Directory.CreateDirectory(logDirectory);
            string path = FilePath;
            long incomingLength = Encoding.UTF8.GetByteCount(line);
            if (File.Exists(path) && new FileInfo(path).Length + incomingLength > FileLimit)
                File.Move(path, Path.Combine(logDirectory, "interactions.previous.jsonl"), overwrite: true);
            File.AppendAllText(path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception) { /* An unavailable diagnostic file must never interrupt a drag. */ }
        finally { samples.Clear(); }
    }

    private Sample Capture(string eventName, ulong? inputTimestamp, bool? pointerSampled,
        bool? sizeRequested, bool? rawLeftPressed, bool? physicalLeftPressed, bool? pointerCaptured)
    {
        Size? frame = window.FrameSize;
        return new Sample(eventName, Stopwatch.GetElapsedTime(started).TotalMilliseconds, inputTimestamp,
            pointerSampled, sizeRequested,
            Pair.From(lastPointer?.X, lastPointer?.Y), Pair.From(lastRequested?.Width, lastRequested?.Height),
            Pair.From(window.Width, window.Height), Pair.From(window.ClientSize.Width, window.ClientSize.Height),
            Pair.From(window.Bounds.Width, window.Bounds.Height), Pair.From(window.Position.X, window.Position.Y),
            Pair.From(frame?.Width, frame?.Height), Finite(window.DesktopScaling), Finite(window.RenderScaling),
            window.WindowState.ToString(), rawLeftPressed, physicalLeftPressed, pointerCaptured);
    }

    private static double? Finite(double? number) => number.HasValue && double.IsFinite(number.Value) ? number : null;

    private sealed record Pair(double? X, double? Y)
    {
        public static Pair From(double? x, double? y) => new(Finite(x), Finite(y));
    }

    private sealed record Session(DateTimeOffset StartedUtc, string Version, string OperatingSystem,
        string Architecture, string Kind, string Meter, bool Floating, Sample Start);

    private sealed record Sample(string Event, double ElapsedMilliseconds, ulong? InputTimestamp,
        bool? PointerSampled, bool? SizeRequested, Pair DesktopPointer, Pair RequestedSize,
        Pair StyledSize, Pair ClientSize, Pair LayoutSize, Pair Position, Pair FrameSize,
        double? DesktopScaling, double? RenderScaling, string WindowState,
        bool? RawLeftPressed, bool? PhysicalLeftPressed, bool? PointerCaptured);
}
