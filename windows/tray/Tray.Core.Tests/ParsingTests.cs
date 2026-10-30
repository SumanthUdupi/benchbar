using System.Text.Json;
using BenchBar.Tray.Core;
using Xunit;

namespace Tray.Core.Tests;

public class ParsingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void RecordedStatusParses()
    {
        var status = BenchJson.ParseStatus(File.ReadAllText(Path.Combine(Fixtures.Dir("recorded"), "status.json")));
        Assert.Equal("frappe-bench", status.Name);
        Assert.Equal("/home/akash/frappe-bench", status.Path);
        Assert.Equal(BenchState.Stopped, status.State);
        Assert.Equal("manual", status.StopReason);
        Assert.Equal("http://linuxdev.localhost:8000", status.WebUrl);
        Assert.Equal("linuxdev.localhost", status.Site);
    }

    [Fact]
    public void RecordedListParses()
    {
        var list = BenchJson.ParseList(File.ReadAllText(Path.Combine(Fixtures.Dir("recorded"), "list.json")));
        var bench = Assert.Single(list);
        Assert.Equal("frappe-bench", bench.Name);
        Assert.Equal("/home/akash/frappe-bench", bench.Path);
        Assert.True(bench.IsDefault);
        Assert.Equal("http://linuxdev.localhost:8000", bench.WebUrl);
    }

    [Theory]
    [InlineData("rest", "stopped", "stopped")]
    [InlineData("load", "running", "stopped")]
    [InlineData("crash", "paused", "stopped")]
    public async Task ScenarioFoldersReadThroughFixtureSource(string scenario, string first, string second)
    {
        var source = new FixtureBenchSource(Fixtures.Dir(scenario));
        var entries = await source.ListAsync(Ct);
        Assert.Equal(["frappe-bench", "v16-bench"], entries.Select(e => e.Name));
        Assert.True(entries[0].IsDefault);
        Assert.False(entries[1].IsDefault);

        var states = new List<BenchStatus>();
        foreach (var entry in entries) states.Add(await source.StatusAsync(entry, Ct));
        Assert.Equal(BenchJson.ParseState(first), states[0].State);
        Assert.Equal(BenchJson.ParseState(second), states[1].State);
        Assert.Equal("http://v16dev.localhost:8001", states[1].WebUrl);
    }

    [Fact]
    public async Task LoadFixtureReportsCpu()
    {
        Assert.Equal(250, new FixtureBenchSource(Fixtures.Dir("load")).CpuPercent);
        Assert.Null(new FixtureBenchSource(Fixtures.Dir("rest")).CpuPercent);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RecordedFolderServesItsSingleStatusForAnyBench()
    {
        var source = new FixtureBenchSource(Fixtures.Dir("recorded"));
        var entry = (await source.ListAsync(Ct)).Single();
        Assert.Equal(BenchState.Stopped, (await source.StatusAsync(entry, Ct)).State);
    }

    [Fact]
    public async Task FixtureSourceRereadsFilesOnEveryCall()
    {
        var dir = Fixtures.Copy("rest");
        var source = new FixtureBenchSource(dir);
        var entry = (await source.ListAsync(Ct))[0];
        Assert.Equal(BenchState.Stopped, (await source.StatusAsync(entry, Ct)).State);

        var path = Path.Combine(dir, "status-frappe-bench.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"state\":\"stopped\"", "\"state\":\"running\""));
        Assert.Equal(BenchState.Running, (await source.StatusAsync(entry, Ct)).State);
    }

    [Fact]
    public async Task FixtureUpAndDownRewriteNothingAndReturnZero()
    {
        var dir = Fixtures.Copy("rest");
        var before = File.ReadAllText(Path.Combine(dir, "status-frappe-bench.json"));
        var source = new FixtureBenchSource(dir);
        Assert.True((await source.RunAsync(["up", "--bench-dir", "/home/akash/frappe-bench"], Ct)).Ok);
        Assert.Equal(before, File.ReadAllText(Path.Combine(dir, "status-frappe-bench.json")));
        Assert.Equal(["up --bench-dir /home/akash/frappe-bench"], source.Actions);
    }

    [Theory]
    [InlineData("stopped", BenchState.Stopped)]
    [InlineData("starting", BenchState.Starting)]
    [InlineData("running", BenchState.Running)]
    [InlineData("crashed", BenchState.Crashed)]
    [InlineData("paused", BenchState.Paused)]
    [InlineData("hibernating", BenchState.Unknown)]
    [InlineData("", BenchState.Unknown)]
    [InlineData(null, BenchState.Unknown)]
    public void StateStringsMapAndUnknownIsUnknown(string? text, BenchState expected) =>
        Assert.Equal(expected, BenchJson.ParseState(text));

    [Fact]
    public void MissingAndExtraFieldsAreTolerated()
    {
        var status = BenchJson.ParseStatus("""{"bench":"/x/y-bench","state":"running","brand_new":{"a":[1,2]}}""");
        Assert.Equal("y-bench", status.Name);
        Assert.Equal(BenchState.Running, status.State);
        Assert.Null(status.WebUrl);
        Assert.Null(status.StopReason);
        Assert.Null(status.Site);

        Assert.Equal(BenchState.Unknown, BenchJson.ParseStatus("{}").State);
        Assert.Empty(BenchJson.ParseList("""{"schema_version":2}"""));
    }

    [Fact]
    public void DefaultBenchFallsBackToTheDefaultPath()
    {
        var list = BenchJson.ParseList("""{"default_bench":"/a","benches":[{"path":"/a","name":"a"},{"path":"/b","name":"b"}]}""");
        Assert.True(list[0].IsDefault);
        Assert.False(list[1].IsDefault);
    }

    [Fact]
    public void MalformedJsonThrowsJsonException()
    {
        Assert.ThrowsAny<JsonException>(() => BenchJson.ParseStatus("not json"));
        Assert.ThrowsAny<JsonException>(() => BenchJson.ParseList("null"));
    }

    [Fact]
    public void FactoryUsesFixturesWhenTheVariableIsSet()
    {
        var old = Environment.GetEnvironmentVariable(BenchSourceFactory.FixturesVariable);
        try
        {
            Environment.SetEnvironmentVariable(BenchSourceFactory.FixturesVariable, Fixtures.Dir("rest"));
            Assert.IsType<FixtureBenchSource>(BenchSourceFactory.FromEnvironment());
            Environment.SetEnvironmentVariable(BenchSourceFactory.FixturesVariable, null);
            Assert.IsType<ShimBenchSource>(BenchSourceFactory.FromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(BenchSourceFactory.FixturesVariable, old);
        }
    }

    [Fact]
    public void DeveloperModeParsesPerSiteAndAnOlderCliGivesFalse()
    {
        var status = BenchJson.ParseStatus(File.ReadAllText(Path.Combine(Fixtures.Dir("recorded"), "status.json")));
        Assert.Equal([true, false], status.Sites.Select(s => s.DeveloperMode));

        var old = BenchJson.ParseStatus("""{"bench":"/b","sites":[{"name":"a.localhost","default":true}]}""");
        Assert.False(Assert.Single(old.Sites).DeveloperMode);
    }

    [Fact]
    public void OpenAsFromTheCliWinsOverTheDefaultFlag()
    {
        // a hand-run `bench use` moves the site bench serve answers as; status still marks BenchBar's default
        var status = BenchJson.ParseStatus("""
            {"bench":"/b","sites":[
              {"name":"old.localhost","default":true,"developer_mode":true,"open_as":false},
              {"name":"new.localhost","default":false,"developer_mode":true,"open_as":true}]}
            """);
        Assert.Equal(["new.localhost"], status.Sites.Where(s => s.CanOpenAs).Select(s => s.Name));
        var old = BenchJson.ParseStatus("""{"bench":"/b","sites":[{"name":"a.localhost","default":true,"developer_mode":true}]}""");
        Assert.True(Assert.Single(old.Sites).CanOpenAs);
    }

    [Fact]
    public void OpenAsResultParsesTheRecordedDocument()
    {
        var result = BenchJson.ParseOpenAs(File.ReadAllText(Path.Combine(Fixtures.Dir("recorded"), "open-as.json")));
        Assert.Equal("linuxdev.localhost", result.Site);
        Assert.Equal("priya@example.com", result.User);
        Assert.Equal("Priya Nair", result.FullName);
        Assert.Equal("System User", result.UserType);
        Assert.Equal("/app", result.Path);
        Assert.StartsWith("http://linuxdev.localhost:8000/login?redirect-to=%2Fapp&sid=", result.Url);
    }

    [Fact]
    public void OpenAsWithoutAUrlHasNullUrlAndWithoutAUserIsMalformed()
    {
        Assert.Null(BenchJson.ParseOpenAs("""{"user":"a@b.c","path":"/"}""").Url);
        Assert.ThrowsAny<JsonException>(() => BenchJson.ParseOpenAs("{}"));
    }

    [Fact]
    public void UsersParseWithRolesAndTruncation()
    {
        var list = BenchJson.ParseUsers(File.ReadAllText(Path.Combine(Fixtures.Dir("recorded"), "users.json")));
        Assert.Equal("linuxdev.localhost", list.Site);
        Assert.False(list.Truncated);
        Assert.Equal(["Administrator", "priya@example.com", "visitor@example.com"], list.Users.Select(u => u.Name));
        var priya = list.Users[1];
        Assert.Equal(5, priya.Roles.Count);
        Assert.Equal(7, priya.RoleCount);
        Assert.Null(priya.LastLogin);
        Assert.Equal("Website User", list.Users[2].UserType);

        var cut = BenchJson.ParseUsers("""{"users":[{"name":"a"},{"full_name":"no name"}],"truncated":true}""");
        Assert.True(cut.Truncated);
        Assert.Equal("a", Assert.Single(cut.Users).Name);
    }

    [Fact]
    public async Task FixtureSourceAnswersOpenAndUsersFromItsFilesAndRefusesOtherCommands()
    {
        var source = new FixtureBenchSource(Fixtures.Dir("recorded"));
        Assert.Contains("\"user\"", await source.RunForOutputAsync(["open", "--as", "x"], Ct));
        Assert.Contains("\"users\"", await source.RunForOutputAsync(["users"], Ct));
        await Assert.ThrowsAsync<CliProblemException>(() => source.RunForOutputAsync(["doctor"], Ct));
    }
}
