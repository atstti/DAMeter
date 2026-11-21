using Avalonia;
using DAMeter.Core;
using DAMeter.Mac.Controls;

namespace DAMeter.Mac.Platform;

/// <summary>One scale for the whole floating meter; no transparent letterboxing.</summary>
public static class FloatingLayout
{
    public static Size Minimum(MeterMode mode)
    {
        Rect viewport = MeterDisplay.GetFloatingViewport(mode);
        double scale = Math.Max(120 / viewport.Width, 40 / viewport.Height);
        return new Size(viewport.Width * scale, viewport.Height * scale);
    }

    public static Size FromWidth(MeterMode mode, double width, Size maximum)
    {
        Rect viewport = MeterDisplay.GetFloatingViewport(mode);
        Size minimum = Minimum(mode);
        double maxWidth = Math.Min(maximum.Width, maximum.Height * viewport.Width / viewport.Height);
        maxWidth = Math.Max(minimum.Width, maxWidth);
        double fittedWidth = Math.Clamp(width, minimum.Width, maxWidth);
        return new Size(fittedWidth, fittedWidth * viewport.Height / viewport.Width);
    }

    public static WindowBounds Migrate(WindowBounds old, MeterMode mode, double desktopScaling)
    {
        Size canvas = MeterDisplay.GetCanvasSize(mode);
        Rect viewport = MeterDisplay.GetFloatingViewport(mode);
        double scale = Math.Min(old.Width / canvas.Width, old.Height / canvas.Height);
        return new WindowBounds
        {
            Left = old.Left + ((old.Width - canvas.Width * scale) / 2 + viewport.X * scale) * desktopScaling,
            Top = old.Top + ((old.Height - canvas.Height * scale) / 2 + viewport.Y * scale) * desktopScaling,
            Width = viewport.Width * scale,
            Height = viewport.Height * scale
        };
    }
}

/// <summary>Immutable desktop anchor, unaffected by window movement or layout callbacks.</summary>
public sealed class WindowResizeGesture(Point origin, Size start, double desktopScaling, Size minimum,
    Size maximum, bool preserveAspect)
{
    public Size GetSize(Point desktopPosition)
    {
        Vector delta = (desktopPosition - origin) / desktopScaling;
        double width = start.Width + delta.X;
        double height = start.Height + delta.Y;
        if (preserveAspect)
        {
            double ratio = start.Width / start.Height;
            // Project the pointer onto the meter's diagonal. Either axis can
            // resize it without growing an invisible area around the drawing.
            width = start.Width + (delta.X * ratio * ratio + delta.Y * ratio) / (ratio * ratio + 1);
            double lower = Math.Max(minimum.Width, minimum.Height * ratio);
            double upper = Math.Max(lower, Math.Min(maximum.Width, maximum.Height * ratio));
            width = Math.Clamp(width, lower, upper);
            height = width / ratio;
        }
        else
        {
            width = Math.Clamp(width, minimum.Width, Math.Max(minimum.Width, maximum.Width));
            height = Math.Clamp(height, minimum.Height, Math.Max(minimum.Height, maximum.Height));
        }
        return new Size(width, height);
    }
}
