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
    public void RepeatedPointerDragsKeepTheAspectRatioAndReleaseCapture(MeterMode mode, double width, double height)
    {
        using var fixture = new WindowFixture(mode, width, height);
        double initialWidth = fixture.Window.Width;
        double ratio = fixture.Window.Width / fixture.Window.Height;
        for (int i = 0; i < 4; i++)
        {
            Drag(fixture, new Vector(80, 80 / ratio), new Vector(40, 40 / ratio));
            Drag(fixture, new Vector(-25, -25 / ratio));
        }
        Assert.InRange(Math.Abs(fixture.Window.Width - initialWidth - 60), 0, 1);
        AssertTight(fixture);
    }

    [AvaloniaFact]
    public void MinimumSizeCanBeReversedAndAnotherGestureStillWorks()
    {
        using var fixture = new WindowFixture(MeterMode.HorizontalPeak, 300, 220);
        double ratio = fixture.Window.Width / fixture.Window.Height;
        Drag(fixture, new Vector(-1000, -1000), new Vector(-50, -50 / ratio));
        Assert.InRange(Math.Abs(fixture.Window.Width - 250), 0, 1);
        Drag(fixture, new Vector(80, 80 / ratio));
        Assert.InRange(Math.Abs(fixture.Window.Width - 330), 0, 1);
        AssertTight(fixture);
    }

    [AvaloniaFact]
    public void LostCaptureStopsSizingAndAllowsANewGesture()
    {
        using var fixture = new WindowFixture(MeterMode.HorizontalPeak, 500, 220);
        Point start = Press(fixture);
        Move(fixture, start + new Vector(50, 20));
        Size stopped = fixture.Window.ClientSize;
        fixture.Pointer!.Capture(null);
        Move(fixture, start + new Vector(200, 200));
        Assert.Equal(stopped, fixture.Window.ClientSize);
        Release(fixture, start + new Vector(200, 200));
        Drag(fixture, new Vector(30, 10));
        Assert.True(fixture.Window.Width > stopped.Width);
    }

    [AvaloniaFact]
    public void WindowOriginChangesCannotResizeAStationaryDesktopPointer()
    {
        using var fixture = new WindowFixture(MeterMode.HorizontalPeak, 500, 220);
        Point start = Press(fixture);
        Move(fixture, start + new Vector(40, 10));
        Size before = fixture.Window.ClientSize;
        fixture.Window.Position = new PixelPoint(-240, 310);
        Pump();
        Move(fixture, start + new Vector(40, 10));
        Assert.Equal(before, fixture.Window.ClientSize);
        Release(fixture, start + new Vector(40, 10));
    }

    [AvaloniaFact]
    public void AQueuedHeldMoveAfterTheRealButtonWasReleasedCannotEnlargeTheWindow()
    {
        using var fixture = new WindowFixture(MeterMode.HorizontalPeak, 500, 220);
        Point start = Press(fixture);
        Move(fixture, start + new Vector(40, 10));
        Size before = fixture.Window.ClientSize;
        fixture.Window.PhysicalLeftPressed = false;
        Move(fixture, start + new Vector(10000, 10000));
        Assert.Equal(before, fixture.Window.ClientSize);
        Assert.Null(fixture.Pointer!.Captured);
        Release(fixture, start);
        fixture.Window.PhysicalLeftPressed = true;
        Drag(fixture, new Vector(20, 5));
        Assert.True(fixture.Window.Width > before.Width);
    }

    [AvaloniaFact]
    public void MouseUpWithStaleLocalCoordinatesCannotMakeAFinalSizeJump()
    {
        using var fixture = new WindowFixture(MeterMode.HorizontalPeak, 500, 220);
        Point start = Press(fixture);
        Move(fixture, start + new Vector(40, 10));
        Size before = fixture.Window.ClientSize;
        fixture.Window.MouseUp(new Point(1500, 1000), MouseButton.Left);
        Pump();
        Assert.Equal(before, fixture.Window.ClientSize);
        Assert.Null(fixture.Pointer!.Captured);
    }

    [AvaloniaFact]
    public void FloatingRoundTripKeepsItsScaleAndNormalGeometry()
    {
        using var fixture = new WindowFixture(MeterMode.HorizontalPeak, 360, 200);
        Drag(fixture, new Vector(80, 20));
        Size floating = fixture.Window.ClientSize;
        fixture.Window.ViewModel.Floating = false; Pump();
        Assert.Equal(700, fixture.Window.Width);
        Assert.Equal(450, fixture.Window.Height);
        fixture.Window.ViewModel.Floating = true; Pump();
        Assert.InRange(Math.Abs(fixture.Window.Width - floating.Width), 0, 1);
        AssertTight(fixture);
        Drag(fixture, new Vector(20, 5));
        Assert.True(fixture.Window.Width > floating.Width);
    }

    [AvaloniaFact]
    public void SwitchingModesPreservesScaleAndHasNoInvisibleTopMargin()
    {
        using var fixture = new WindowFixture(MeterMode.HorizontalPeak, 500, 220);
        foreach (MeterMode mode in new[] { MeterMode.VerticalPeak, MeterMode.AnalogVu, MeterMode.HorizontalPeak })
        {
            fixture.Window.ViewModel.Meter = mode; Pump();
            AssertTight(fixture);
            var work = fixture.Window.Screens.Primary!.WorkingArea;
            fixture.Window.Position = new PixelPoint(work.X, work.Y); Pump();
            Assert.InRange(fixture.Meter.GetFaceBounds(mode).Top, 0, 1);
            Assert.Equal(work.Y, fixture.Window.Position.Y);
            Drag(fixture, new Vector(30, 20));
        }
    }

    [AvaloniaFact]
    public void LegacyFloatingBoundsLoseTheLetterboxWithoutGrowingTheVisibleMeter()
    {
        var settings = new AppSettings { Floating = true,
            FloatingBounds = new WindowBounds { Left = 100, Top = 100, Width = 500, Height = 220 } };
        using var fixture = new WindowFixture(settings);
        Assert.True(settings.CompactFloatingBounds);
        Assert.InRange(fixture.Window.Width, 488, 490);
        Assert.InRange(fixture.Window.Height, 128, 130);
        Assert.Equal(new PixelPoint(105, 145), fixture.Window.Position);
        AssertTight(fixture);
    }

    private static void AssertTight(WindowFixture fixture)
    {
        var viewport = MeterDisplay.GetFloatingViewport(fixture.Window.ViewModel.Meter);
        Assert.InRange(Math.Abs(fixture.Window.Width / fixture.Window.Height - viewport.Width / viewport.Height), 0, 0.015);
        double scale = Math.Min(fixture.Meter.Bounds.Width / viewport.Width, fixture.Meter.Bounds.Height / viewport.Height);
        double paintedTop = fixture.Meter.GetFaceBounds(fixture.Window.ViewModel.Meter).Top - 0.75 * scale;
        Assert.InRange(paintedTop, 0, 0.6); // At most half a logical pixel from native layout rounding.
    }
    private static void Drag(WindowFixture fixture, params Vector[] offsets)
    {
        Point start = Press(fixture);
        foreach (var offset in offsets) Move(fixture, start + offset);
        Size beforeRelease = fixture.Window.ClientSize;
        Release(fixture, start + offsets[^1]);
        Assert.Equal(beforeRelease, fixture.Window.ClientSize);
        Assert.Null(fixture.Pointer!.Captured);
    }
    private static Point Press(WindowFixture fixture)
    {
        fixture.Window.MouseMove(new Point(0, 0)); Pump();
        Point local = fixture.Handle.TranslatePoint(new Point(10, 10), fixture.Window)!.Value;
        fixture.Window.DesktopPointer = new Point(fixture.Window.Position.X + local.X, fixture.Window.Position.Y + local.Y);
        fixture.Window.MouseMove(local); Pump();
        Assert.Same(fixture.Handle, fixture.Window.InputHitTest(local));
        fixture.Window.MouseDown(local, MouseButton.Left); Pump();
        Assert.Same(fixture.Handle, fixture.Pointer!.Captured);
        return fixture.Window.DesktopPointer;
    }
    private static void Move(WindowFixture fixture, Point desktop)
    {
        fixture.Window.DesktopPointer = desktop;
        fixture.Window.MouseMove(new Point(desktop.X - fixture.Window.Position.X, desktop.Y - fixture.Window.Position.Y), RawInputModifiers.LeftMouseButton);
        Pump();
    }
    private static void Release(WindowFixture fixture, Point desktop)
    {
        fixture.Window.MouseUp(new Point(desktop.X - fixture.Window.Position.X, desktop.Y - fixture.Window.Position.Y), MouseButton.Left);
        Pump();
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private sealed class TestWindow(SettingsStore store, AppSettings settings) : MainWindow(store, settings, smokeTest: true)
    {
        public Point DesktopPointer { get; set; }
        public bool PhysicalLeftPressed { get; set; } = true;
        protected override Point GetResizeDesktopPosition(PointerEventArgs e) => DesktopPointer;
        protected override bool IsResizeLeftButtonPressed(PointerEventArgs e) => PhysicalLeftPressed;
    }
    private sealed class WindowFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"dameter-resize-{Guid.NewGuid():N}");
        public TestWindow Window { get; }
        public Border Handle { get; }
        public MeterDisplay Meter { get; }
        public IPointer? Pointer { get; private set; }
        public WindowFixture(MeterMode mode, double width, double height) : this(new AppSettings
        {
            CompactFloatingBounds = true, Floating = true, Meter = mode,
            NormalBounds = new WindowBounds { Left = 25, Top = 35, Width = 700, Height = 450 },
            FloatingBounds = new WindowBounds { Left = 100, Top = 100, Width = width, Height = height }
        }) { }
        public WindowFixture(AppSettings settings)
        {
            Window = new TestWindow(new SettingsStore(directory), settings);
            Handle = Window.FindControl<Border>("ResizeHandle")!;
            Meter = Window.FindControl<MeterDisplay>("Meter")!;
            Handle.AddHandler(InputElement.PointerPressedEvent, (_, e) => Pointer = e.Pointer, RoutingStrategies.Tunnel, handledEventsToo: true);
            Window.Show(); Pump();
        }
        public void Dispose()
        {
            Pointer?.Capture(null);
            Window.MouseUp(new Point(0, 0), MouseButton.Left);
            Window.PrepareForShutdown(); Window.Close(); Pump();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
