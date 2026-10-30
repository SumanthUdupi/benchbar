using BenchBar.Tray.Core;
using Xunit;

namespace Tray.Core.Tests;

public class DistroGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARunningDistroLetsTheShimBeCalled()
    {
        var probe = new FakeProbe { Running = true };
        Assert.Null(await DistroGuard.CheckAsync(probe, " Ubuntu ", Ct));
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task AStoppedDistroIsRefusedWithTheFlyoutLineFromAFreshProbeNotASnapshot()
    {
        var probe = new FakeProbe { Running = false };
        Assert.Equal(DistroGuard.StoppedMessage, await DistroGuard.CheckAsync(probe, "Ubuntu", Ct));
        Assert.Equal(QuickSwitchModel.DistroStoppedText, DistroGuard.StoppedMessage);
        Assert.Equal(1, probe.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task WithNoDistroToAskAboutNothingIsAsked(string? distro)
    {
        var probe = new FakeProbe { Running = false };
        Assert.Null(await DistroGuard.CheckAsync(probe, distro, Ct));
        Assert.Null(await DistroGuard.CheckAsync(null, "Ubuntu", Ct));
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task AProbeThatFailsIsAProblemLineAndNeverAGoAheadForTheShim()
    {
        var silent = new FakeProbe { Throws = new TimeoutException() };
        Assert.Equal(BenchPoller.WslSilentMessage, await DistroGuard.CheckAsync(silent, "Ubuntu", Ct));

        var missing = new FakeProbe { Throws = new System.ComponentModel.Win32Exception(2) };
        Assert.Equal(BenchPoller.WslMissingMessage, await DistroGuard.CheckAsync(missing, "Ubuntu", Ct));
    }

    [Fact]
    public async Task ACancelledCallerIsNotAProblem()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var probe = new FakeProbe { Throws = new OperationCanceledException(cts.Token) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DistroGuard.CheckAsync(probe, "Ubuntu", cts.Token));
    }
}

public class PanelPlacementTests
{
    [Theory]
    [InlineData(460.0, 96u, 460)]
    [InlineData(460.0, 144u, 690)]
    [InlineData(460.0, 192u, 920)]
    [InlineData(460.0, 0u, 460)]
    public void DipsBecomePixelsAtTheMonitorsOwnDpi(double dip, uint dpi, int pixels) =>
        Assert.Equal(pixels, PanelPlacement.ToPixels(dip, dpi));

    [Fact]
    public void ThePanelIsCentredInThePixelsOfTheMonitorItIsOn()
    {
        // a 4K monitor at 200% to the right of a 1080p one: the work area is in pixels, so is the result
        var (x, y) = PanelPlacement.Centre(1920, 0, 5760, 2120, PanelPlacement.ToPixels(460, 192), PanelPlacement.ToPixels(300, 192));
        Assert.Equal(1920 + (3840 - 920) / 2, x);
        Assert.Equal((2120 - 600) / 2, y);
    }

    [Fact]
    public void APanelLargerThanTheWorkAreaStaysAtItsTopLeft()
    {
        Assert.Equal((100, 50), PanelPlacement.Centre(100, 50, 400, 300, 900, 900));
    }
}
