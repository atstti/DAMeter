using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using DAMeter.Mac.Platform;

namespace DAMeter.Mac;

public partial class MainWindow
{
    private IPointer? movePointer;
    private WindowMoveGesture? moveGesture;

    private void DragWindow(object? sender, PointerPressedEventArgs e)
    {
        if (resizePointer is not null || ViewModel.ClickThrough || !IsResizeLeftButtonPressed(e)) return;
        if (!ViewModel.Floating)
        {
            BeginMoveDrag(e);
            return;
        }
        // Floating meters are utility windows. Keep movement in the same
        // desktop coordinate system as resizing instead of entering AppKit's
        // separate window-drag/tiling session.
        FinishInteractions();
        Point? origin = ReadResizePointer(e);
        if (origin is null) return;
        moveGesture = new WindowMoveGesture(origin.Value, Position);
        movePointer = e.Pointer;
        movePointer.Capture(Meter);
        interactionLog?.Begin("move", ViewModel.Meter.ToString(), true, origin, ClientSize);
        interactionLog?.Record("press", e.Timestamp, origin, pointerCaptured: true);
        e.Handled = true;
    }

    private void MovePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(movePointer, e.Pointer)) return;
        if (!IsResizeLeftButtonPressed(e)) { FinishMove("button-released"); return; }
        Point? desktop = ReadResizePointer(e);
        if (desktop is null) return;
        var pointer = new PixelPoint((int)Math.Round(desktop.Value.X), (int)Math.Round(desktop.Value.Y));
        var screen = Screens.ScreenFromPoint(pointer) ?? Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        Position = moveGesture!.GetPosition(desktop.Value, screen.WorkingArea,
            PixelSize.FromSize(ClientSize, DesktopScaling));
        interactionLog?.Record("move", e.Timestamp, desktop, rawLeftPressed: e.GetCurrentPoint(this).Properties.IsLeftButtonPressed,
            physicalLeftPressed: true, pointerCaptured: true);
        e.Handled = true;
    }

    private void MovePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(movePointer, e.Pointer) || e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        FinishMove("released");
        e.Handled = true;
    }

    private void MovePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(movePointer, e.Pointer)) FinishMove("capture-lost");
    }

    private void FinishMove(string reason = "interrupted")
    {
        var pointer = movePointer;
        movePointer = null;
        moveGesture = null;
        if (pointer is null) return;
        if (ReferenceEquals(pointer.Captured, Meter)) pointer.Capture(null);
        interactionLog?.End(reason);
        CaptureBounds();
        ScheduleSave();
    }

    private void FinishInteractions()
    {
        FinishMove();
        FinishResize();
    }
}
