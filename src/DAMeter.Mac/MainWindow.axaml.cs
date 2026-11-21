using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DAMeter.Core;
using DAMeter.Mac.Platform;
using DAMeter.Mac.ViewModels;
using DAMeter.Mac.Audio;
using DAMeter.Mac.Controls;

namespace DAMeter.Mac;

public partial class MainWindow : Window
{
    private readonly SettingsStore store;
    private readonly bool smokeTest;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly MacTrayService? tray;
    private SettingsWindow? settingsWindow;
    private bool appliedFloating;
    private bool applyingBounds;
    private bool opened;
    private bool exiting;
    private bool shutdownRequested;
    private IPointer? resizePointer;
    private WindowResizeGesture? resizeGesture;
    private readonly WindowInteractionLog? interactionLog;
    private MeterMode appliedMeter;
    private readonly TaskCompletionSource shutdownCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsShutdownPrepared => exiting;
    public bool IsShutdownRequested => shutdownRequested || exiting;
    public Task ShutdownCompleted => shutdownCompletion.Task;
    public MainViewModel ViewModel { get; }
    protected virtual bool HasRecoveryIcon => tray?.IsAvailable == true;

    public MainWindow() : this(new SettingsStore(), new AppSettings(), Design.IsDesignMode) { }

    public MainWindow(SettingsStore store, AppSettings settings, bool smokeTest = false, NativeMacAudioService? audio = null)
    {
        InitializeComponent();
        this.store = store;
        this.smokeTest = smokeTest;
        if (!smokeTest)
            interactionLog = new WindowInteractionLog(this,
                typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown");
        ViewModel = new MainViewModel(store, settings, audio);
        DataContext = ViewModel;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://DAMeter.Mac/Assets/dameter.png")));
        ViewModel.FrameReady += (state, mode) => { if (IsVisible) Meter.Update(state, mode); };
        ViewModel.OpenSettingsRequested += OpenSettings;
        ViewModel.OpenPermissionsRequested += OpenPermissions;
        ViewModel.OpenDiagnosticsRequested += OpenDiagnostics;
        ViewModel.PropertyChanged += SettingsChanged;
        saveTimer.Tick += (_, _) => Persist();
        if (!smokeTest || (Program.SmokeTest && OperatingSystem.IsMacOS()))
        {
            tray = new MacTrayService(Application.Current!, ShowAccessible, HideSafely, ReturnToNormal,
                () => { ShowAccessible(); ViewModel.Floating = true; }, ToggleClickThrough,
                () => ViewModel.Topmost = !ViewModel.Topmost, OpenSettings, Exit);
            if (Program.SmokeTest && !tray.IsAvailable)
                throw new InvalidOperationException("The native menu-bar recovery icon is unavailable during the quit smoke test.");
        }
        appliedMeter = settings.Meter;
        if (!settings.CompactFloatingBounds)
        {
            var point = new PixelPoint((int)settings.FloatingBounds.Left, (int)settings.FloatingBounds.Top);
            double scale = Screens.ScreenFromPoint(point)?.Scaling ?? DesktopScaling;
            settings.FloatingBounds = FloatingLayout.Migrate(settings.FloatingBounds, settings.Meter, scale);
            settings.CompactFloatingBounds = true;
        }
        Size compact = FloatingLayout.FromWidth(settings.Meter, settings.FloatingBounds.Width,
            MaximumClientSize(new PixelPoint((int)settings.FloatingBounds.Left, (int)settings.FloatingBounds.Top)));
        settings.FloatingBounds.Width = compact.Width;
        settings.FloatingBounds.Height = compact.Height;
        appliedFloating = settings.Floating;
        CanMaximize = !appliedFloating;
        ApplyBounds(appliedFloating ? settings.FloatingBounds : settings.NormalBounds);
        Topmost = settings.Topmost;
        Opened += (_, _) =>
        {
            opened = true;
            MacWindowInterop.EnsureVisible(this);
            if (smokeTest)
            {
                ViewModel.Status = "UI smoke test: no audio capture or permission request";
                Meter.Update(new MeterBallistics(), ViewModel.Meter);
                // Unit tests also suppress capture, but own their window's
                // lifetime. Only the command-line smoke run animates/exits.
                if (Program.SmokeTest)
                {
                    if (OperatingSystem.IsMacOS()) _ = VerifyNativeAndExitAsync();
                    else
                    {
                        DispatcherTimer.RunOnce(() => Meter.Update(new MeterBallistics(), MeterMode.AnalogVu), TimeSpan.FromMilliseconds(200));
                        DispatcherTimer.RunOnce(() => Meter.Update(new MeterBallistics(), MeterMode.VerticalPeak), TimeSpan.FromMilliseconds(400));
                        DispatcherTimer.RunOnce(Exit, TimeSpan.FromSeconds(1));
                    }
                }
            }
            else
            {
                ApplyClickThrough();
                ViewModel.Start();
            }
        };
        Closing += (_, e) =>
        {
            if (exiting) return;
            if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
            {
                PrepareForNativeShutdown();
                return;
            }
            if (HasRecoveryIcon) { e.Cancel = true; HideSafely(); }
            else
            {
                // Let this close finish instead of recursively calling Close
                // from inside Closing when no recovery icon is available.
                e.Cancel = true;
                Exit();
            }
        };
        Closed += (_, _) =>
        {
            FinishInteractions();
            saveTimer.Stop();
            LayoutUpdated -= UpdateResizeHandle;
            Screens.Changed -= ScreensChanged;
            tray?.Dispose();
            ViewModel.Dispose();
            shutdownCompletion.TrySetResult();
        };
        PositionChanged += (_, _) => GeometryChanged();
        SizeChanged += (_, _) => GeometryChanged();
        LayoutUpdated += UpdateResizeHandle;
        Deactivated += (_, _) => FinishInteractions();
        Screens.Changed += ScreensChanged;
    }

    private void UpdateResizeHandle(object? sender, EventArgs e)
    {
        // Match the painted face rather than the transparent letterbox around
        // it. This keeps the grip on the meter at every size and aspect ratio.
        var corner = new Point(Bounds.Width, Bounds.Height);
        if (ViewModel.Floating)
        {
            Rect face = Meter.GetFaceBounds(ViewModel.Meter);
            if (face.Width > 0 && face.Height > 0)
                corner = Meter.TranslatePoint(face.BottomRight, this) ?? corner;
        }
        double left = Math.Clamp(corner.X - ResizeHandle.Width, 0, Math.Max(0, Bounds.Width - ResizeHandle.Width));
        double top = Math.Clamp(corner.Y - ResizeHandle.Height, 0, Math.Max(0, Bounds.Height - ResizeHandle.Height));
        if (!double.IsFinite(Canvas.GetLeft(ResizeHandle)) || Math.Abs(Canvas.GetLeft(ResizeHandle) - left) > 0.01)
            Canvas.SetLeft(ResizeHandle, left);
        if (!double.IsFinite(Canvas.GetTop(ResizeHandle)) || Math.Abs(Canvas.GetTop(ResizeHandle) - top) > 0.01)
            Canvas.SetTop(ResizeHandle, top);
    }

    private void GeometryChanged()
    {
        interactionLog?.Record("geometry");
        if (!opened || applyingBounds || WindowState != WindowState.Normal) return;
        CaptureBounds();
        ScheduleSave();
    }
    private async Task VerifyNativeAndExitAsync()
    {
        var exitCode = 0;
        try
        {
            await ViewModel.VerifyNativeAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(VerifyNativeLayoutAsync).ConfigureAwait(false);
        }
        catch (Exception ex) { store.Log(ex); Console.Error.WriteLine(ex); exitCode = 1; }
        Dispatcher.UIThread.Post(() =>
        {
            if (exitCode == 0 && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Exercise the same cancelable shutdown path as Dock Quit. In
                // particular, the recovery icon must not turn Quit into Hide.
                if (!desktop.TryShutdown() && !IsShutdownRequested)
                {
                    Console.Error.WriteLine("Native application shutdown was unexpectedly canceled.");
                    PrepareForShutdown();
                    desktop.Shutdown(1);
                }
            }
            else ExitWithCode(exitCode);
        });
    }
    private async Task VerifyNativeLayoutAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        _ = MacWindowInterop.GetPointerPosition(); // Read only; posts no system input.
        _ = MacWindowInterop.IsLeftButtonPressed();
        ViewModel.Floating = true;
        await Task.Delay(100);
        foreach (MeterMode mode in new[] { MeterMode.HorizontalPeak, MeterMode.VerticalPeak, MeterMode.AnalogVu })
        {
            ViewModel.Meter = mode;
            Meter.Update(new MeterBallistics(), mode);
            await Task.Delay(100);
            foreach (double width in new[] { 350.0, 520, 280, 450 })
            {
                Size expected = FloatingLayout.FromWidth(mode, width, MaximumClientSize(Position));
                SetFloatingSize(width);
                // Native callbacks and the child layout can settle on separate
                // frames, particularly during the first Metal/font compilation.
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    UpdateLayout();
                    bool sizeReady = Math.Abs(ClientSize.Width - expected.Width) <= 1.5 &&
                        Math.Abs(ClientSize.Height - expected.Height) <= 1.5;
                    bool layoutReady = Meter.Compact && Math.Abs(Meter.Bounds.Width - ClientSize.Width) <= 1.5 &&
                        Math.Abs(Meter.Bounds.Height - ClientSize.Height) <= 1.5 &&
                        Math.Abs(Meter.GetPaintedFaceBounds(mode).Top) <= 1 / RenderScaling;
                    if (sizeReady && layoutReady) break;
                    if (deadline.Elapsed > TimeSpan.FromSeconds(3))
                        throw new InvalidOperationException($"Native layout did not settle: mode={mode}, expected={expected}, client={ClientSize}, meter={Meter.Bounds}, painted={Meter.GetPaintedFaceBounds(mode)}, compact={Meter.Compact}.");
                    await Task.Delay(25);
                }
            }
        }
        Screen? screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is not null)
        {
            Position = new PixelPoint(screen.WorkingArea.X + 2, screen.WorkingArea.Y + 2);
            await Task.Delay(100);
            if (Position.Y > screen.WorkingArea.Y + 4)
                throw new InvalidOperationException("The native floating window cannot reach the top of its desktop working area.");
        }
        ViewModel.Floating = false;
        await Task.Delay(100);
        Console.WriteLine("Verified native desktop pointer access, 12 compact window resizes, and top-edge placement without capture permission.");
    }
    private void ScreensChanged(object? sender, EventArgs e)
    {
        if (exiting) return;
        FinishInteractions();
        if (appliedFloating) SetFloatingSize(Width);
        MacWindowInterop.EnsureVisible(this);
        Dispatcher.UIThread.Post(() => { CaptureBounds(); ScheduleSave(); }, DispatcherPriority.Loaded);
    }
    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Floating))
        {
            FinishInteractions();
            CaptureBounds();
            applyingBounds = true;
            try
            {
                appliedFloating = ViewModel.Floating;
                CanMaximize = !appliedFloating;
                if (!appliedFloating) ViewModel.ClickThrough = false;
                WindowState = WindowState.Normal;
                ApplyBounds(appliedFloating ? ViewModel.Settings.FloatingBounds : ViewModel.Settings.NormalBounds);
                ApplyClickThrough();
                MacWindowInterop.EnsureVisible(this);
            }
            finally { applyingBounds = false; }
            Dispatcher.UIThread.Post(() => { CaptureBounds(); ScheduleSave(); }, DispatcherPriority.Loaded);
        }
        else if (e.PropertyName == nameof(MainViewModel.Topmost)) Topmost = ViewModel.Topmost;
        else if (e.PropertyName == nameof(MainViewModel.Meter))
        {
            FinishInteractions();
            CaptureBounds();
            double scale = ViewModel.Settings.FloatingBounds.Width / MeterDisplay.GetFloatingViewport(appliedMeter).Width;
            appliedMeter = ViewModel.Meter;
            Rect viewport = MeterDisplay.GetFloatingViewport(appliedMeter);
            Size size = FloatingLayout.FromWidth(appliedMeter, viewport.Width * scale, MaximumClientSize(Position));
            ViewModel.Settings.FloatingBounds.Width = size.Width;
            ViewModel.Settings.FloatingBounds.Height = size.Height;
            if (appliedFloating)
            {
                ApplyBounds(ViewModel.Settings.FloatingBounds);
                MacWindowInterop.EnsureVisible(this);
            }
            UpdateResizeHandle(this, EventArgs.Empty);
        }
        else if (e.PropertyName == nameof(MainViewModel.ClickThrough))
        {
            FinishInteractions();
            ApplyClickThrough();
        }
        if (e.PropertyName != nameof(MainViewModel.Status) && !applyingBounds) ScheduleSave();
    }
    private void ApplyClickThrough()
    {
        if (!opened || smokeTest) return;
        var enabled = ViewModel.Floating && ViewModel.ClickThrough;
        if (enabled && !HasRecoveryIcon)
        {
            ViewModel.ClickThrough = false;
            ViewModel.Status = "復帰用のメニューバーアイコンが利用できないため、クリック透過は有効にできません。";
            return;
        }
        if (!OperatingSystem.IsMacOS()) { if (enabled) ViewModel.ClickThrough = false; return; }
        try { MacWindowInterop.SetClickThrough(this, enabled); }
        catch (Exception ex)
        {
            store.Log(ex);
            if (enabled) ViewModel.ClickThrough = false;
            ViewModel.Status = "クリック透過を変更できませんでした。例外ログを確認してください。";
        }
    }
    private void ApplyBounds(WindowBounds source)
    {
        var bounds = source with { };
        bool previous = applyingBounds; applyingBounds = true;
        try
        {
            Size minimum = appliedFloating ? FloatingLayout.Minimum(ViewModel.Meter) : new Size(420, 300);
            MinWidth = minimum.Width;
            MinHeight = minimum.Height;
            Position = new PixelPoint((int)bounds.Left, (int)bounds.Top);
            if (appliedFloating) SetFloatingSize(bounds.Width);
            else { Width = Math.Max(MinWidth, bounds.Width); Height = Math.Max(MinHeight, bounds.Height); }
        }
        finally { applyingBounds = previous; }
    }
    private Size MaximumClientSize(PixelPoint position, bool fromAnchor = false)
    {
        Screen? screen = Screens.ScreenFromPoint(position) ?? Screens.Primary;
        if (screen is null) return new Size(16384, 16384);
        double scale = screen.Scaling;
        var work = screen.WorkingArea;
        double width = fromAnchor ? work.Right - Math.Max(work.X, position.X) : work.Width;
        double height = fromAnchor ? work.Bottom - Math.Max(work.Y, position.Y) : work.Height;
        return new Size(Math.Max(1, Math.Min(MaxWidth, width / scale)), Math.Max(1, Math.Min(MaxHeight, height / scale)));
    }
    private void SetFloatingSize(double width)
    {
        Size size = FloatingLayout.FromWidth(ViewModel.Meter, width, MaximumClientSize(Position));
        Width = size.Width;
        Height = size.Height;
    }
    private void CaptureBounds()
    {
        if (applyingBounds || !opened || WindowState != WindowState.Normal) return;
        var bounds = new WindowBounds { Left = Position.X, Top = Position.Y, Width = Bounds.Width, Height = Bounds.Height };
        if (appliedFloating) ViewModel.Settings.FloatingBounds = bounds;
        else ViewModel.Settings.NormalBounds = bounds;
    }
    private void ScheduleSave() { if (smokeTest || exiting) return; saveTimer.Stop(); saveTimer.Start(); }
    private void Persist()
    {
        saveTimer.Stop();
        if (smokeTest) return;
        try { ViewModel.Save(); } catch (Exception ex) { store.Log(ex); }
    }
    public void ShowAccessible()
    {
        ViewModel.ClickThrough = false;
        Show(); WindowState = WindowState.Normal;
        MacWindowInterop.EnsureVisible(this); Activate();
    }
    private void HideSafely()
    {
        if (!HasRecoveryIcon) { Exit(); return; }
        FinishInteractions();
        CaptureBounds(); Persist(); Hide();
    }
    public void ReturnToNormal() { ShowAccessible(); ViewModel.Floating = false; }
    private void ReturnToNormal(object? sender, RoutedEventArgs e) => ReturnToNormal();
    public void OpenSettings()
    {
        ShowAccessible();
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow { DataContext = ViewModel };
        settingsWindow.Closed += (_, _) => { settingsWindow = null; Persist(); };
        settingsWindow.Show(this);
    }
    private void OpenPermissions()
    {
        if (smokeTest) return;
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            store.Log(ex);
            ViewModel.Status = "システム設定の『プライバシーとセキュリティ』から収録の許可を確認してください。";
        }
    }
    private void OpenDiagnostics()
    {
        if (interactionLog is null) return;
        try
        {
            string directory = Path.GetDirectoryName(interactionLog.FilePath)!;
            Directory.CreateDirectory(directory);
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory)
                { UseShellExecute = true });
        }
        catch (Exception ex) { store.Log(ex); ViewModel.Status = "操作ログのフォルダーを開けませんでした。"; }
    }
    private void ResizeWindow(object? sender, PointerPressedEventArgs e)
    {
        if (!CanResize || WindowState != WindowState.Normal || ViewModel.ClickThrough ||
            !IsResizeLeftButtonPressed(e)) return;
        FinishInteractions();
        // Read desktop coordinates directly on macOS. A queued event's local
        // coordinates can refer to the content view before its latest resize.
        Point? origin = ReadResizePointer(e);
        if (origin is null) return;
        Size start = ClientSize;
        if (ViewModel.Floating)
        {
            Rect viewport = MeterDisplay.GetFloatingViewport(ViewModel.Meter);
            start = new Size(start.Width, start.Width * viewport.Height / viewport.Width);
        }
        resizeGesture = new WindowResizeGesture(origin.Value, start, DesktopScaling,
            new Size(MinWidth, MinHeight), MaximumClientSize(Position, fromAnchor: true), ViewModel.Floating);
        resizePointer = e.Pointer;
        resizePointer.Capture(ResizeHandle);
        interactionLog?.Begin("resize", ViewModel.Meter.ToString(), ViewModel.Floating, origin, start);
        interactionLog?.Record("press", e.Timestamp, origin, start, pointerCaptured: true);
        e.Handled = true;
    }
    private void ResizePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(resizePointer, e.Pointer)) return;
        if (!IsResizeLeftButtonPressed(e))
        {
            FinishResize("button-released");
            return;
        }
        Point? position = ReadResizePointer(e);
        if (position is null) return;
        Size size = resizeGesture!.GetSize(position.Value);
        interactionLog?.Record("request", e.Timestamp, position, size,
            e.GetCurrentPoint(this).Properties.IsLeftButtonPressed, true, true);
        Width = size.Width;
        Height = size.Height;
        interactionLog?.Record("applied", e.Timestamp);
        e.Handled = true;
    }
    private void ResizePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(resizePointer, e.Pointer) || e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // Finish at the last drag sample. Reinterpreting MouseUp in a newly
        // resized native view was another source of an end-of-drag jump.
        FinishResize("released");
        e.Handled = true;
    }
    private void ResizePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(resizePointer, e.Pointer)) FinishResize("capture-lost");
    }
    protected virtual Point GetResizeDesktopPosition(PointerEventArgs e)
    {
        if (OperatingSystem.IsMacOS() && !smokeTest) return MacWindowInterop.GetPointerPosition();
        PixelPoint point = this.PointToScreen(e.GetPosition(this));
        return new Point(point.X, point.Y);
    }
    protected virtual bool IsResizeLeftButtonPressed(PointerEventArgs e) =>
        OperatingSystem.IsMacOS() && !smokeTest ? MacWindowInterop.IsLeftButtonPressed() :
        e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
    private Point? ReadResizePointer(PointerEventArgs e)
    {
        try { return GetResizeDesktopPosition(e); }
        catch (Exception ex) { store.Log(ex); FinishInteractions(); return null; }
    }
    private void FinishResize(string reason = "interrupted")
    {
        var pointer = resizePointer;
        resizePointer = null;
        resizeGesture = null;
        if (pointer is null) return;
        if (ReferenceEquals(pointer.Captured, ResizeHandle)) pointer.Capture(null);
        interactionLog?.End(reason);
        CaptureBounds();
        ScheduleSave();
    }
    private void ToggleFloating(object? sender, RoutedEventArgs e) => ViewModel.Floating = !ViewModel.Floating;
    private void ToggleTopmost(object? sender, RoutedEventArgs e) => ViewModel.Topmost = !ViewModel.Topmost;
    private void ToggleClickThrough() { if (ViewModel.Floating) ViewModel.ClickThrough = !ViewModel.ClickThrough; }
    private void ToggleClickThrough(object? sender, RoutedEventArgs e) => ToggleClickThrough();
    private void HideWindow(object? sender, RoutedEventArgs e) => HideSafely();
    private void MinimizeWindow(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void ExitApplication(object? sender, RoutedEventArgs e) => Exit();
    public void Exit() => ExitWithCode(0);
    public void PrepareForNativeShutdown()
    {
        // No wait or cancellation inside native termination callbacks. In
        // particular, macOS logout must continue even if consent is pending.
        shutdownRequested = true;
        ViewModel.Dispose();
        PrepareForShutdown();
    }
    public void PrepareForShutdown()
    {
        if (exiting) return;
        FinishInteractions();
        exiting = true;
        CaptureBounds(); Persist(); settingsWindow?.Close();
    }
    public void ExitWithCode(int exitCode)
    {
        if (exiting || shutdownRequested) return;
        shutdownRequested = true;
        // Defer past the native Quit/Closing callback to avoid reentrant closes.
        Dispatcher.UIThread.Post(() => _ = ExitAfterAudioStopsAsync(exitCode));
    }
    private async Task ExitAfterAudioStopsAsync(int exitCode)
    {
        try
        {
            ViewModel.Status = "音声取得を停止して終了しています…";
            await ViewModel.StopAudioAsync();
        }
        catch (Exception ex) { store.Log(ex); }
        finally
        {
            PrepareForShutdown();
            Close();
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(exitCode);
            shutdownCompletion.TrySetResult();
        }
    }
}
