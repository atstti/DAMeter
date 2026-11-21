using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using DAMeter.Core;
using DAMeter.Mac;
using DAMeter.Mac.Controls;
using DAMeter.Mac.Platform;

namespace DAMeter.Mac.NativeSmoke;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("Native pointer smoke tests require macOS and a logged-in desktop session.");
            return 2;
        }
        return AppBuilder.Configure<SmokeApplication>().UsePlatformDetect().LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }
}

/// <summary>A separate process: these checks must use Avalonia.Native, never the headless backend.</summary>
public sealed class SmokeApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var settings = new AppSettings
            {
                Floating = true,
                CompactFloatingBounds = true,
                Meter = MeterMode.HorizontalPeak,
                FloatingBounds = new WindowBounds { Left = 120, Top = 180, Width = 400, Height = 106 }
            };
            var window = new PointerWindow(new SettingsStore(Path.Combine(Path.GetTempPath(), "DAMeter-native-pointer-smoke")), settings);
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                int exitCode = 0;
                Point originalPointer = MacWindowInterop.GetPointerPosition();
                try
                {
                    await NativePointerChecks.Run(window);
                    Console.WriteLine("PASS: AppKit mouse events, arrow cursor over the resize grip, real Quartz cursor sampling, repeated native resizes, and native frame bounds.");
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error);
                    exitCode = 1;
                }
                finally
                {
                    window.SyntheticLeftPressed = false;
                    NativeInput.Warp(originalPointer);
                    window.PrepareForShutdown();
                    desktop.Shutdown(exitCode);
                }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class PointerWindow : MainWindow
{
    public PointerWindow(SettingsStore store, AppSettings settings) : base(store, settings, smokeTest: true) =>
        AddHandler(InputElement.PointerPressedEvent, (_, e) => LastPointer = e.Pointer,
            RoutingStrategies.Tunnel, handledEventsToo: true);

    // App-local NSEvents deliberately do not forge system HID state or require
    // Accessibility permission. All geometry and cursor sampling remain native.
    public bool SyntheticLeftPressed { get; set; }
    public IPointer? LastPointer { get; private set; }
    protected override bool IsResizeLeftButtonPressed(PointerEventArgs e) => SyntheticLeftPressed;
    protected override Point GetResizeDesktopPosition(PointerEventArgs e) => MacWindowInterop.GetPointerPosition();
}

internal static class NativePointerChecks
{
    public static async Task Run(PointerWindow window)
    {
        await Settle(window);
        var meter = window.FindControl<MeterDisplay>("Meter")!;
        var handle = window.FindControl<Border>("ResizeHandle")!;
        int movesObserved = 0;
        handle.AddHandler(InputElement.PointerMovedEvent, (_, _) => movesObserved++,
            RoutingStrategies.Bubble, handledEventsToo: true);
        foreach (MeterMode mode in Enum.GetValues<MeterMode>())
        {
            window.ViewModel.Meter = mode;
            meter.Update(new MeterBallistics(), mode);
            var work = window.Screens.ScreenFromWindow(window)!.WorkingArea;
            window.Position = new PixelPoint(work.X + 60, work.Y + 90);
            Size initial = FloatingLayout.FromWidth(mode, mode == MeterMode.VerticalPeak ? 220 : 400,
                new Size(work.Width - 100, work.Height - 130));
            window.Width = initial.Width;
            window.Height = initial.Height;
            await Settle(window);
            await VerifyResizeCursor(window, handle, mode);
            for (int repetition = 0; repetition < 6; repetition++)
            {
                await Drag(window, handle, mode, 45);
                await Drag(window, handle, mode, -45);
                await MoveToTopAndBack(window, meter, mode);
            }
            AssertClose(window.ClientSize.Width, initial.Width, 2, $"{mode}: repeated drags changed base width");
        }
        if (movesObserved < 100)
            throw new InvalidOperationException($"Native pointer routing did not run enough samples: {movesObserved}.");
        Console.WriteLine($"Observed {movesObserved} routed AppKit drag samples.");
    }

    private static async Task VerifyResizeCursor(PointerWindow window, Border handle, MeterMode mode)
    {
        // Leave the grip first so a previously selected cursor cannot satisfy
        // this check without routing a new native hover to this meter's grip.
        window.SyntheticLeftPressed = false;
        var away = new Point(window.Position.X + window.ClientSize.Width / 2,
            window.Position.Y + window.ClientSize.Height / 2);
        NativeInput.Warp(away);
        NativeInput.Post(window, NativeInput.MouseMoved, away);
        await Settle(window);

        int hovered = 0;
        EventHandler<PointerEventArgs> onHover = (_, _) => hovered++;
        handle.AddHandler(InputElement.PointerMovedEvent, onHover,
            RoutingStrategies.Bubble, handledEventsToo: true);
        try
        {
            Point local = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)
                ?? throw new InvalidOperationException($"{mode}: resize cursor check cannot locate the native grip.");
            var desktop = new Point(window.Position.X + local.X, window.Position.Y + local.Y);
            NativeInput.Warp(desktop);
            NativeInput.Post(window, NativeInput.MouseMoved, desktop);
            await Settle(window);
            if (hovered == 0)
                throw new InvalidOperationException($"{mode}: resize cursor check received no routed native hover on ResizeHandle at {desktop}.");
            NativeInput.AssertArrowResizeCursor($"{mode}: resize grip hover");
        }
        finally { handle.RemoveHandler(InputElement.PointerMovedEvent, onHover); }
    }

    private static async Task Drag(PointerWindow window, Border handle, MeterMode mode, double widthDelta)
    {
        await Settle(window);
        Size before = window.ClientSize;
        PixelPoint anchor = window.Position;
        Point grip = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("Native resize handle is not in the window visual tree.");
        var start = new Point(anchor.X + grip.X, anchor.Y + grip.Y);
        NativeInput.Warp(start);
        window.SyntheticLeftPressed = true;
        NativeInput.Post(window, NativeInput.MouseDown, start);
        await Task.Delay(40);
        AssertCapture(window, handle, $"{mode}: resize press");

        double ratio = before.Width / before.Height;
        for (int sample = 1; sample <= 6; sample++)
        {
            double delta = widthDelta * sample / 6;
            Point desktop = start + new Vector(delta, delta / ratio);
            NativeInput.Warp(desktop);
            NativeInput.Post(window, NativeInput.MouseDragged, desktop);
            await Settle(window);
            AssertClose(window.ClientSize.Width, before.Width + delta, 2,
                $"{mode}: sample {sample}, delta {delta}, before {before}, anchor {anchor}, pointer {desktop}, actual {window.ClientSize}");
            AssertClose(window.ClientSize.Height, (before.Width + delta) / ratio, 2, $"{mode}: aspect changed");
            if (window.Position != anchor)
                throw new InvalidOperationException($"{mode}: resizing moved the native top-left from {anchor} to {window.Position}.");

            // Re-deliver an event after native layout changed, at the same real
            // desktop pointer. A window-local coordinate feedback loop grows here.
            NativeInput.Post(window, NativeInput.MouseDragged, desktop);
            await Settle(window);
            AssertClose(window.ClientSize.Width, before.Width + delta, 2, $"{mode}: stationary pointer changed size");
        }

        Size onRelease = window.ClientSize;
        Point end = start + new Vector(widthDelta, widthDelta / ratio);
        window.SyntheticLeftPressed = false;
        NativeInput.Post(window, NativeInput.MouseUp, end);
        await Settle(window);
        AssertCapture(window, null, $"{mode}: resize release");
        AssertClose(window.ClientSize.Width, onRelease.Width, 1, $"{mode}: mouse-up changed native size");

        // A delayed move with the button released must not keep sizing.
        Point away = end + new Vector(140, 80);
        NativeInput.Warp(away);
        NativeInput.Post(window, NativeInput.MouseMoved, away);
        await Settle(window);
        AssertClose(window.ClientSize.Width, onRelease.Width, 1, $"{mode}: released pointer changed native size");
    }

    private static async Task MoveToTopAndBack(PointerWindow window, MeterDisplay meter, MeterMode mode)
    {
        var screen = window.Screens.ScreenFromWindow(window)!;
        int top = screen.WorkingArea.Y;
        // The cursor can enter the menu-bar region while the utility window
        // itself remains inside the work area. No native drag session is used.
        await Move(window, meter, mode, Math.Max(screen.Bounds.Y, top - 8), top);
        Point grip = meter.TranslatePoint(new Point(meter.Bounds.Width / 2, meter.Bounds.Height / 2), window)!.Value;
        await Move(window, meter, mode, top + grip.Y + 70, top + 70);
    }

    private static async Task Move(PointerWindow window, MeterDisplay meter, MeterMode mode, double pointerY, int expectedTop)
    {
        Size before = window.ClientSize;
        Point grip = meter.TranslatePoint(new Point(meter.Bounds.Width / 2, meter.Bounds.Height / 2), window)!.Value;
        var start = new Point(window.Position.X + grip.X, window.Position.Y + grip.Y);
        NativeInput.Warp(start);
        window.SyntheticLeftPressed = true;
        NativeInput.Post(window, NativeInput.MouseDown, start);
        await Task.Delay(40);
        AssertCapture(window, meter, $"{mode}: move press");
        for (int sample = 1; sample <= 4; sample++)
        {
            Point desktop = new(start.X, start.Y + (pointerY - start.Y) * sample / 4);
            NativeInput.Warp(desktop);
            NativeInput.Post(window, NativeInput.MouseDragged, desktop);
            await Settle(window);
            AssertClose(window.ClientSize.Width, before.Width, 1, $"{mode}: moving resized native width");
            AssertClose(window.ClientSize.Height, before.Height, 1, $"{mode}: moving resized native height");
        }
        window.SyntheticLeftPressed = false;
        NativeInput.Post(window, NativeInput.MouseUp, new Point(start.X, pointerY));
        await Settle(window);
        AssertCapture(window, null, $"{mode}: move release");
        AssertClose(window.Position.Y, expectedTop, 1, $"{mode}: native move did not reach requested top");
    }

    private static void AssertCapture(PointerWindow window, IInputElement? expected, string message)
    {
        if (window.LastPointer is null || !ReferenceEquals(window.LastPointer.Captured, expected))
            throw new InvalidOperationException($"{message}: expected capture={expected?.GetType().Name ?? "none"}, actual={window.LastPointer?.Captured?.GetType().Name ?? "none"}.");
    }

    private static async Task Settle(Window window)
    {
        await Task.Delay(30);
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static void AssertClose(double actual, double expected, double tolerance, string message)
    {
        if (!double.IsFinite(actual) || Math.Abs(actual - expected) > tolerance)
            throw new InvalidOperationException($"{message}; expected={expected:F3}, actual={actual:F3}.");
    }
}

internal static class NativeInput
{
    public const nuint MouseDown = 1;
    public const nuint MouseUp = 2;
    public const nuint MouseMoved = 5;
    public const nuint MouseDragged = 6;
    private static int eventNumber;

    public static void AssertArrowResizeCursor(string context)
    {
        nint cursorClass = GetClass("NSCursor");
        nint current = GetObject(cursorClass, Selector("currentCursor"));
        if (cursorClass == 0 || current == 0)
            throw new InvalidOperationException($"{context}: NSCursor.currentCursor is unavailable (class=0x{cursorClass:x}, current=0x{current:x}).");
        nint arrow = GetObject(cursorClass, Selector("arrowCursor"));
        nint crosshair = GetObject(cursorClass, Selector("crosshairCursor"));
        string nativeClass = Marshal.PtrToStringUTF8(GetClassName(current)) ?? "unknown";
        string details = $"class={nativeClass}, current=0x{current:x}, arrow=0x{arrow:x}, crosshair=0x{crosshair:x}";
        // Keep the ordinary pointer over the visible grip, without switching
        // to Avalonia.Native's crosshair mapping for diagonal resize cursors.
        if (arrow == 0 || crosshair == 0 || current != arrow || current == crosshair)
            throw new InvalidOperationException($"{context}: expected the ordinary arrow cursor, distinct from the crosshair; {details}.");
        Console.WriteLine($"Verified native arrow cursor over resize grip: {details}.");
    }

    public static void Warp(Point desktop)
    {
        int result = CGWarpMouseCursorPosition(new NativePoint(desktop.X, desktop.Y));
        if (result != 0)
            throw new InvalidOperationException($"CGWarpMouseCursorPosition failed with status {result}.");
        Point actual = MacWindowInterop.GetPointerPosition();
        if (Math.Abs(actual.X - desktop.X) > 2 || Math.Abs(actual.Y - desktop.Y) > 2)
            throw new InvalidOperationException($"Quartz cursor warp did not reach its target: expected={desktop}, actual={actual}.");
    }

    public static void Post(Window window, nuint type, Point desktop)
    {
        nint handle = window.TryGetPlatformHandle()?.Handle ?? throw new InvalidOperationException("No NSWindow handle.");
        nint app = GetObject(GetClass("NSApplication"), Selector("sharedApplication"));
        nint number = GetInteger(handle, Selector("windowNumber"));
        Point local = new(desktop.X - window.Position.X, window.ClientSize.Height - (desktop.Y - window.Position.Y));
        nint input = CreateMouseEvent(GetClass("NSEvent"), Selector("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
            type, new NativePoint(local.X, local.Y), 0, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency,
            number, 0, ++eventNumber, 1, type == MouseUp ? 0 : 1);
        if (input == 0) throw new InvalidOperationException("NSEvent creation returned nil.");
        PostEvent(app, Selector("postEvent:atStart:"), input, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(double x, double y)
    {
        public readonly double X = x;
        public readonly double Y = y;
    }
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern int CGWarpMouseCursorPosition(NativePoint point);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern nint GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "object_getClassName")]
    private static extern nint GetClassName(nint instance);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint GetObject(nint target, nint selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint GetInteger(nint target, nint selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint CreateMouseEvent(nint target, nint selector, nuint type, NativePoint location,
        nuint modifierFlags, double timestamp, nint windowNumber, nint context, nint eventNumber, nint clickCount, float pressure);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void PostEvent(nint target, nint selector, nint input, byte atStart);
}
