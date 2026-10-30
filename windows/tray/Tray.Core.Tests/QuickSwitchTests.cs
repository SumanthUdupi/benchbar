using BenchBar.Tray.Core;
using Xunit;

namespace Tray.Core.Tests;

public class QuickSwitchTests
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["ne@x.com"] = "Nina Edwards",
        ["pm@x.com"] = "Pavan Mehta",
        ["Administrator"] = "Administrator",
        ["zed@x.com"] = "Zed Zimmer",
    };

    private static QuickSwitchData Data(
        string[]? pins = null, string? previous = null, string? current = null, Dictionary<string, string>? labels = null)
    {
        var pinned = (pins ?? []).Select(u => new PinnedUser(u, labels is not null && labels.TryGetValue(u, out var l) ? l : null)).ToList();
        return new QuickSwitchData(
            _ => pinned,
            _ => current,
            _ => previous,
            (_, user) => pinned.FirstOrDefault(p => p.User == user)?.Label ?? (Names.TryGetValue(user, out var n) ? n : user),
            (_, user) => Names.TryGetValue(user, out var n) ? n : null);
    }

    private static QuickSwitchModel Model(QuickSwitchData data, params string[] sites) =>
        new(sites.Length > 0 ? sites : ["a.localhost"], null, data);

    private static SiteUser User(string name, string full = "") => new(name, full, "System User", [], 0, null);

    private static BenchStatus Bench(string path, BenchState state, params (string Name, bool Default, bool Dev)[] sites) =>
        new(Path.GetFileName(path), path, state, null, null, sites.FirstOrDefault().Name)
        {
            Sites = [.. sites.Select(s => new SiteInfo(s.Name, s.Default, s.Dev))],
        };

    [Fact]
    public void RowsAreSwitchBackThenThePinsThenAdministrator()
    {
        var model = Model(Data(["ne@x.com", "pm@x.com"], previous: "zed@x.com"));
        Assert.Equal(
            ["Switch Back to Zed Zimmer", "Nina Edwards — ne@x.com", "Pavan Mehta — pm@x.com", "Administrator"],
            model.Rows.Select(r => r.Text));
        Assert.Equal(
            [QuickRowKind.SwitchBack, QuickRowKind.Pin, QuickRowKind.Pin, QuickRowKind.Administrator],
            model.Rows.Select(r => r.Kind));
    }

    [Fact]
    public void WithoutAPreviousUserThereIsNoSwitchBackRow()
    {
        var model = Model(Data(["ne@x.com"]));
        Assert.Equal(["Nina Edwards — ne@x.com", "Administrator"], model.Rows.Select(r => r.Text));
    }

    [Fact]
    public void ThePinLabelIsTheRowLabelAndAnUnnamedUserShowsOnlyItsId()
    {
        var model = Model(Data(["ne@x.com", "who@x.com"], labels: new() { ["ne@x.com"] = "NE" }));
        Assert.Equal(["NE — ne@x.com", "who@x.com", "Administrator"], model.Rows.Select(r => r.Text));
    }

    [Fact]
    public void TheDefaultSelectionIsSwitchBackElseTheFirstPin()
    {
        var withBack = Model(Data(["ne@x.com"], previous: "zed@x.com"));
        Assert.Equal(0, withBack.Selected);
        Assert.Equal(QuickRowKind.SwitchBack, withBack.SelectedRow!.Kind);

        var withoutBack = Model(Data(["ne@x.com", "pm@x.com"]));
        Assert.Equal("ne@x.com", withoutBack.SelectedRow!.User);
    }

    [Fact]
    public void ReturnWithoutTouchingAnythingSwitchesBackToThePreviousUser()
    {
        var model = Model(Data(["ne@x.com"], previous: "zed@x.com"));
        Assert.Equal(new QuickSwitchChoice("a.localhost", "zed@x.com"), model.Confirm());
    }

    [Fact]
    public void UpAndDownWrapAround()
    {
        var model = Model(Data(["ne@x.com"], previous: "zed@x.com")); // 3 rows
        model.Move(-1);
        Assert.Equal(2, model.Selected);
        model.Move(1);
        Assert.Equal(0, model.Selected);
        model.Move(1);
        model.Move(1);
        model.Move(1);
        Assert.Equal(0, model.Selected);
    }

    [Fact]
    public void PinsKeepDigits1To9AndAdministratorIs0WhetherOrNotSwitchBackShows()
    {
        var pins = Enumerable.Range(1, 9).Select(i => $"u{i}@x.com").ToArray();
        foreach (var previous in new string?[] { null, "zed@x.com" })
        {
            var model = Model(Data(pins, previous: previous)); // 9 pins + Administrator, with or without Switch Back
            for (var digit = 1; digit <= 9; digit++) Assert.Equal($"u{digit}@x.com", model.RowForDigit(digit)!.User);
            Assert.Equal("Administrator", model.RowForDigit(0)!.User);
            Assert.Equal(previous is null ? 10 : 11, model.Rows.Count);
        }
    }

    [Fact]
    public void SwitchBackAndSearchedUsersHaveNoDigit()
    {
        var model = Model(Data(["ne@x.com", "pm@x.com"], previous: "zed@x.com"));
        Assert.Equal([null, "1", "2", "0"], Enumerable.Range(0, model.Rows.Count).Select(model.DigitLabel));
        Assert.Null(model.DigitLabel(4));
        Assert.Null(model.DigitLabel(-1));

        model.SetQuery("x");
        model.SetOthers([User("xavier@x.com")]);
        Assert.Equal(QuickRowKind.Other, model.Rows[^1].Kind);
        Assert.Null(model.DigitLabel(model.Rows.Count - 1));
    }

    [Fact]
    public void ADigitWithNoRowDoesNothing()
    {
        var model = Model(Data()); // Administrator only
        Assert.Null(model.RowForDigit(1));
        Assert.Null(model.ConfirmDigit(1));
        Assert.Null(model.RowForDigit(5));
        Assert.False(model.Busy);
        Assert.Equal("Administrator", model.ConfirmDigit(0)!.User);
    }

    [Fact]
    public void ABlankQueryIsNotAQueryDigitsStayActiveAndRowsStayUnfiltered()
    {
        var model = Model(Data(["ne@x.com"]));
        model.SetQuery("   ");
        Assert.True(model.DigitsActive);
        Assert.Equal(2, model.Rows.Count);
        Assert.Equal("ne@x.com", model.ConfirmDigit(1)!.User);

        model.Rearm();
        model.SetQuery(" a ");
        Assert.False(model.DigitsActive);
        Assert.Null(model.ConfirmDigit(1));
    }

    [Fact]
    public void ADigitIsTextOnceTheBoxHasAQuery()
    {
        var model = Model(Data(["ne@x.com"]));
        model.SetQuery("1");
        Assert.Null(model.ConfirmDigit(1));
        Assert.False(model.Busy);
    }

    [Fact]
    public void TypingFiltersByLabelUserIdAndFullName()
    {
        var model = Model(Data(["ne@x.com", "pm@x.com"], previous: "zed@x.com", labels: new() { ["ne@x.com"] = "NE" }));

        model.SetQuery("ne"); // the label, and the id
        Assert.Equal(["ne@x.com"], model.Rows.Select(r => r.User));
        model.SetQuery("MEHTA"); // the full name, any case
        Assert.Equal(["pm@x.com"], model.Rows.Select(r => r.User));
        model.SetQuery("edwards"); // the full name of a pin whose label hides it
        Assert.Equal(["ne@x.com"], model.Rows.Select(r => r.User));
        model.SetQuery("x.com");
        Assert.Equal(["zed@x.com", "ne@x.com", "pm@x.com"], model.Rows.Select(r => r.User));
    }

    [Fact]
    public void ANewQueryPutsTheSelectionOnTheFirstMatch()
    {
        var model = Model(Data(["ne@x.com", "pm@x.com"]));
        model.Move(2);
        model.SetQuery("x.com");
        Assert.Equal(0, model.Selected);
    }

    [Fact]
    public void TwoCharactersAskForASearchAndOneDoesNot()
    {
        var model = Model(Data());
        Assert.Equal(250, QuickSwitchModel.SearchDebounceMs);
        Assert.Equal(8, QuickSwitchModel.SearchLimit);
        model.SetQuery("n");
        Assert.False(model.ShouldSearch);
        model.SetQuery("ne");
        Assert.True(model.ShouldSearch);
        Assert.Equal("ne", model.SearchText);
        model.SetQuery(" n ");
        Assert.False(model.ShouldSearch);
    }

    [Fact]
    public void SearchedUsersFollowTheMatchesAndLeaveOutThoseAlreadyShown()
    {
        var model = Model(Data(["ne@x.com"]));
        model.SetQuery("ne");
        model.SetOthers([User("ne@x.com", "Nina Edwards"), User("neo@x.com", "Neo Anders"), User("ned@x.com")]);

        Assert.Equal(1, model.PrimaryCount);
        Assert.Equal(["ne@x.com", "neo@x.com", "ned@x.com"], model.Rows.Select(r => r.User));
        Assert.Equal(["Nina Edwards — ne@x.com", "Neo Anders — neo@x.com", "ned@x.com"], model.Rows.Select(r => r.Text));
        Assert.Equal(QuickRowKind.Other, model.Rows[1].Kind);
        Assert.Equal("Other users", QuickSwitchModel.OtherUsersHeader);
    }

    [Fact]
    public void SearchedUsersAreCappedAtEight()
    {
        var model = Model(Data());
        model.SetQuery("zz");
        model.SetOthers(Enumerable.Range(1, 20).Select(i => User($"zz{i}@x.com")));
        Assert.Equal(8, model.Rows.Count(r => r.Kind == QuickRowKind.Other));
    }

    [Fact]
    public void AnotherQueryDropsTheOldSearchResults()
    {
        var model = Model(Data());
        model.SetQuery("zz");
        model.SetOthers([User("zz1@x.com")]);
        model.SetQuery("zzz");
        Assert.DoesNotContain(model.Rows, r => r.Kind == QuickRowKind.Other);
    }

    [Fact]
    public void NothingMatchingLeavesNoRowsAndEnterDoesNothing()
    {
        var model = Model(Data(["ne@x.com"]));
        model.SetQuery("qqqq");
        Assert.Empty(model.Rows);
        Assert.Null(model.SelectedRow);
        Assert.Null(model.Confirm());
        model.Move(1); // no rows: no crash
        Assert.Equal("No users match", QuickSwitchModel.NoMatchText);
    }

    [Fact]
    public void TabCyclesTheGivenSitesAndWrapsAndOneSiteStays()
    {
        var model = Model(Data(), "a.localhost", "b.localhost", "c.localhost");
        Assert.Equal("Open a.localhost as…", model.Title);
        Assert.True(model.NextSite());
        Assert.Equal("b.localhost", model.Site);
        model.NextSite();
        model.NextSite();
        Assert.Equal("a.localhost", model.Site);

        var single = Model(Data(), "only.localhost");
        Assert.False(single.NextSite());
        Assert.Equal("only.localhost", single.Site);
    }

    [Fact]
    public void TabRebuildsTheRowsFromTheNewSitesPinsAndKeepsTheQuery()
    {
        var data = new QuickSwitchData(
            site => site == "a.localhost" ? [new PinnedUser("a-pin@x.com", null)] : [new PinnedUser("b-pin@x.com", null)],
            _ => null, _ => null, (_, user) => user, (_, _) => null);
        var model = new QuickSwitchModel(["a.localhost", "b.localhost"], null, data);
        model.SetQuery("pin");
        Assert.Equal(["a-pin@x.com"], model.Rows.Select(r => r.User));
        model.NextSite();
        Assert.Equal("pin", model.Query);
        Assert.Equal(["b-pin@x.com"], model.Rows.Select(r => r.User));
        Assert.Equal(new QuickSwitchChoice("b.localhost", "b-pin@x.com"), model.Confirm());
    }

    [Fact]
    public void TheStartSiteIsUsedWhenItIsOneOfTheSites()
    {
        var model = new QuickSwitchModel(["a.localhost", "b.localhost"], "b.localhost", Data());
        Assert.Equal("b.localhost", model.Site);
        Assert.Equal("a.localhost", new QuickSwitchModel(["a.localhost", "b.localhost"], "zzz", Data()).Site);
    }

    [Fact]
    public void TheCurrentUserIsMarkedOnItsRowOnly()
    {
        var model = Model(Data(["ne@x.com", "pm@x.com"], previous: "zed@x.com", current: "PM@x.com"));
        Assert.Equal([false, false, true, false], model.Rows.Select(r => r.IsCurrent));
        Assert.Equal("✓", QuickSwitchModel.CurrentMark);
        Assert.Equal("current", QuickSwitchModel.CurrentHint);
    }

    [Fact]
    public void ReturnProducesOneSwitchEvenWhenPressedTwice()
    {
        var model = Model(Data(["ne@x.com"]));
        Assert.NotNull(model.Confirm());
        Assert.True(model.Busy);
        Assert.Null(model.Confirm());
        Assert.Null(model.ConfirmDigit(1));
        model.Move(1); // frozen while the switch runs
        Assert.Equal(0, model.Selected);
    }

    [Fact]
    public void AFailedSwitchCanBeTriedAgain()
    {
        var model = Model(Data(["ne@x.com"]));
        Assert.NotNull(model.Confirm());
        model.Rearm();
        Assert.NotNull(model.Confirm());
    }

    [Fact]
    public void ADigitSwitchIsOneActionToo()
    {
        var model = Model(Data(["ne@x.com"]));
        Assert.Equal("ne@x.com", model.ConfirmDigit(1)!.User);
        Assert.Null(model.ConfirmDigit(1));
        Assert.Null(model.Confirm());
    }

    private static TraySnapshot Snap(params BenchStatus[] benches) => new(benches);

    [Fact]
    public void NoDefaultSiteInDeveloperModeGivesItsCopy()
    {
        var target = QuickSwitchModel.Choose(Snap(Bench("/b", BenchState.Running, ("a.localhost", true, false))), null);
        Assert.Equal(QuickSwitchState.NoDeveloperSite, target.State);
        Assert.Contains("default site", target.Message);
        Assert.DoesNotContain("benchbar open --help", target.Message);
        Assert.Null(target.Bench);
    }

    [Fact]
    public void OnlyTheDefaultSiteIsOfferedBecauseBenchServeAnswersForNoOtherSite()
    {
        var nonDefaultOnly = Bench("/run", BenchState.Running, ("def.localhost", true, false), ("dev.localhost", false, true));
        Assert.Equal(QuickSwitchState.NoDeveloperSite, QuickSwitchModel.Choose(Snap(nonDefaultOnly), null).State);

        var both = Bench("/run", BenchState.Running, ("dev1.localhost", false, true), ("def.localhost", true, true), ("dev2.localhost", false, true));
        var target = QuickSwitchModel.Choose(Snap(both), null);
        Assert.Equal(QuickSwitchState.Ready, target.State);
        Assert.Equal(["def.localhost"], target.Sites);
        Assert.Equal("def.localhost", target.StartSite);
        Assert.Equal(["def.localhost"], both.OpenAsSites.Select(s => s.Name));
    }

    [Fact]
    public void AStoppedBenchGivesItsCopy()
    {
        var stopped = QuickSwitchModel.Choose(Snap(Bench("/b", BenchState.Stopped, ("a.localhost", true, true))), null);
        Assert.Equal(QuickSwitchState.BenchStopped, stopped.State);
        Assert.StartsWith("Start the bench first", stopped.Message);

        var noneRunning = QuickSwitchModel.Choose(Snap(Bench("/b", BenchState.Stopped)), null);
        Assert.Equal(QuickSwitchState.BenchStopped, noneRunning.State);
    }

    [Fact]
    public void AStartingBenchSaysItIsStartingNotToStartIt()
    {
        var withSite = QuickSwitchModel.Choose(Snap(Bench("/work/b", BenchState.Starting, ("a.localhost", true, true))), null);
        Assert.Equal(QuickSwitchState.BenchStarting, withSite.State);
        Assert.Equal("b is starting.", withSite.Message);

        var noSiteYet = QuickSwitchModel.Choose(Snap(Bench("/work/b", BenchState.Starting)), null);
        Assert.Equal(QuickSwitchState.BenchStarting, noSiteYet.State);
        Assert.Equal("b is starting.", noSiteYet.Message);
    }

    [Fact]
    public void AProblemOfTheSnapshotIsShownAsItIsNotAsAStoppedBench()
    {
        var snapshot = new TraySnapshot(
            [Bench("/b", BenchState.Unknown)], Problem: "benchbar.exe was not found. Put it next to BenchBarTray.exe or on PATH (benchbar.exe path install).");
        var target = QuickSwitchModel.Choose(snapshot, null);
        Assert.Equal(QuickSwitchState.Problem, target.State);
        Assert.Equal(snapshot.Problem, target.Message);
    }

    [Fact]
    public void BeforeTheFirstPollItSaysLoadingAndAfterAPollWithNoBenchesItDoesNot()
    {
        var loading = QuickSwitchModel.Choose(TraySnapshot.Empty, null);
        Assert.Equal(QuickSwitchState.Loading, loading.State);
        Assert.Equal("Loading…", loading.Message);

        Assert.Equal(QuickSwitchState.NoDeveloperSite, QuickSwitchModel.Choose(new TraySnapshot([]), null).State);
    }

    [Fact]
    public void AStoppedDistroGivesItsCopyAndNeverPicksABench()
    {
        var snapshot = new TraySnapshot([Bench("/b", BenchState.Running, ("a.localhost", true, true))], DistroStopped: true, Distro: "Ubuntu");
        var target = QuickSwitchModel.Choose(snapshot, null);
        Assert.Equal(QuickSwitchState.DistroStopped, target.State);
        Assert.StartsWith("The WSL distro is stopped", target.Message);
        Assert.Null(target.Bench);
    }

    [Fact]
    public void TheRunningBenchWithADefaultDeveloperSiteIsChosen()
    {
        var benches = new[]
        {
            Bench("/stopped", BenchState.Stopped, ("s.localhost", true, true)),
            Bench("/plain", BenchState.Running, ("plain.localhost", true, false)),
            Bench("/run", BenchState.Running, ("dev.localhost", true, true)),
        };
        var target = QuickSwitchModel.Choose(Snap(benches), null);
        Assert.Equal(QuickSwitchState.Ready, target.State);
        Assert.Equal("/run", target.Bench!.Path);
        Assert.Equal(["dev.localhost"], target.Sites);
    }

    [Fact]
    public void TheBenchOfTheLastSwitchIsPreferredAndTheLastSiteIsKeptOnlyWhenOffered()
    {
        var benches = new[]
        {
            Bench("/one", BenchState.Running, ("one.localhost", true, true)),
            Bench("/two", BenchState.Running, ("two.localhost", true, true)),
        };
        var target = QuickSwitchModel.Choose(Snap(benches), ("/two", "two.localhost"));
        Assert.Equal("/two", target.Bench!.Path);
        Assert.Equal("two.localhost", QuickSwitchModel.Choose(Snap(benches), ("/two", "gone.localhost")).StartSite);
    }
}
