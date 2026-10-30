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
}
