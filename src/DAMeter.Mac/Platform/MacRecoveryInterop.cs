using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace DAMeter.Mac.Platform;

/// <summary>Native click-through and Dock recovery checks without global input.</summary>
public static class MacRecoveryInterop
{
    public static bool IsClickThrough(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        VerifyAccess();
        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == 0 || handle.HandleDescriptor != "NSWindow")
            throw new NotSupportedException("A valid native NSWindow is required to inspect click-through.");
        nint selector = RequireSelector(handle.Handle, "ignoresMouseEvents");
        return ObjC.GetBoolean(handle.Handle, selector) != 0;
    }

    public static void RequestDockReopen()
    {
        var (app, appDelegate) = GetApplicationDelegate();
        nint selector = RequireSelector(appDelegate, "applicationShouldHandleReopen:hasVisibleWindows:");
        // Use true: recovery must also work while the floating meter is visible.
        if (ObjC.RequestReopen(appDelegate, selector, app, 1) == 0)
            throw new InvalidOperationException("The application delegate rejected the Dock reopen request.");
    }

    public static void InvokeDockRestore()
    {
        var (app, appDelegate) = GetApplicationDelegate();
        nint selector = RequireSelector(appDelegate, "applicationDockMenu:");
        nint menu = ObjC.GetObjectWithArgument(appDelegate, selector, app);
        if (menu == 0)
            throw new InvalidOperationException("The application delegate did not expose a native Dock recovery menu.");
        if (ObjC.GetInteger(menu, RequireSelector(menu, "numberOfItems")) < 1)
            throw new InvalidOperationException("The native Dock recovery menu is empty.");
        ObjC.PerformAction(menu, RequireSelector(menu, "performActionForItemAtIndex:"), 0);
    }

    private static (nint App, nint Delegate) GetApplicationDelegate()
    {
        VerifyAccess();
        nint type = ObjC.GetClass("NSApplication");
        if (type == 0) throw new NotSupportedException("NSApplication is unavailable.");
        nint app = ObjC.GetObject(type, RequireSelector(type, "sharedApplication"));
        if (app == 0) throw new InvalidOperationException("The native NSApplication is unavailable.");
        nint appDelegate = ObjC.GetObject(app, RequireSelector(app, "delegate"));
        if (appDelegate == 0) throw new InvalidOperationException("The native application delegate is unavailable.");
        return (app, appDelegate);
    }

    private static void VerifyAccess()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Dock recovery checks require macOS.");
        Dispatcher.UIThread.VerifyAccess();
    }

    private static nint RequireSelector(nint target, string name)
    {
        nint selector = ObjC.Selector(name);
        if (ObjC.RespondsToSelector(target, ObjC.Selector("respondsToSelector:"), selector) == 0)
            throw new NotSupportedException($"The native recovery target does not support {name}.");
        return selector;
    }

    private static class ObjC
    {
        private const string Library = "/usr/lib/libobjc.A.dylib";
        [DllImport(Library, EntryPoint = "objc_getClass")]
        public static extern nint GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Library, EntryPoint = "sel_registerName")]
        public static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern byte RespondsToSelector(nint target, nint selector, nint candidate);
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern nint GetObject(nint target, nint selector);
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern nint GetObjectWithArgument(nint target, nint selector, nint argument);
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern nint GetInteger(nint target, nint selector);
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern byte GetBoolean(nint target, nint selector);
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern byte RequestReopen(nint target, nint selector, nint sender, byte hasVisibleWindows);
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern void PerformAction(nint target, nint selector, nint itemIndex);
    }
}
