using Avalonia;
using DAMeter.Mac.Platform;
using Xunit;

namespace DAMeter.Mac.Tests;

public sealed class WindowResizeGestureTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DesktopDeltaUsesDesktopScalingRatherThanWindowOrRenderCoordinates(double scaling)
    {
        var gesture = new WindowResizeGesture(new Point(-500, -100), new Size(400, 200), scaling,
            new Size(120, 40), new Size(1600, 900), true);
        Assert.Equal(new Size(500, 250), gesture.GetSize(new Point(-500 + 100 * scaling, -100 + 50 * scaling)));
        Assert.Equal(new Size(400, 200), gesture.GetSize(new Point(-500, -100)));
    }
    [Fact]
    public void EitherAxisResizesUniformlyAndHugeDeltasAreBoundedByTheWorkingArea()
    {
        var gesture = new WindowResizeGesture(new Point(10, 10), new Size(400, 200), 1,
            new Size(120, 40), new Size(800, 600), true);
        Assert.Equal(new Size(480, 240), gesture.GetSize(new Point(110, 10)));
        Assert.Equal(new Size(420, 210), gesture.GetSize(new Point(10, 60)));
        Assert.Equal(new Size(800, 400), gesture.GetSize(new Point(1e6, 1e6)));
        Assert.Equal(new Size(120, 60), gesture.GetSize(new Point(-1e6, -1e6)));
        Assert.Equal(new Size(400, 200), gesture.GetSize(new Point(10, 10)));
    }
    [Fact]
    public void NormalWindowsCanStillResizeBothDimensionsIndependently()
    {
        var gesture = new WindowResizeGesture(default, new Size(700, 450), 1,
            new Size(420, 300), new Size(1600, 900), false);
        Assert.Equal(new Size(750, 430), gesture.GetSize(new Point(50, -20)));
    }
}
