using System.Net;
using System.Text;
using BenchBar.Tray.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Tray.Core.Tests;

public class OpenAsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BenchStatus Bench(BenchState state = BenchState.Running) =>
        new("b", "/b", state, null, "http://a.localhost:8000", "a.localhost");

    private static string OpenJson(string site, int port, string user, string sid) =>
        "{\"schema_version\":1,\"site\":\"" + site + "\",\"user\":\"" + user + "\",\"full_name\":\"Full " + user + "\"," +
        "\"user_type\":\"System User\",\"path\":\"/app\",\"opened\":false," +
        "\"url\":\"http://" + site + ":" + port + "/login?redirect-to=%2Fapp&sid=" + sid + "&t=1\"}";

    /// <summary>A service over a scripted source. <c>Who</c> is what the session check answers; <c>Sids</c> hands out a new sid per CLI call.</summary>
    private sealed class Rig
    {
        private int _sid;

        public Rig()
        {
            Source.Output = args =>
            {
                var site = args[Array.IndexOf(args, "--site") + 1];
                var user = args[Array.IndexOf(args, "--as") + 1];
                return OpenJson(site, Port, user, "sid" + (++_sid));
            };
            Service = new OpenAsService(Source, (uri, sid, _) =>
            {
                Checks.Add((uri, sid));
                return Task.FromResult(Who(sid));
            }, Time);
        }

        public int Port { get; set; } = 8000;
        public ScriptedSource Source { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public List<(Uri Uri, string Sid)> Checks { get; } = [];
        public Func<string, string?> Who { get; set; } = _ => "Guest";
        public OpenAsService Service { get; }
    }

    private static string Query(Uri url, string key) =>
        url.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Single(p => p[0] == key)[1];

    [Fact]
    public async Task TheCliGetsEveryArgumentAsOneEntryAndTheUrlIsTheCliOwn()
    {
        var rig = new Rig();
        var (url, error) = await rig.Service.SwitchAsync(Bench(), "a.localhost", "a+b@x.com", "/app/nst?x=1&sid=old#y", Ct);

        Assert.Null(error);
        Assert.Equal(
            ["open", "--bench-dir", "/b", "--site", "a.localhost", "--as", "a+b@x.com", "--path", "/app/nst?x=1&sid=old#y", "--print-url", "--json"],
            Assert.Single(rig.Source.OutputCalls));
        Assert.Equal("http://a.localhost:8000/login?redirect-to=%2Fapp&sid=sid1&t=1", url!.OriginalString);
    }

    [Fact]
    public async Task ADefaultPathIsLeftToTheCli()
    {
        var rig = new Rig();
        await rig.Service.SwitchAsync(Bench(), "a.localhost", "u@x.com", null, Ct);
        Assert.DoesNotContain("--path", rig.Source.OutputCalls[0]);
    }

    [Fact]
    public async Task ASiteThatIsNotTheDefaultKeepsItsNameAndPortThroughArgsAndUrl()
    {
        var rig = new Rig { Port = 8001 };
        var (url, _) = await rig.Service.SwitchAsync(Bench(), "two.localhost", "u@x.com", null, Ct);

        var args = rig.Source.OutputCalls[0];
        Assert.Equal("two.localhost", args[Array.IndexOf(args, "--site") + 1]);
        Assert.NotNull(url);
        Assert.Equal("two.localhost", url.Host);
        Assert.Equal(8001, url.Port);

        rig.Who = _ => "u@x.com";
        var (again, _) = await rig.Service.SwitchAsync(Bench(), "two.localhost", "u@x.com", null, Ct);
        Assert.NotNull(again);
        Assert.Equal("two.localhost", again.Host);
        Assert.Equal(8001, again.Port);
        Assert.Equal(8001, rig.Checks.Single().Uri.Port);
    }

    [Fact]
    public async Task ALiveSessionIsReusedWithoutTheCliAndEveryUrlHasANewT()
    {
        var rig = new Rig();
        var (first, _) = await rig.Service.SwitchAsync(Bench(), "a.localhost", "u@x.com", null, Ct);
        rig.Who = sid => sid == "sid1" ? "u@x.com" : "Guest";

        var (second, error) = await rig.Service.SwitchAsync(Bench(), "a.localhost", "u@x.com", "/app/todo?a=b", Ct);
        var (third, _) = await rig.Service.SwitchAsync(Bench(), "a.localhost", "u@x.com", "/app/todo?a=b", Ct);

        Assert.Null(error);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
        Assert.Single(rig.Source.OutputCalls);
        Assert.Equal("sid1", Query(second, "sid"));
        Assert.Equal("%2Fapp%2Ftodo%3Fa%3Db", Query(second, "redirect-to"));
        Assert.Equal("http://a.localhost:8000/login", second.GetLeftPart(UriPartial.Path));
        Assert.NotEqual(Query(second, "t"), Query(third, "t")); // the clock did not move: still different
        Assert.NotEqual(Query(first, "t"), Query(second, "t"));
        Assert.Equal(2, rig.Checks.Count);
        Assert.Equal("http://a.localhost:8000/", rig.Checks[0].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task AReusedSessionLandsOnTheCliDefaultPathWhenNoneIsGiven()
    {
        var rig = new Rig();
        rig.Source.Output = _ => OpenJson("a.localhost", 8000, "visitor@x.com", "sidW").Replace("\"path\":\"/app\"", "\"path\":\"/\"");
        await rig.Service.SwitchAsync(Bench(), "a.localhost", "visitor@x.com", null, Ct);
        rig.Who = _ => "visitor@x.com";
        var (url, _) = await rig.Service.SwitchAsync(Bench(), "a.localhost", "visitor@x.com", null, Ct);
        Assert.Equal("%2F", Query(url!, "redirect-to"));
    }

    [Theory]
    [InlineData("Guest")]
    [InlineData("someone.else@x.com")]
    [InlineData(null)]
    public async Task ADeadOrForeignSessionIsReplacedThroughTheCli(string? answer)
    {
        var rig = new Rig();
        await rig.Service.SwitchAsync(Bench(), "a.localhost", "u@x.com", null, Ct);
        rig.Who = _ => answer;

        var (url, error) = await rig.Service.SwitchAsync(Bench(), "a.localhost", "u@x.com", null, Ct);

        Assert.Null(error);
        Assert.Equal(2, rig.Source.OutputCalls.Count);
        Assert.Equal("sid2", Query(url!, "sid"));
        Assert.Equal(["sid1"], rig.Checks.Select(c => c.Sid));
    }

    [Fact]
    public async Task ASessionCheckThatCannotReachTheSiteCountsAsDead()
    {
        var rig = new Rig();
        var service = new OpenAsService(rig.Source, (_, _, _) => throw new HttpRequestException("refused"), rig.Time);
        await service.SwitchAsync(Bench(), "a.localhost", "u@x.com", null, Ct);
        var (url, error) = await service.SwitchAsync(Bench(), "a.localhost", "u@x.com", null, Ct);
        Assert.Null(error);
        Assert.Equal("sid2", Query(url!, "sid"));
        Assert.Equal(2, rig.Source.OutputCalls.Count);
    }

    [Fact]
    public async Task ASessionIsPerBenchSiteAndUser()
    {
        var rig = new Rig { Who = _ => "u@x.com" };
        await rig.Service.SwitchAsync(Bench(), "a.localhost", "u@x.com", null, Ct);
        await rig.Service.SwitchAsync(Bench(), "two.localhost", "u@x.com", null, Ct);
        await rig.Service.SwitchAsync(Bench(), "a.localhost", "v@x.com", null, Ct);
        Assert.Equal(3, rig.Source.OutputCalls.Count);
        Assert.Empty(rig.Checks);
    }

    [Fact]
    public async Task AStoppedBenchIsRefusedBeforeAnyCallToTheCli()
    {
        var rig = new Rig();
        foreach (var state in new[] { BenchState.Stopped, BenchState.Starting, BenchState.Paused, BenchState.Unknown })
        {
            var (url, error) = await rig.Service.SwitchAsync(Bench(state), "a.localhost", "u@x.com", null, Ct);
            Assert.Null(url);
            Assert.Equal("Start the bench first.", error);
            var (users, usersError) = await rig.Service.FindUsersAsync(Bench(state), "a.localhost", null, Ct);
            Assert.Null(users);
            Assert.Equal("Start the bench first.", usersError);
        }
        Assert.Empty(rig.Source.OutputCalls);
        Assert.Empty(rig.Checks);
    }

    [Fact]
    public async Task ARefusalOfTheCliIsTheErrorAndChangesNoState()
    {
        var rig = new Rig();
        rig.Source.OutputFails = new CliProblemException("a.localhost is not in developer mode, so benchbar will not sign in as another user.");
        var (url, error) = await rig.Service.SwitchAsync(Bench(), "a.localhost", "u@x.com", null, Ct);
        Assert.Null(url);
        Assert.Equal("a.localhost is not in developer mode, so benchbar will not sign in as another user.", error);
        Assert.Null(rig.Service.CurrentUser("/b", "a.localhost"));
    }

    [Fact]
    public async Task UnreadableOutputNeverQuotesTheDocumentBecauseItHoldsTheSessionId()
    {
        var rig = new Rig();
        rig.Source.Output = _ => "{\"user\":\"u@x.com\",\"url\":\"http://a.localhost:8000/login?sid=SECRETSID\", broken";
        var (url, error) = await rig.Service.SwitchAsync(Bench(), "a.localhost", "u@x.com", null, Ct);
        Assert.Null(url);
        Assert.Equal(OpenAsService.UnreadableMessage, error);
        Assert.DoesNotContain("SECRETSID", error);
    }

    [Fact]
    public async Task ACliThatLeavesOutTheUrlIsTooOldForOpenAs()
    {
        var rig = new Rig();
        rig.Source.Output = _ => "{\"user\":\"u@x.com\",\"path\":\"/app\"}";
        var (url, error) = await rig.Service.SwitchAsync(Bench(), "a.localhost", "u@x.com", null, Ct);
        Assert.Null(url);
        Assert.Equal(OpenAsService.UnsupportedMessage, error);
        Assert.Null(rig.Service.CurrentUser("/b", "a.localhost"));
    }

    [Fact]
    public async Task RecentsKeepTwoNewestFirstAndSwitchBackPointsAtThePreviousUser()
    {
        var rig = new Rig();
        Assert.Null(rig.Service.CurrentUser("/b", "a.localhost"));
        Assert.Null(rig.Service.PreviousUser("/b", "a.localhost"));

        foreach (var user in new[] { "a@x.com", "b@x.com", "c@x.com" })
            await rig.Service.SwitchAsync(Bench(), "a.localhost", user, null, Ct);
        Assert.Equal("c@x.com", rig.Service.CurrentUser("/b", "a.localhost"));
        Assert.Equal("b@x.com", rig.Service.PreviousUser("/b", "a.localhost"));

        await rig.Service.SwitchAsync(Bench(), "a.localhost", "b@x.com", null, Ct);
        Assert.Equal("b@x.com", rig.Service.CurrentUser("/b", "a.localhost"));
        Assert.Equal("c@x.com", rig.Service.PreviousUser("/b", "a.localhost"));

        await rig.Service.SwitchAsync(Bench(), "a.localhost", "b@x.com", null, Ct); // a click always switches, never "already b"
        Assert.Equal("c@x.com", rig.Service.PreviousUser("/b", "a.localhost"));

        Assert.Null(rig.Service.CurrentUser("/b", "two.localhost"));
    }

    [Fact]
    public async Task TheLabelIsThePinLabelElseTheFullNameElseTheUserId()
    {
        var rig = new Rig();
        await rig.Service.SwitchAsync(Bench(), "a.localhost", "a@x.com", null, Ct);
        PinnedUser[] pins = [new PinnedUser("a@x.com", "NE"), new PinnedUser("p@x.com", null)];

        Assert.Equal("NE", rig.Service.LabelFor("/b", "a.localhost", "a@x.com", pins));
        Assert.Equal("Full a@x.com", rig.Service.LabelFor("/b", "a.localhost", "a@x.com", []));
        Assert.Equal("p@x.com", rig.Service.LabelFor("/b", "a.localhost", "p@x.com", pins));
        Assert.Equal("NE", rig.Service.CurrentLabel("/b", "a.localhost", pins));
        Assert.Null(rig.Service.CurrentLabel("/b", "two.localhost", pins));
    }

    [Fact]
    public async Task FindUsersPassesTheSearchAsOneArgumentAndRemembersFullNames()
    {
        var rig = new Rig();
        rig.Source.Output = _ => File.ReadAllText(Path.Combine(Fixtures.Dir("recorded"), "users.json"));

        var (list, error) = await rig.Service.FindUsersAsync(Bench(), "a.localhost", " o'brien x ", Ct);
        Assert.Null(error);
        Assert.Equal(3, list!.Users.Count);
        Assert.Equal(
            ["users", "--bench-dir", "/b", "--site", "a.localhost", "--search", "o'brien x", "--limit", "50", "--json"],
            rig.Source.OutputCalls[0]);
        Assert.Equal("Priya Nair", rig.Service.LabelFor("/b", "a.localhost", "priya@example.com", []));

        await rig.Service.FindUsersAsync(Bench(), "a.localhost", "  ", Ct);
        Assert.DoesNotContain("--search", rig.Source.OutputCalls[1]);
    }

    [Fact]
    public async Task FindUsersReportsACliRefusalAsTheError()
    {
        var rig = new Rig();
        rig.Source.OutputFails = new CliProblemException("The bench is not running.");
        var (list, error) = await rig.Service.FindUsersAsync(Bench(), "a.localhost", null, Ct);
        Assert.Null(list);
        Assert.Equal("The bench is not running.", error);
    }

    [Theory]
    [InlineData("/app", "%2Fapp")]
    [InlineData("/app/nst?status=Open&sid=old#x", "%2Fapp%2Fnst%3Fstatus%3DOpen%23x")]
    [InlineData("/app/x?sid=old", "%2Fapp%2Fx")]
    [InlineData("/app/x?sid=old&a=1", "%2Fapp%2Fx%3Fa%3D1")]
    [InlineData("/app/x?a=1&sid", "%2Fapp%2Fx%3Fa%3D1")]
    [InlineData("/app/x?mysid=keep", "%2Fapp%2Fx%3Fmysid%3Dkeep")]
    [InlineData("/app/a b+c", "%2Fapp%2Fa%20b%2Bc")]
    [InlineData("/app/q?name=o'brien", "%2Fapp%2Fq%3Fname%3Do%27brien")]
    [InlineData("", "%2F")]
    public void BuildLoginUrlEncodesThePathOnceAndDropsAnOldSid(string path, string encoded) =>
        Assert.Equal(
            "http://a.localhost:8000/login?redirect-to=" + encoded + "&sid=S1&t=5",
            OpenAsService.BuildLoginUrl("http://a.localhost:8000", path, "S1", 5).OriginalString);

    [Fact]
    public void BuildLoginUrlAcceptsABaseWithATrailingSlash() =>
        Assert.Equal(
            "http://a.localhost:8000/login?redirect-to=%2Fapp&sid=S1&t=5",
            OpenAsService.BuildLoginUrl("http://a.localhost:8000/", "/app", "S1", 5).OriginalString);

    [Fact]
    public void TheMenuIsSwitchBackThenPinsThenAdministratorThenFindUser()
    {
        PinnedUser[] pins = [new PinnedUser("priya@x.com", "NE"), new PinnedUser("arun@x.com", null)];
        string Label(string user) => user switch { "priya@x.com" => "NE", "arun@x.com" => "Arun K", "rahul@x.com" => "Rahul", _ => user };

        var items = OpenAsMenuModel.Items(pins, "rahul@x.com", Label);

        Assert.Equal(
            [OpenAsItemKind.SwitchBack, OpenAsItemKind.Separator, OpenAsItemKind.Pin, OpenAsItemKind.Pin,
             OpenAsItemKind.Administrator, OpenAsItemKind.Separator, OpenAsItemKind.FindUser],
            items.Select(i => i.Kind));
        Assert.Equal("Switch Back to Rahul", items[0].Header);
        Assert.Equal("rahul@x.com", items[0].User);
        Assert.Equal("NE — priya@x.com", items[2].Header);
        Assert.Equal("Arun K — arun@x.com", items[3].Header);
        Assert.Equal("Administrator", items[4].Header);
        Assert.Equal("Administrator", items[4].User);
        Assert.Equal("Find User…", items[6].Header);
        Assert.Null(items[6].User);
        Assert.Equal("Open as…", OpenAsMenuModel.Title);
    }

    [Fact]
    public void WithoutAPreviousUserThereIsNoSwitchBackAndNoLeadingSeparatorAndAPinWithNoNameIsJustTheId()
    {
        var items = OpenAsMenuModel.Items([new PinnedUser("x@x.com", null)], null, u => u);
        Assert.Equal([OpenAsItemKind.Pin, OpenAsItemKind.Administrator, OpenAsItemKind.Separator, OpenAsItemKind.FindUser], items.Select(i => i.Kind));
        Assert.Equal("x@x.com", items[0].Header);
        Assert.Equal(
            [OpenAsItemKind.Administrator, OpenAsItemKind.Separator, OpenAsItemKind.FindUser],
            OpenAsMenuModel.Items([], null, u => u).Select(i => i.Kind));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task TheSessionCheckSendsTheSidAsACookieToGetLoggedUser()
    {
        var handler = new StubHandler(_ => Json("{\"message\":\"u@x.com\"}"));
        using var http = new HttpClient(handler);

        var who = await OpenAsService.CheckSessionAsync(http, new Uri("http://a.localhost:8000"), "S1", TimeSpan.FromSeconds(2), Ct);

        Assert.Equal("u@x.com", who);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:8000/api/method/frappe.auth.get_logged_user", request.RequestUri!.AbsoluteUri);
        Assert.Equal("a.localhost:8000", request.Headers.Host);
        Assert.Equal("sid=S1", Assert.Single(request.Headers.GetValues("Cookie")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "{\"message\":\"u@x.com\"}", null)]
    [InlineData(HttpStatusCode.OK, "not json", null)]
    [InlineData(HttpStatusCode.OK, "[]", null)]
    [InlineData(HttpStatusCode.OK, "{\"message\":null}", null)]
    [InlineData(HttpStatusCode.OK, "{\"message\":\"Guest\"}", "Guest")]
    public async Task TheSessionCheckGivesNullForAnythingButAUserName(HttpStatusCode status, string body, string? expected)
    {
        using var http = new HttpClient(new StubHandler(_ => Json(body, status)));
        Assert.Equal(expected, await OpenAsService.CheckSessionAsync(http, new Uri("http://a.localhost:8000"), "S1", TimeSpan.FromSeconds(2), Ct));
    }

    [Fact]
    public async Task TheShimReturnsStdoutOfOpenAndUsersWithTheDefaultTimeout()
    {
        var runner = new FakeProcessRunner { Answer = (_, _) => new ProcessResult(0, "{\"ok\":true}", "") };
        var source = new ShimBenchSource(runner, () => @"C:\bin\benchbar.exe");

        Assert.Equal("{\"ok\":true}", await source.RunForOutputAsync(["open", "--site", "a+b"], Ct));
        Assert.Equal(["open", "--site", "a+b"], runner.Calls[0].Args);
        Assert.Equal(ShimBenchSource.ReadTimeout, runner.LastTimeout);
    }

    [Fact]
    public async Task AFailingOpenThrowsTheFirstStderrLineAndNeverQuotesStdout()
    {
        var runner = new FakeProcessRunner
        {
            Answer = (_, _) => new ProcessResult(1, "{\"url\":\"http://x/login?sid=SECRETSID\"}", "\nU is disabled on S.\nsecond\n"),
        };
        var source = new ShimBenchSource(runner, () => @"C:\bin\benchbar.exe");
        var ex = await Assert.ThrowsAsync<CliProblemException>(() => source.RunForOutputAsync(["open"], Ct));
        Assert.Equal("U is disabled on S.", ex.Message);

        runner.Answer = (_, _) => new ProcessResult(2, "{\"url\":\"SECRETSID\"}", "");
        ex = await Assert.ThrowsAsync<CliProblemException>(() => source.RunForOutputAsync(["open"], Ct));
        Assert.Equal("benchbar.exe exited with code 2.", ex.Message);
    }

    [Fact]
    public async Task AMissingShimIsASetupProblemForOpenToo()
    {
        var source = new ShimBenchSource(new FakeProcessRunner(), () => null);
        await Assert.ThrowsAsync<SetupProblemException>(() => source.RunForOutputAsync(["users"], Ct));
    }
}
