using System.Windows.Input;
using BenchBar.Tray.Core;
using Xunit;

namespace BenchBar.Tray.Tests;

public class QuickSwitchWindowTests
{
    private static readonly ResolvedTheme Theme = new(true, true);

    private static QuickSwitchData Data(string[] pins, string? previous = null, string? current = null) => new(
        _ => [.. pins.Select(p => new PinnedUser(p, null))],
        _ => current,
        _ => previous,
        (_, user) => user,
        (_, _) => null);

    private static QuickSwitchTarget Ready(params string[] sites) =>
        new(QuickSwitchState.Ready, new BenchStatus("b", "/b", BenchState.Running, null, null, sites[0]), sites, sites[0]);

    private static QuickSwitchWindow Window(QuickSwitchTarget target, QuickSwitchData data, QuickSwitchActions? actions = null) =>
        new(target, data, actions ?? new QuickSwitchActions(), Theme);

    [Fact]
    public void ItBuildsItsRowsFromTheModel() => Sta.Run(() =>
    {
        var window = Window(Ready("a.localhost"), Data(["ne@x.com", "pm@x.com"], previous: "zed@x.com", current: "pm@x.com"));
        Assert.Equal("Open a.localhost as…", window.TitleText);
        Assert.Equal(["Switch Back to zed@x.com", "ne@x.com", "pm@x.com", "Administrator"], window.RowTexts);
        Assert.Equal(0, window.SelectedIndex);
        Assert.True(window.SearchBoxVisible);
        Assert.Equal("", window.MessageText);
    });

    [Fact]
    public void UpAndDownMoveTheSelectionAndWrap() => Sta.Run(() =>
    {
        var window = Window(Ready("a.localhost"), Data(["ne@x.com"]));
        Assert.True(window.HandleKey(Key.Up, shift: false));
        Assert.Equal(1, window.SelectedIndex);
        Assert.True(window.HandleKey(Key.Down, shift: false));
        Assert.Equal(0, window.SelectedIndex);
    });

    [Fact]
    public void EnterSwitchesOnceEvenWhenPressedTwice() => Sta.Run(() =>
    {
        var calls = new List<(string Site, string User)>();
        var release = new TaskCompletionSource<string?>();
        var actions = new QuickSwitchActions { Switch = (site, user, _) => { calls.Add((site, user)); return release.Task; } };
        var window = Window(Ready("a.localhost"), Data(["ne@x.com"], previous: "zed@x.com"), actions);

        window.HandleKey(Key.Enter, shift: false);
        window.HandleKey(Key.Enter, shift: false);
        Assert.Equal([("a.localhost", "zed@x.com")], calls);
    });

    [Fact]
    public void ADigitSwitchesAtOnceWhileTheBoxIsEmpty() => Sta.Run(() =>
    {
        var calls = new List<string>();
        var actions = new QuickSwitchActions { Switch = (_, user, _) => { calls.Add(user); return Task.FromResult<string?>(null); } };
        var window = Window(Ready("a.localhost"), Data(["ne@x.com", "pm@x.com"]), actions);
        Assert.True(window.HandleKey(Key.D2, shift: false));
        Assert.Equal(["pm@x.com"], calls);
    });

    [Fact]
    public void DigitsKeepTheirPinsWhenSwitchBackShowsAndAdministratorIsZero() => Sta.Run(() =>
    {
        var calls = new List<string>();
        var actions = new QuickSwitchActions { Switch = (_, user, _) => { calls.Add(user); return new TaskCompletionSource<string?>().Task; } };
        var window = Window(Ready("a.localhost"), Data(["ne@x.com", "pm@x.com"], previous: "zed@x.com"), actions);

        Assert.True(window.HandleKey(Key.D1, shift: false)); // the first pin, not Switch Back
        Assert.Equal(["ne@x.com"], calls);
    });

    [Fact]
    public void ZeroSwitchesToAdministrator() => Sta.Run(() =>
    {
        var calls = new List<string>();
        var actions = new QuickSwitchActions { Switch = (_, user, _) => { calls.Add(user); return new TaskCompletionSource<string?>().Task; } };
        var window = Window(Ready("a.localhost"), Data(["ne@x.com"], previous: "zed@x.com"), actions);
        Assert.True(window.HandleKey(Key.D0, shift: false));
        Assert.Equal(["Administrator"], calls);
    });

    [Fact]
    public void ABlankBoxStillTakesDigitsAndARealQueryDoesNot() => Sta.Run(() =>
    {
        var calls = new List<string>();
        var actions = new QuickSwitchActions { Switch = (_, user, _) => { calls.Add(user); return new TaskCompletionSource<string?>().Task; } };
        var window = Window(Ready("a.localhost"), Data(["ne@x.com"]), actions);

        window.SetSearchText("ne");
        Assert.False(window.HandleKey(Key.D1, shift: false));
        Assert.Empty(calls);

        window.SetSearchText("   ");
        Assert.True(window.HandleKey(Key.D1, shift: false));
        Assert.Equal(["ne@x.com"], calls);
    });

    [Fact]
    public void ALetterTypedWhileAltIsStillDownLandsInTheBox() => Sta.Run(() =>
    {
        var window = Window(Ready("a.localhost"), Data(["ne@x.com"]));
        Assert.True(window.HandleAltLetter(Key.N, shift: false));
        Assert.True(window.HandleAltLetter(Key.E, shift: true));
        Assert.Equal("nE", window.SearchText);
        Assert.False(window.HandleAltLetter(Key.F5, shift: false));
        Assert.False(window.HandleAltLetter(Key.D1, shift: false));
        Assert.Equal('q', QuickSwitchWindow.LetterOf(Key.Q, false));
        Assert.Equal('Z', QuickSwitchWindow.LetterOf(Key.Z, true));
        Assert.Null(QuickSwitchWindow.LetterOf(Key.Space, false));
    });

    [Fact]
    public void AFailedSwitchWhileThePanelIsNotInFrontIsReportedNotDropped() => Sta.Run(() =>
    {
        var reported = new List<string>();
        var actions = new QuickSwitchActions
        {
            Switch = (_, _, _) => Task.FromResult<string?>("ne@x.com is disabled on a.localhost."),
            ReportFailure = reported.Add,
        };
        var window = Window(Ready("a.localhost"), Data(["ne@x.com"]), actions); // never shown, so never active
        window.HandleKey(Key.Enter, shift: false);
        Assert.Equal(["ne@x.com is disabled on a.localhost."], reported);
    });

    [Fact]
    public void ClosingThePanelMidSwitchCancelsTheSwitch() => Sta.Run(() =>
    {
        CancellationToken seen = default;
        var actions = new QuickSwitchActions { Switch = (_, _, ct) => { seen = ct; return new TaskCompletionSource<string?>().Task; } };
        var window = Window(Ready("a.localhost"), Data(["ne@x.com"]), actions);

        window.HandleKey(Key.Enter, shift: false);
        Assert.False(seen.IsCancellationRequested);
        Assert.True(window.HandleKey(Key.Escape, shift: false));
        Assert.True(seen.IsCancellationRequested);
    });

    [Fact]
    public void EveryStateOfTheTargetShowsItsOwnLineAndNoSearchBox() => Sta.Run(() =>
    {
        var cases = new (TraySnapshot Snapshot, string Text)[]
        {
            (TraySnapshot.Empty, "Loading…"),
            (new TraySnapshot([], Problem: "benchbar.exe was not found."), "benchbar.exe was not found."),
            (new TraySnapshot([new BenchStatus("b", "/b", BenchState.Starting, null, null, "a") { Sites = [new SiteInfo("a", true, true)] }]), "b is starting."),
        };
        foreach (var (snapshot, text) in cases)
        {
            var window = Window(QuickSwitchModel.Choose(snapshot, null), QuickSwitchData.Empty);
            Assert.Equal(text, window.MessageText);
            Assert.False(window.SearchBoxVisible);
        }
    });

    [Fact]
    public void AShiftedDigitIsLeftToTheTextBox() => Sta.Run(() =>
    {
        var window = Window(Ready("a.localhost"), Data(["ne@x.com"]));
        Assert.False(window.HandleKey(Key.D1, shift: true));
    });

    [Fact]
    public void TheNumPadCountsAsDigits()
    {
        Assert.Equal(0, QuickSwitchWindow.DigitOf(Key.D0));
        Assert.Equal(7, QuickSwitchWindow.DigitOf(Key.NumPad7));
        Assert.Null(QuickSwitchWindow.DigitOf(Key.A));
    }

    [Fact]
    public void TabMovesToTheNextSiteAndTheTitleFollows() => Sta.Run(() =>
    {
        var window = Window(Ready("a.localhost", "b.localhost"), Data([]));
        window.HandleKey(Key.Tab, shift: false);
        Assert.Equal("Open b.localhost as…", window.TitleText);
    });

    [Fact]
    public void ASearchAddsOtherUsersAndAskedForTheTypedSiteAndText() => Sta.Run(() =>
    {
        string? askedSite = null, askedText = null;
        var actions = new QuickSwitchActions
        {
            Find = (site, text, _) =>
            {
                (askedSite, askedText) = (site, text);
                return Task.FromResult<(UserList?, string?)>((new UserList(site, [new SiteUser("kim@x.com", "Kim Lee", "System User", [], 0, null)], false), null));
            },
        };
        var window = Window(Ready("a.localhost"), Data(["ne@x.com"]), actions);

        // the box is the model's input; the window reads it through TextChanged, so drive the same path
        window.SetSearchText("kim");
        window.SearchAsync().GetAwaiter().GetResult();

        Assert.Equal("a.localhost", askedSite);
        Assert.Equal("kim", askedText);
        Assert.Equal(["Kim Lee — kim@x.com"], window.RowTexts);
    });

    [Fact]
    public void ASearchThatFailsShowsTheErrorInline() => Sta.Run(() =>
    {
        var actions = new QuickSwitchActions
        {
            Find = (_, _, _) => Task.FromResult<(UserList?, string?)>((null, "The bench is not running.")),
        };
        var window = Window(Ready("a.localhost"), Data([]), actions);
        window.SetSearchText("kim");
        window.SearchAsync().GetAwaiter().GetResult();
        Assert.Equal("The bench is not running.", window.MessageText);
        Assert.True(window.MessageIsError);
    });

    [Fact]
    public void AStoppedDistroShowsItsMessageWithNoRowsAndNoSearchBox() => Sta.Run(() =>
    {
        var target = QuickSwitchModel.Choose(new TraySnapshot([], DistroStopped: true, Distro: "Ubuntu"), null);
        var window = Window(target, QuickSwitchData.Empty);
        Assert.StartsWith("The WSL distro is stopped", window.MessageText);
        Assert.Empty(window.RowTexts);
        Assert.False(window.SearchBoxVisible);
        Assert.False(window.HandleKey(Key.Enter, shift: false)); // nothing to switch, nothing called
    });
}
