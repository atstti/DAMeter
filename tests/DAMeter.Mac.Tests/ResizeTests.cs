using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DAMeter.Core;
using DAMeter.Mac.Controls;
using Xunit;

namespace DAMeter.Mac.Tests;

public sealed class ResizeTests
{
    [AvaloniaTheory]
    [InlineData(MeterMode.HorizontalPeak, 500, 220)]
    [InlineData(MeterMode.HorizontalPeak, 900, 140)]
    [InlineData(MeterMode.VerticalPeak, 240, 600)]
    [InlineData(MeterMode.AnalogVu, 850, 420)]
    public void RepeatedPointerDragsResizeAfterTheHandleMoves(MeterMode mode, double width, double height)
    {
        using var fixture = new WindowFixture(mode, width, height);
        for (int iteration = 0; iteration < 4; iteration++)
        {
            // The face and its handle move during the gesture. A position
            // relative to that handle would feed this movement back into sizing.
            Drag(fixture, new Vector(80, 60), new Vector(40, 30));
            Drag(fixture, new Vector(-25, -15));
        }
        AssertSize(fixture.Window, width + 60, height + 60);
        Assert.True(fixture.Window.CanResize);
        Assert.True(fixture.Window.IsVisible);
    }

    [AvaloniaFact]
    public void MinimumSizeCanBeReversedAndAnotherGestureStillWorks()
    {
        using var fixture = new WindowFixture(MeterMode.HorizontalPeak, 300, 220);
        Drag(fixture, new Vector(-500, -500), new Vector(-50, -30));
        AssertSize(fixture.Window, 250, 190);

        Drag(fixture, new Vector(80, 50));
        AssertSize(fixture.Window, 330, 240);
    }

    [AvaloniaFact]
    public void LostPointerCaptureStopsSizingAndAllowsTheNextGesture()
    {
        using var fixture = new WindowFixture(MeterMode.HorizontalPeak, 500, 220);
        Point start = PressHandle(fixture);
        MoveHeld(fixture.Window, start + new Vector(50, 30));
        AssertSize(fixture.Window, 550, 250);
        Assert.NotNull(fixture.LastPointer);

        fixture.LastPointer.Capture(null);
        Pump();
        MoveHeld(fixture.Window, start + new Vector(160, 120));
        AssertSize(fixture.Window, 550, 250);
        fixture.Window.MouseUp(start + new Vector(160, 120), MouseButton.Left);
        Pump();

        Drag(fixture, new Vector(25, 20));
        AssertSize(fixture.Window, 575, 270);
    }

    [AvaloniaFact]
    public void AResizedFloatingWindowKeepsItsSizeAfterReturningToNormalAndBack()
    {
        using var fixture = new WindowFixture(MeterMode.HorizontalPeak, 360, 200);
        Drag(fixture, new Vector(100, 70));
        AssertSize(fixture.Window, 460, 270);

        fixture.Window.ViewModel.Floating = false;
        Pump();
        AssertSize(fixture.Window, 700, 450);

        fixture.Window.ViewModel.Floating = true;
        Pump();
        AssertSize(fixture.Window, 460, 270);
        Drag(fixture, new Vector(20, 15));
        AssertSize(fixture.Window, 480, 285);
        Assert.Equal(700, fixture.Window.ViewModel.Settings.NormalBounds.Width);
        Assert.Equal(450, fixture.Window.ViewModel.Settings.NormalBounds.Height);
    }

    [AvaloniaFact]
    public void SwitchingMetersRepositionsTheHandleWithoutResizingTheWindow()
    {
        using var fixture = new WindowFixture(MeterMode.HorizontalPeak, 500, 220);
        MeterDisplay meter = fixture.Window.FindControl<MeterDisplay>("Meter")!;
        foreach (MeterMode mode in new[] { MeterMode.VerticalPeak, MeterMode.AnalogVu, MeterMode.HorizontalPeak })
        {
            double width = fixture.Window.Bounds.Width;
            double height = fixture.Window.Bounds.Height;
            fixture.Window.ViewModel.Meter = mode;
            Pump();
            AssertSize(fixture.Window, width, height);

            Point faceCorner = meter.TranslatePoint(meter.GetFaceBounds(mode).BottomRight, fixture.Window)!.Value;
            Point handleCorner = fixture.Handle.TranslatePoint(
                new Point(fixture.Handle.Bounds.Width, fixture.Handle.Bounds.Height), fixture.Window)!.Value;
            // Arranging onto physical pixels can round the painted coordinates.
            Assert.InRange(Math.Abs(faceCorner.X - handleCorner.X), 0, 1);
            Assert.InRange(Math.Abs(faceCorner.Y - handleCorner.Y), 0, 1);

            Drag(fixture, new Vector(30, 20));
        }
    }

    private static void Drag(WindowFixture fixture, params Vector[] offsets)
    {
        double startWidth = fixture.Window.Bounds.Width;
        double startHeight = fixture.Window.Bounds.Height;
        Point start = PressHandle(fixture);
        foreach (Vector offset in offsets)
        {
            MoveHeld(fixture.Window, start + offset);
            AssertSize(fixture.Window,
                Math.Max(fixture.Window.MinWidth, startWidth + offset.X),
                Math.Max(fixture.Window.MinHeight, startHeight + offset.Y));
        }
        fixture.Window.MouseUp(start + offsets[^1], MouseButton.Left);
        Pump();
        Assert.Null(fixture.LastPointer?.Captured);
    }

    private static Point PressHandle(WindowFixture fixture)
    {
        Pump();
        fixture.Window.MouseMove(new Point(0, 0));
        Pump();
        Border handle = fixture.Handle;
        Point point = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), fixture.Window)
            ?? throw new InvalidOperationException("The resize handle must belong to the visible window.");
        // Headless pointer input processes pending render/layout work before
        // hit testing, just as a real pointer event would.
        fixture.Window.MouseMove(point);
        Pump();
        Assert.Same(handle, fixture.Window.InputHitTest(point));
        fixture.Window.MouseDown(point, MouseButton.Left);
        Pump();
        Assert.NotNull(fixture.LastPointer);
        Assert.Same(handle, fixture.LastPointer.Captured);
        return point;
    }

    private static void MoveHeld(MainWindow window, Point point)
    {
        window.MouseMove(point, RawInputModifiers.LeftMouseButton);
        Pump();
    }

    private static void AssertSize(MainWindow window, double width, double height)
    {
        Assert.Equal(width, window.Width, 5);
        Assert.Equal(height, window.Height, 5);
        Assert.Equal(width, window.Bounds.Width, 5);
        Assert.Equal(height, window.Bounds.Height, 5);
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private sealed class WindowFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"dameter-resize-tests-{Guid.NewGuid():N}");
        public MainWindow Window { get; }
        public Border Handle { get; }
        public IPointer? LastPointer { get; private set; }

        public WindowFixture(MeterMode mode, double width, double height)
        {
            var settings = new AppSettings
            {
                Floating = true,
                Meter = mode,
                NormalBounds = new WindowBounds { Left = 25, Top = 35, Width = 700, Height = 450 },
                FloatingBounds = new WindowBounds { Left = 100, Top = 100, Width = width, Height = height }
            };
            Window = new MainWindow(new SettingsStore(directory), settings, smokeTest: true);
            Handle = Window.FindControl<Border>("ResizeHandle")!;
            Handle.AddHandler(InputElement.PointerPressedEvent, (_, e) => LastPointer = e.Pointer,
                RoutingStrategies.Tunnel, handledEventsToo: true);
            Window.Show();
            Pump();
        }

        public void Dispose()
        {
            LastPointer?.Capture(null);
            Window.MouseUp(new Point(0, 0), MouseButton.Left);
            Window.PrepareForShutdown();
            Window.Close();
            Pump();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
