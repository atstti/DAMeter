using Avalonia;

namespace DAMeter.Mac.Platform;

/// <summary>Moves a floating window without entering AppKit's native window-drag session.</summary>
public sealed class WindowMoveGesture(Point pointerOrigin, PixelPoint windowOrigin)
{
    public PixelPoint GetPosition(Point pointer, PixelRect workArea, PixelSize windowSize)
    {
        Vector delta = pointer - pointerOrigin;
        double right = Math.Max(workArea.X, workArea.Right - windowSize.Width);
        double bottom = Math.Max(workArea.Y, workArea.Bottom - windowSize.Height);
        return new PixelPoint(
            (int)Math.Round(Math.Clamp(windowOrigin.X + delta.X, workArea.X, right)),
            (int)Math.Round(Math.Clamp(windowOrigin.Y + delta.Y, workArea.Y, bottom)));
    }
}
