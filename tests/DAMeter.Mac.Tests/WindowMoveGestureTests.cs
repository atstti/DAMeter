using Avalonia;
using DAMeter.Mac.Platform;
using Xunit;

namespace DAMeter.Mac.Tests;

public sealed class WindowMoveGestureTests
{
    [Fact]
    public void DraggingAboveTheMenuBarReachesTheWorkingAreaTop()
    {
        var gesture = new WindowMoveGesture(new Point(350, 440), new PixelPoint(300, 400));
        Assert.Equal(new PixelPoint(300, 25), gesture.GetPosition(new Point(350, -200),
            [new PixelRect(0, 25, 1440, 855)], new PixelSize(500, 140)));
    }

    [Fact]
    public void ReturningAfterAClampedMoveStillUsesTheOriginalPointerAnchor()
    {
        var gesture = new WindowMoveGesture(new Point(350, 440), new PixelPoint(300, 400));
        var work = new PixelRect(0, 25, 1440, 855);
        var size = new PixelSize(500, 140);
        gesture.GetPosition(new Point(-500, -500), [work], size);
        Assert.Equal(new PixelPoint(340, 450), gesture.GetPosition(new Point(390, 490), [work], size));
    }

    [Fact]
    public void ASecondaryScreenWithNegativeCoordinatesRemainsReachable()
    {
        var gesture = new WindowMoveGesture(new Point(350, 440), new PixelPoint(300, 400));
        Assert.Equal(new PixelPoint(-900, -800), gesture.GetPosition(new Point(-850, -760),
            [new PixelRect(-1440, -900, 1440, 900)], new PixelSize(500, 140)));
    }

    [Fact]
    public void CrossingTheBoundaryBetweenMonitorsPreservesTheGrabOffset()
    {
        var gesture = new WindowMoveGesture(new Point(1300, 400), new PixelPoint(1050, 350));
        PixelRect[] screens = [new(0, 25, 1440, 855), new(1440, 25, 1440, 855)];
        var size = new PixelSize(500, 140);
        PixelPoint before = gesture.GetPosition(new Point(1439, 400), screens, size);
        PixelPoint after = gesture.GetPosition(new Point(1440, 400), screens, size);
        Assert.Equal(1189, before.X);
        Assert.Equal(before.X + 1, after.X);
        Assert.Equal(before.Y, after.Y);
    }
}
