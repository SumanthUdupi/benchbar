using System.Windows;
using System.Windows.Controls;
using BenchBar.Tray.Core;
using Xunit;

namespace BenchBar.Tray.Tests;

public class FlyoutViewTests
{
    private static string FixtureDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "fixtures", "recorded-v1");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("fixtures/recorded-v1 not found above " + AppContext.BaseDirectory);
    }

    private static BenchStatus Status(string name, string state, string? reason = null, string? site = "x.localhost")
    {
        static string Q(string? s) => s is null ? "null" : "\"" + s + "\"";
        return BenchJson.ParseStatus(
            "{\"bench\":\"/home/a/" + name + "\",\"name\":\"" + name + "\",\"site\":" + Q(site) + ",\"state\":\"" + state + "\"," +
            "\"stop_reason\":" + Q(reason) + ",\"web_url\":" + Q(site is null ? null : "http://" + site + ":8000") + "}");
    }

    /// <summary>The recorded running bench read through the fixture source, a paused port conflict and a not adopted one.</summary>
    private static TraySnapshot ThreeBenches()
    {
        var source = new FixtureBenchSource(FixtureDir());
        var entry = source.ListAsync(CancellationToken.None).GetAwaiter().GetResult().Single();
        var running = source.StatusAsync(entry, CancellationToken.None).GetAwaiter().GetResult();
        return new TraySnapshot([running, Status("v16-bench", "paused", "port_conflict", "v16dev.localhost"), Status("new-bench", "not_adopted", null, site: null)]);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Fact]
    public void ARowPerBenchWithNameStateWordSiteAndButtons() => Sta.Run(() =>
    {
        var view = new FlyoutView(new FlyoutActions());
        view.SetSnapshot(ThreeBenches(), "1 of 3 up");

        Assert.Equal(FlyoutMode.Benches, view.Mode);
        Assert.Equal("1 of 3 up", view.UpText);
        Assert.Equal(["frappe-bench", "v16-bench", "new-bench"], view.Rows.Select(r => r.NameText));
        Assert.Equal(["running", "port conflict", "unknown (not_adopted)"], view.Rows.Select(r => r.StateText));
        Assert.Equal(["linuxdev.localhost", "v16dev.localhost", "no site"], view.Rows.Select(r => r.SiteText));

        string[] strip = ["⋯", "Open site", "Open as…", "Logs", "VS Code", "Doctor"];
        Assert.Equal(["Stop", .. strip], view.Rows[0].ButtonLabels);
        Assert.Equal(["Start", .. strip], view.Rows[1].ButtonLabels);
        Assert.Equal(["Start", .. strip], view.Rows[2].ButtonLabels);
        Assert.True(view.Rows[0].ActionButton.IsEnabled);
        Assert.False(view.Rows[2].ActionButton.IsEnabled); // not adopted: nothing to start
        Assert.False(view.Rows[2].Buttons[2].IsEnabled); // no site, no Open site
    });

    [Fact]
    public void ThePolledDefaultSiteWinsOverTheFirstSite() => Sta.Run(() =>
    {
        var bench = BenchJson.ParseStatus("""{"bench":"/b","name":"b","site":"a.localhost","state":"running","sites":[{"name":"a.localhost","default":false},{"name":"z.localhost","default":true}]}""");
        var view = new FlyoutView(new FlyoutActions());
        view.SetSnapshot(new TraySnapshot([bench]), "1 of 1 up");
        Assert.Equal("z.localhost", view.Rows[0].SiteText);
    });

    [Fact]
    public void ADistroThatIsStoppedShowsOneCardWithAStartButton() => Sta.Run(() =>
    {
        var started = 0;
        var view = new FlyoutView(new FlyoutActions { StartDistro = () => started++ });
        view.SetSnapshot(new TraySnapshot([], DistroStopped: true, Distro: "Ubuntu-24.04"), "distro stopped");

        Assert.Equal(FlyoutMode.DistroStopped, view.Mode);
        Assert.Empty(view.Rows);
        Assert.Equal("Ubuntu-24.04 is stopped", view.CardTitle);
        Assert.Equal("The tray does not start it on its own.", view.CardHint);
        Assert.Equal(["Start"], view.CardButtons);

        Click(view.CardButton!);
        Assert.Equal(1, started);
        Assert.False(view.CardButton!.IsEnabled); // asked once; the poll that finds it up replaces the card
    });

    [Fact]
    public void AProblemShowsItsLineInACardWithCopy() => Sta.Run(() =>
    {
        string? copied = null;
        var view = new FlyoutView(new FlyoutActions { CopyText = t => copied = t });
        view.SetSnapshot(new TraySnapshot([], Problem: ShimBenchSource.NotFoundMessage), "needs attention");

        Assert.Equal(FlyoutMode.Problem, view.Mode);
        Assert.Equal(ShimBenchSource.NotFoundMessage, view.CardTitle);
        Assert.Equal(["Copy"], view.CardButtons);
        Click(view.CardButton!);
        Assert.Equal(ShimBenchSource.NotFoundMessage, copied);
    });

    [Fact]
    public void NoBenchesSaysSo() => Sta.Run(() =>
    {
        var view = new FlyoutView(new FlyoutActions());
        view.SetSnapshot(new TraySnapshot([]), "none up");
        Assert.Equal(FlyoutMode.Empty, view.Mode);
        Assert.Empty(view.Rows);
    });

    [Fact]
    public void AFailedStartShowsItsLineUnderTheRowUntilTheStateChanges() => Sta.Run(() =>
    {
        var stopped = Status("b", "stopped", "manual");
        var view = new FlyoutView(new FlyoutActions
        {
            StartStop = _ => Task.FromResult(new ActionResult(1, "Port 8000 is held by another bench.")),
        });
        view.SetSnapshot(new TraySnapshot([stopped]), "none up");
        Assert.Null(view.Rows[0].FailureText);

        Click(view.Rows[0].ActionButton);
        Assert.Equal("Port 8000 is held by another bench.", view.Rows[0].FailureText);
        Assert.True(view.Rows[0].ActionButton.IsEnabled); // can try again

        view.SetSnapshot(new TraySnapshot([Status("b", "stopped", "manual")]), "none up"); // a poll that changed nothing
        Assert.Equal("Port 8000 is held by another bench.", view.Rows[0].FailureText);

        view.SetSnapshot(new TraySnapshot([Status("b", "starting")]), "1 of 1 up"); // the state changed
        Assert.Null(view.Rows[0].FailureText);
    });

    [Fact]
    public void AnActionThatThrowsIsAFailureLineToo() => Sta.Run(() =>
    {
        var view = new FlyoutView(new FlyoutActions { StartStop = _ => throw new InvalidOperationException("boom") });
        view.SetSnapshot(new TraySnapshot([Status("b", "stopped")]), "none up");
        Click(view.Rows[0].ActionButton);
        Assert.Equal("boom", view.Rows[0].FailureText);
    });

    [Fact]
    public void VsCodeNotFoundShowsTheInstallMessageInTheFlyout() => Sta.Run(() =>
    {
        var view = new FlyoutView(new FlyoutActions { OpenVsCode = _ => VsCodeOutcome.NotFound, VsCodeAvailable = () => false });
        view.SetSnapshot(new TraySnapshot([Status("b", "running")]), "1 of 1 up");
        var code = view.Rows[0].Buttons.Single(b => b.Content as string == "VS Code");
        Assert.Equal(VsCode.InstallMessage, code.ToolTip);
        Assert.Equal("", view.MessageText);

        Click(code);
        Assert.Equal("Install VS Code and its WSL extension (ms-vscode-remote.remote-wsl), then try again.", view.MessageText);
        view.ClearMessage();
        Assert.Equal("", view.MessageText);
    });

    [Fact]
    public void TheStripButtonsCallTheirActions() => Sta.Run(() =>
    {
        var calls = new List<string>();
        var view = new FlyoutView(new FlyoutActions
        {
            OpenSite = b => calls.Add("site " + b.Name),
            ShowLogs = b => calls.Add("logs " + b.Name),
            ShowDoctor = b => calls.Add("doctor " + b.Name),
            OpenVsCode = b => { calls.Add("code " + b.Name); return VsCodeOutcome.Launched; },
        });
        view.SetSnapshot(new TraySnapshot([Status("b", "running")]), "1 of 1 up");
        foreach (var label in new[] { "Open site", "Logs", "VS Code", "Doctor" })
            Click(view.Rows[0].Buttons.Single(b => b.Content as string == label));
        Assert.Equal(["site b", "logs b", "code b", "doctor b"], calls);
    });

    [Fact]
    public void APollThatChangedNothingKeepsTheSameRows() => Sta.Run(() =>
    {
        var view = new FlyoutView(new FlyoutActions());
        view.SetSnapshot(new TraySnapshot([Status("b", "running")]), "1 of 1 up");
        var first = view.Rows[0];
        view.SetSnapshot(new TraySnapshot([Status("b", "running")]), "1 of 1 up");
        Assert.Same(first, view.Rows[0]);
        view.SetSnapshot(new TraySnapshot([Status("b", "stopped")]), "none up");
        Assert.NotSame(first, view.Rows[0]);
    });

    private static BenchStatus Dev(string name, string state, params (string Site, bool DeveloperMode)[] sites)
    {
        var list = string.Join(",", sites.Select((s, i) =>
            "{\"name\":\"" + s.Site + "\",\"default\":" + (i == 0 ? "true" : "false") + ",\"developer_mode\":" + (s.DeveloperMode ? "true" : "false") + "}"));
        return BenchJson.ParseStatus(
            "{\"bench\":\"/home/a/" + name + "\",\"name\":\"" + name + "\",\"site\":\"" + sites[0].Site + "\",\"state\":\"" + state + "\"," +
            "\"web_url\":\"http://" + sites[0].Site + ":8000\",\"sites\":[" + list + "]}");
    }

    private static Button OpenAsButton(BenchRow row) => row.Buttons.Single(b => b.Content as string == "Open as…");

    private static IReadOnlyList<string> Headers(ItemsControl menu) =>
        menu.Items.Cast<object>().Select(i => i is MenuItem m ? (string)m.Header : "-").ToList();

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    [Fact]
    public void OpenAsIsEnabledOnlyForARunningBenchWithADeveloperModeSite() => Sta.Run(() =>
    {
        var view = new FlyoutView(new FlyoutActions());
        view.SetSnapshot(new TraySnapshot([
            Dev("running-dev", "running", ("a.localhost", true)),
            Dev("stopped-dev", "stopped", ("a.localhost", true)),
            Dev("running-prod", "running", ("a.localhost", false)),
            Dev("dev-elsewhere", "running", ("a.localhost", false), ("b.localhost", true)),
            Status("old-cli", "running"),
        ]), "1 of 5 up");

        Assert.Equal([true, false, false, false, false], view.Rows.Select(r => OpenAsButton(r).IsEnabled));
        Assert.Equal("Start the bench to open a site as another user", OpenAsButton(view.Rows[1]).ToolTip);
        Assert.Equal("No site of this bench is in developer mode", OpenAsButton(view.Rows[2]).ToolTip);
    });

    [Fact]
    public void OpenAsNeedsTheDefaultSiteInDeveloperModeAndTheTooltipSaysWhy() => Sta.Run(() =>
    {
        var view = new FlyoutView(new FlyoutActions());
        var elsewhere = Dev("elsewhere", "running", ("def.localhost", false), ("dev.localhost", true));
        view.SetSnapshot(new TraySnapshot([elsewhere]), "1 of 1 up");

        // bench serve answers for the default site only, so a developer-mode site that is not the default cannot be signed in to
        Assert.Empty(elsewhere.OpenAsSites);
        Assert.False(OpenAsButton(view.Rows[0]).IsEnabled);
        Assert.Equal("Open as works on the default site only, and the default site is not in developer mode", OpenAsButton(view.Rows[0]).ToolTip);

        var both = Dev("both", "running", ("def.localhost", true), ("dev.localhost", true));
        Assert.Equal(["def.localhost"], both.OpenAsSites.Select(s => s.Name));
    });

    [Fact]
    public void WithTheDistroStoppedThereAreNoRowsAndSoNoOpenAsButton() => Sta.Run(() =>
    {
        var view = new FlyoutView(new FlyoutActions());
        view.SetSnapshot(new TraySnapshot([Dev("b", "running", ("a.localhost", true))], DistroStopped: true, Distro: "Ubuntu"), "distro stopped");
        Assert.Empty(view.Rows);
    });

    [Fact]
    public void OneDeveloperModeSiteShowsItsItemsDirectly() => Sta.Run(() =>
    {
        var view = new FlyoutView(new FlyoutActions());
        var bench = Dev("b", "running", ("a.localhost", true), ("b.localhost", false));
        var menu = view.BuildOpenAsMenu(bench, bench.OpenAsSites);
        Assert.Equal(["Administrator", "-", "Find User…"], Headers(menu));
    });

    [Fact]
    public void AMenuBuiltForSeveralSitesGivesASubmenuEach() => Sta.Run(() =>
    {
        var view = new FlyoutView(new FlyoutActions());
        var bench = Dev("b", "running", ("a.localhost", true), ("b.localhost", true), ("c.localhost", false));
        var menu = view.BuildOpenAsMenu(bench, bench.Sites.Where(s => s.DeveloperMode).ToList()); // the flyout itself passes one site, the default
        Assert.Equal(["a.localhost", "b.localhost"], Headers(menu));
        Assert.Equal(["Administrator", "-", "Find User…"], Headers((MenuItem)menu.Items[1]));
    });

    [Fact]
    public void AnItemOpensTheSitesOwnSiteAsThatUserAndFindUserOpensTheWindowForIt() => Sta.Run(() =>
    {
        var calls = new List<string>();
        var view = new FlyoutView(new FlyoutActions
        {
            OpenAsItems = (_, site) => OpenAsMenuModel.Items([new PinnedUser("priya@x.com", "NE")], "rahul@x.com", u => u == "priya@x.com" ? "NE" : "Rahul"),
            OpenAs = (b, site, user) => { calls.Add($"{b.Name} {site} {user}"); return Task.FromResult<string?>(null); },
            FindUser = (b, site) => calls.Add($"find {b.Name} {site}"),
        });
        var bench = Dev("b", "running", ("a.localhost", true), ("b.localhost", true));
        var menu = view.BuildOpenAsMenu(bench, bench.Sites);

        var second = (MenuItem)menu.Items[1];
        Assert.Equal(["Switch Back to Rahul", "-", "NE — priya@x.com", "Administrator", "-", "Find User…"], Headers(second));
        Click((MenuItem)second.Items[0]);
        Click((MenuItem)second.Items[2]);
        Click((MenuItem)second.Items[5]);
        Assert.Equal(["b b.localhost rahul@x.com", "b b.localhost priya@x.com", "find b b.localhost"], calls);
    });

    [Fact]
    public void ARefusalShowsUnderTheRowAndAUserIdKeepsItsUnderscore() => Sta.Run(() =>
    {
        var view = new FlyoutView(new FlyoutActions
        {
            OpenAsItems = (_, _) => OpenAsMenuModel.Items([new PinnedUser("a_b@x.com", null)], null, u => u),
            OpenAs = (_, _, _) => Task.FromResult<string?>("a_b@x.com is disabled on a.localhost."),
        });
        var bench = Dev("b", "running", ("a.localhost", true));
        view.SetSnapshot(new TraySnapshot([bench]), "1 of 1 up");

        var menu = view.BuildOpenAsMenu(view.Rows[0].Bench, view.Rows[0].Bench.Sites);
        Assert.Equal("a__b@x.com", Headers(menu)[0]);
        Click((MenuItem)menu.Items[0]);

        Assert.Equal("a_b@x.com is disabled on a.localhost.", view.Rows[0].FailureText);
        Assert.True(OpenAsButton(view.Rows[0]).IsEnabled);
    });

    [Fact]
    public void ARowRebuildsWhenWhoTheSiteWasOpenedAsChanges() => Sta.Run(() =>
    {
        string? label = null;
        var view = new FlyoutView(new FlyoutActions { OpenAsLabel = (_, _) => label });
        var snapshot = new TraySnapshot([Dev("b", "running", ("a.localhost", true))]);
        view.SetSnapshot(snapshot, "1 of 1 up");
        var first = view.Rows[0];
        view.SetSnapshot(snapshot, "1 of 1 up");
        Assert.Same(first, view.Rows[0]);

        label = "NE";
        view.SetSnapshot(snapshot, "1 of 1 up");
        Assert.NotSame(first, view.Rows[0]);
    });

    [Fact]
    public void ARowRebuildsWhenASiteTurnsDeveloperModeOn() => Sta.Run(() =>
    {
        var view = new FlyoutView(new FlyoutActions());
        view.SetSnapshot(new TraySnapshot([Dev("b", "running", ("a.localhost", false))]), "1 of 1 up");
        Assert.False(OpenAsButton(view.Rows[0]).IsEnabled);
        view.SetSnapshot(new TraySnapshot([Dev("b", "running", ("a.localhost", true))]), "1 of 1 up");
        Assert.True(OpenAsButton(view.Rows[0]).IsEnabled);
    });
}
