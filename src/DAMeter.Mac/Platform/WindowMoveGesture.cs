using Avalonia;

namespace DAMeter.Mac.Platform;

/// <summary>Moves a floating window without entering AppKit's native window-drag session.</summary>
public sealed class WindowMoveGesture(Point pointerOrigin, PixelPoint windowOrigin)
{
    public PixelPoint GetPosition(Point pointer, IEnumerable<PixelRect> workAreas, PixelSize windowSize)
    {
        Vector delta = pointer - pointerOrigin;
        // Allow straddling connected screens. Constraining to the screen under
        // the pointer makes a whole window jump when the pointer crosses an edge.
        PixelRect desktop = workAreas.Where(area => area.Width > 0 && area.Height > 0)
            .DefaultIfEmpty(new PixelRect(windowOrigin, windowSize)).Aggregate((a, b) => a.Union(b));
        double right = Math.Max(desktop.X, desktop.Right - windowSize.Width);
        double bottom = Math.Max(desktop.Y, desktop.Bottom - windowSize.Height);
        return new PixelPoint(
            (int)Math.Round(Math.Clamp(windowOrigin.X + delta.X, desktop.X, right)),
            (int)Math.Round(Math.Clamp(windowOrigin.Y + delta.Y, desktop.Y, bottom)));
    }
}
