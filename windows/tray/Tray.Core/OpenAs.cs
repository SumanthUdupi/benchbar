using System.Globalization;
using System.Text.Json;

namespace BenchBar.Tray.Core;

/// <summary>
/// "Open as…": opens a local dev site signed in as another user. Session ids live in this object's memory and
/// nowhere else: never in config.json, a log, or any message this class returns. A session this service
/// created is reused (one cheap HTTP check) instead of asking the CLI for another, which takes seconds.
/// </summary>
public sealed class OpenAsService(
    IBenchSource source,
    Func<Uri, string, CancellationToken, Task<string?>> checkSession,
    TimeProvider time)
{
    public const int UserListLimit = 50;
    public const int RecentsKept = 2;
    public const string AdministratorId = "Administrator";
    public const string NotRunningMessage = "Start the bench first.";
    public const string UnsupportedMessage = "This benchbar does not support \"Open as\". Update the BenchBar CLI.";
    public const string UnreadableMessage = "benchbar answered with something the tray could not read.";

    // A class, not a record: a record's generated ToString would print the session id.
    private sealed class Session(string baseUrl, string sid, string landingPath)
    {
        public string BaseUrl { get; } = baseUrl;
        public string Sid { get; } = sid;
        public string LandingPath { get; } = landingPath;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = [];
    private readonly Dictionary<string, List<string>> _recents = [];
    private readonly Dictionary<string, string> _fullNames = [];
    private long _lastStamp;

    private static string UserKey(string benchPath, string site, string user) => TrayConfig.PinKey(benchPath, site) + "|" + user;

    /// <summary>
    /// Opens <paramref name="site"/> as <paramref name="user"/>: the cached session when it is still alive, else a new
    /// one from the CLI. The URL is for the browser only. A refusal comes back as <c>Error</c> (the CLI's own line).
    /// </summary>
    public async Task<(Uri? Url, string? Error)> SwitchAsync(
        BenchStatus bench, string site, string user, string? path, CancellationToken ct)
    {
        if (bench.State != BenchState.Running) return (null, NotRunningMessage);

        var key = UserKey(bench.Path, site, user);
        Session? cached;
        lock (_gate) _sessions.TryGetValue(key, out cached);
        if (cached is not null)
        {
            if (await IsAliveAsync(cached, user, ct).ConfigureAwait(false))
            {
                Remember(bench.Path, site, user);
                return (BuildLoginUrl(cached.BaseUrl, path ?? cached.LandingPath, cached.Sid, NextStamp()), null);
            }
            lock (_gate) _sessions.Remove(key);
        }

        List<string> args = ["open", "--bench-dir", bench.Path, "--site", site, "--as", user];
        if (!string.IsNullOrEmpty(path)) args.AddRange(["--path", path]);
        args.AddRange(["--print-url", "--json"]);
        try
        {
            var result = BenchJson.ParseOpenAs(await source.RunForOutputAsync([.. args], ct).ConfigureAwait(false));
            if (result.Url is null || !Uri.TryCreate(result.Url, UriKind.Absolute, out var url)) return (null, UnsupportedMessage);
            if (SidOf(url) is { } sid)
            {
                lock (_gate)
                {
                    _sessions[key] = new Session(url.GetLeftPart(UriPartial.Authority), sid, result.Path);
                    if (result.FullName.Length > 0) _fullNames[key] = result.FullName;
                }
            }
            Remember(bench.Path, site, user);
            return (url, null);
        }
        catch (SourceProblemException ex)
        {
            return (null, ex.Message);
        }
        catch (JsonException)
        {
            // the message of a JsonException can quote the document, which holds the session id
            return (null, UnreadableMessage);
        }
    }

    /// <summary>The enabled users of <paramref name="site"/> matching <paramref name="search"/> (id or full name), at most <paramref name="limit"/>.</summary>
    public async Task<(UserList? Users, string? Error)> FindUsersAsync(
        BenchStatus bench, string site, string? search, CancellationToken ct, int limit = UserListLimit)
    {
        if (bench.State != BenchState.Running) return (null, NotRunningMessage);

        List<string> args = ["users", "--bench-dir", bench.Path, "--site", site];
        if (!string.IsNullOrWhiteSpace(search)) args.AddRange(["--search", search.Trim()]);
        args.AddRange(["--limit", limit.ToString(CultureInfo.InvariantCulture), "--json"]);
        try
        {
            var list = BenchJson.ParseUsers(await source.RunForOutputAsync([.. args], ct).ConfigureAwait(false));
            lock (_gate)
            {
                foreach (var user in list.Users)
                {
                    if (user.FullName.Length > 0) _fullNames[UserKey(bench.Path, site, user.Name)] = user.FullName;
                }
            }
            return (list, null);
        }
        catch (SourceProblemException ex)
        {
            return (null, ex.Message);
        }
        catch (JsonException)
        {
            return (null, UnreadableMessage);
        }
    }

    /// <summary>The user the browser was last switched to on this site, or null.</summary>
    public string? CurrentUser(string benchPath, string site) => RecentAt(benchPath, site, 0);

    /// <summary>The user before <see cref="CurrentUser"/>: what "Switch Back" goes to.</summary>
    public string? PreviousUser(string benchPath, string site) => RecentAt(benchPath, site, 1);

    /// <summary>The full name this service has seen for <paramref name="user"/> on this site, or null.</summary>
    public string? FullNameOf(string benchPath, string site, string user)
    {
        lock (_gate) return _fullNames.TryGetValue(UserKey(benchPath, site, user), out var name) ? name : null;
    }

    /// <summary>The pin label, else the full name this service has seen, else the user id.</summary>
    public string LabelFor(string benchPath, string site, string user, IReadOnlyList<PinnedUser> pins)
    {
        if (pins.FirstOrDefault(p => p.User == user)?.Label is { Length: > 0 } label) return label;
        lock (_gate) return _fullNames.TryGetValue(UserKey(benchPath, site, user), out var name) ? name : user;
    }

    /// <summary>The row subtitle "as {label}": null when nothing was switched to on this site.</summary>
    public string? CurrentLabel(string benchPath, string site, IReadOnlyList<PinnedUser> pins) =>
        CurrentUser(benchPath, site) is { } user ? LabelFor(benchPath, site, user, pins) : null;

    /// <summary>
    /// <c>{base}/login?redirect-to=ENC&amp;sid=SID&amp;t=MS</c>. Frappe answers it with a 301 that sets the cookie, and a
    /// browser caches a 301, so <paramref name="ms"/> must differ on every call. Any <c>sid=</c> already in
    /// <paramref name="path"/> is dropped; the rest of the path is encoded once.
    /// </summary>
    public static Uri BuildLoginUrl(string baseUrl, string path, string sid, long ms)
    {
        var landing = WithoutSid(string.IsNullOrEmpty(path) ? "/" : path);
        return new Uri(
            $"{baseUrl.TrimEnd('/')}/login?redirect-to={Uri.EscapeDataString(landing)}" +
            $"&sid={Uri.EscapeDataString(sid)}&t={ms.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>The path with any <c>sid</c> query parameter removed; the other parameters and the fragment stay.</summary>
    internal static string WithoutSid(string path)
    {
        var hash = path.IndexOf('#');
        var fragment = hash >= 0 ? path[hash..] : "";
        var beforeFragment = hash >= 0 ? path[..hash] : path;
        var mark = beforeFragment.IndexOf('?');
        if (mark < 0) return path;
        var kept = beforeFragment[(mark + 1)..].Split('&')
            .Where(p => p != "sid" && !p.StartsWith("sid=", StringComparison.Ordinal));
        var query = string.Join('&', kept);
        return beforeFragment[..mark] + (query.Length > 0 ? "?" + query : "") + fragment;
    }

    /// <summary>The <c>frappe.auth.get_logged_user</c> check: the logged-in user of <paramref name="sid"/>, or null (Guest answers "Guest").</summary>
    /// <remarks>Asked of 127.0.0.1 with the site as the Host header, as frappe tells sites apart: a
    /// browser resolves <c>*.localhost</c> itself, Windows name resolution may not.</remarks>
    public static async Task<string?> CheckSessionAsync(
        HttpClient http, Uri baseUrl, string sid, TimeSpan timeout, CancellationToken ct)
    {
        var loopback = new UriBuilder(baseUrl) { Host = "127.0.0.1", Path = "/api/method/frappe.auth.get_logged_user" }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, loopback);
        request.Headers.Host = baseUrl.Authority;
        request.Headers.TryAddWithoutValidation("Cookie", "sid=" + sid);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        using var response = await http.SendAsync(request, limit.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("message", out var message)
                   && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<bool> IsAliveAsync(Session session, string user, CancellationToken ct)
    {
        try
        {
            var who = await checkSession(new Uri(session.BaseUrl), session.Sid, ct).ConfigureAwait(false);
            return string.Equals(who, user, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException or IOException) && !ct.IsCancellationRequested)
        {
            // unreachable site or the 2 s limit: treat as dead, the CLI will say what is wrong
            return false;
        }
    }

    private long NextStamp()
    {
        lock (_gate)
        {
            _lastStamp = Math.Max(time.GetUtcNow().ToUnixTimeMilliseconds(), _lastStamp + 1);
            return _lastStamp;
        }
    }

    private void Remember(string benchPath, string site, string user)
    {
        lock (_gate)
        {
            var key = TrayConfig.PinKey(benchPath, site);
            if (!_recents.TryGetValue(key, out var recents)) _recents[key] = recents = [];
            recents.Remove(user);
            recents.Insert(0, user);
            if (recents.Count > RecentsKept) recents.RemoveRange(RecentsKept, recents.Count - RecentsKept);
        }
    }

    private string? RecentAt(string benchPath, string site, int index)
    {
        lock (_gate)
        {
            return _recents.TryGetValue(TrayConfig.PinKey(benchPath, site), out var recents) && recents.Count > index ? recents[index] : null;
        }
    }

    private static string? SidOf(Uri url)
    {
        foreach (var part in url.Query.TrimStart('?').Split('&'))
        {
            if (part.StartsWith("sid=", StringComparison.Ordinal) && part.Length > 4) return Uri.UnescapeDataString(part[4..]);
        }
        return null;
    }
}

public enum OpenAsItemKind { SwitchBack, Pin, Administrator, FindUser, Separator }

/// <summary>One line of the "Open as…" menu. <see cref="User"/> is set for every kind that switches.</summary>
public sealed record OpenAsItem(OpenAsItemKind Kind, string Header, string? User = null);

/// <summary>The "Open as…" menu as data, so its order and wording are testable without a window.</summary>
public static class OpenAsMenuModel
{
    public const string Title = "Open as…";
    public const string FindUserHeader = "Find User…";

    /// <summary>
    /// Switch Back (only with a previous user), a separator, the pins, Administrator, a separator, Find User….
    /// <paramref name="labelOf"/> is <see cref="OpenAsService.LabelFor"/>.
    /// </summary>
    public static IReadOnlyList<OpenAsItem> Items(IReadOnlyList<PinnedUser> pins, string? previousUser, Func<string, string> labelOf)
    {
        var items = new List<OpenAsItem>();
        if (previousUser is not null)
        {
            items.Add(new OpenAsItem(OpenAsItemKind.SwitchBack, "Switch Back to " + labelOf(previousUser), previousUser));
            items.Add(new OpenAsItem(OpenAsItemKind.Separator, ""));
        }
        foreach (var pin in pins)
        {
            var label = labelOf(pin.User);
            items.Add(new OpenAsItem(OpenAsItemKind.Pin, label == pin.User ? pin.User : $"{label} — {pin.User}", pin.User));
        }
        items.Add(new OpenAsItem(OpenAsItemKind.Administrator, OpenAsService.AdministratorId, OpenAsService.AdministratorId));
        items.Add(new OpenAsItem(OpenAsItemKind.Separator, ""));
        items.Add(new OpenAsItem(OpenAsItemKind.FindUser, FindUserHeader));
        return items;
    }
}
