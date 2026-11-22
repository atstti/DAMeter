using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DAMeter.Core;

namespace DAMeter.Mac;

public partial class App : Application
{
    private readonly SettingsStore store = new();
    // Avalonia.Native reads Application.Name after application setup to name
    // the standard macOS application menu and NSProcessInfo process title.
    public App() => Name = "DAMeter";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new MainWindow(store, Program.SmokeTest ? new AppSettings() : store.Load(), Program.SmokeTest);
            desktop.MainWindow = window;
            // A status-bar icon can be hidden by macOS or the menu-bar notch.
            // Dock reopens must recover input without requiring that menu.
            var activation = TryGetFeature(typeof(IActivatableLifetime)) as IActivatableLifetime;
            EventHandler<ActivatedEventArgs> recoverOnActivation = (_, args) =>
            {
                if (args.Kind == ActivationKind.Reopen ||
                    (args.Kind == ActivationKind.Background && window.ViewModel.ClickThrough))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!window.IsShutdownRequested) window.ShowAccessible();
                    });
                }
            };
            if (activation is not null) activation.Activated += recoverOnActivation;
            // macOS Command+Q and Dock Quit first request application shutdown,
            // then close its windows. A tray-style window must not cancel this
            // request as though the user had only closed the meter window.
            desktop.ShutdownRequested += (_, _) =>
            {
                if (window.IsShutdownPrepared) return;
                // Avalonia does not distinguish Dock Quit from logout here.
                // Canceling the request can cancel the entire OS logout, so
                // request nonblocking cleanup and let native termination proceed.
                // Our own Quit menu uses Exit() for awaited audio cleanup.
                window.PrepareForNativeShutdown();
            };
            if (!Program.SmokeTest || OperatingSystem.IsMacOS())
            {
                var menu = new NativeMenu();
                var restore = new NativeMenuItem("DAMeter を表示（クリック透過を解除）")
                    { Gesture = new KeyGesture(Key.M, KeyModifiers.Meta | KeyModifiers.Shift) };
                restore.Click += (_, _) => window.ShowAccessible();
                var settings = new NativeMenuItem("設定…"); settings.Click += (_, _) => window.OpenSettings();
                var quit = new NativeMenuItem("DAMeter を終了") { Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta) };
                quit.Click += (_, _) => window.Exit();
                menu.Add(restore); menu.Add(settings); menu.Add(new NativeMenuItemSeparator()); menu.Add(quit);
                NativeMenu.SetMenu(this, menu);

                // The Dock's right-click menu is independent of the status-bar
                // menu and the application menu. Items must have one parent.
                var dockMenu = new NativeMenu();
                var dockRestore = new NativeMenuItem("DAMeter を表示（クリック透過を解除）");
                dockRestore.Click += (_, _) => window.ShowAccessible();
                var dockNormal = new NativeMenuItem("通常表示に戻す");
                dockNormal.Click += (_, _) => window.ReturnToNormal();
                var dockSettings = new NativeMenuItem("設定…");
                dockSettings.Click += (_, _) => window.OpenSettings();
                var dockQuit = new NativeMenuItem("DAMeter を終了");
                dockQuit.Click += (_, _) => window.Exit();
                dockMenu.Add(dockRestore); dockMenu.Add(dockNormal);
                dockMenu.Add(new NativeMenuItemSeparator()); dockMenu.Add(dockSettings); dockMenu.Add(dockQuit);
                NativeDock.SetMenu(this, dockMenu);
            }
            Dispatcher.UIThread.UnhandledException += (_, args) =>
            {
                store.Log(args.Exception); args.Handled = true; window.ExitWithCode(1);
            };
            desktop.Exit += (_, _) =>
            {
                if (activation is not null) activation.Activated -= recoverOnActivation;
                window.ViewModel.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
