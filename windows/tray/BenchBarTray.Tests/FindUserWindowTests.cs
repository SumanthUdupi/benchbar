using System.Windows;
using System.Windows.Controls.Primitives;
using BenchBar.Tray.Core;
using Xunit;

namespace BenchBar.Tray.Tests;

public class FindUserWindowTests
{
    private static readonly ResolvedTheme Theme = new(true, true);

    private static SiteUser User(string name) => new(name, "Name of " + name, "System User", ["Sales User"], 1, null);

    private static FindUserWindow Window(FindUserActions actions) => new("a.localhost", actions, Theme);

    private static void Search(FindUserWindow window) => window.SearchAsync().GetAwaiter().GetResult();

    private static FindUserActions Answer(UserList? users, string? error = null) => new()
    {
        Find = (_, _) => Task.FromResult<(UserList?, string?)>((users, error)),
    };

    [Fact]
    public void TheDebounceIs250Milliseconds() => Assert.Equal(250, FindUserWindow.DebounceMs);

    [Fact]
    public void RowsShowAndATruncatedListSaysToRefineTheSearch() => Sta.Run(() =>
    {
        var window = Window(Answer(new UserList("a.localhost", [User("a@x.com"), User("b@x.com")], Truncated: true)));
        Search(window);
        Assert.Equal(2, window.RowCount);
        Assert.False(window.ShowsEmptyState);
        Assert.Equal("Showing the first 50; refine the search", window.FooterText);
        Assert.False(window.FooterIsError);
    });

    [Fact]
    public void AListThatIsNotTruncatedHasNoFooter() => Sta.Run(() =>
    {
        var window = Window(Answer(new UserList("a.localhost", [User("a@x.com")], Truncated: false)));
        Search(window);
        Assert.Equal("", window.FooterText);
    });

    [Fact]
    public void NoMatchesShowTheEmptyState() => Sta.Run(() =>
    {
        var window = Window(Answer(new UserList("a.localhost", [], Truncated: false)));
        Search(window);
        Assert.Equal(0, window.RowCount);
        Assert.True(window.ShowsEmptyState);
        Assert.Equal("No users match", FindUserWindow.EmptyText);
    });

    [Fact]
    public void ARefusalOfTheCliIsTheFooterInRed() => Sta.Run(() =>
    {
        var window = Window(Answer(null, "The bench is not running."));
        Search(window);
        Assert.Equal(0, window.RowCount);
        Assert.Equal("The bench is not running.", window.FooterText);
        Assert.True(window.FooterIsError);
    });

    [Fact]
    public void AThrowingSearchIsTheFooterToo() => Sta.Run(() =>
    {
        var window = Window(new FindUserActions { Find = (_, _) => throw new InvalidOperationException("boom") });
        Search(window);
        Assert.Equal("boom", window.FooterText);
        Assert.True(window.FooterIsError);
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    private static ToggleButton PinButton(FindUserWindow window, string user) =>
        Descendants<ToggleButton>((DependencyObject)window.Content)
            .Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == $"Pin {user}");

    private static void Click(ToggleButton button, bool isChecked)
    {
        button.IsChecked = isChecked; // a real click flips the state before it raises Click
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    [Fact]
    public void APinThatCouldNotBeSavedStaysPinnedButTheFooterSaysSo() => Sta.Run(() =>
    {
        var actions = Answer(new UserList("a.localhost", [User("a@x.com")], Truncated: false));
        actions.Pin = (_, _) => PinOutcome.NotSaved;
        var window = Window(actions);
        Search(window);

        var pin = PinButton(window, "a@x.com");
        Click(pin, isChecked: true);

        Assert.Equal(FindUserWindow.PinSaveFailedText, window.FooterText);
        Assert.True(window.FooterIsError);
        Assert.Equal("Pinned", pin.Content);
    });

    [Fact]
    public void APinTheSiteHasNoRoomForIsUncheckedWithTheLimitInTheFooter() => Sta.Run(() =>
    {
        var actions = Answer(new UserList("a.localhost", [User("a@x.com")], Truncated: false));
        actions.Pin = (_, _) => PinOutcome.NoRoom;
        var window = Window(actions);
        Search(window);

        var pin = PinButton(window, "a@x.com");
        Click(pin, isChecked: true);

        Assert.Equal(FindUserWindow.PinLimitText, window.FooterText);
        Assert.False(pin.IsChecked);
    });

    [Fact]
    public void AnUnpinThatCouldNotBeSavedShowsInTheFooter() => Sta.Run(() =>
    {
        var actions = Answer(new UserList("a.localhost", [User("a@x.com")], Truncated: false));
        actions.Pins = () => [new PinnedUser("A@X.com", null)]; // pinned in another case: still the same user
        actions.Unpin = _ => false;
        var window = Window(actions);
        Search(window);

        var pin = PinButton(window, "a@x.com");
        Assert.Equal("Pinned", pin.Content);
        Click(pin, isChecked: false);

        Assert.Equal(FindUserWindow.UnpinSaveFailedText, window.FooterText);
        Assert.True(window.FooterIsError);
    });
}
