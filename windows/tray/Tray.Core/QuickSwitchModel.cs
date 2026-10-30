namespace BenchBar.Tray.Core;

public enum QuickRowKind { SwitchBack, Pin, Administrator, Other }

/// <summary>One selectable line of the Quick Switch panel. <see cref="FullName"/> and <see cref="Label"/> only feed the filter.</summary>
public sealed record QuickRow(QuickRowKind Kind, string User, string Text, bool IsCurrent, string Label, string? FullName);

/// <summary>What Enter (or a digit) asked for: open <see cref="Site"/> as <see cref="User"/>.</summary>
public sealed record QuickSwitchChoice(string Site, string User);

/// <summary>What the model reads from the app, per site. <see cref="OpenAsService"/> and <see cref="TrayConfig"/> back these.</summary>
public sealed record QuickSwitchData(
    Func<string, IReadOnlyList<PinnedUser>> Pins,
    Func<string, string?> CurrentUser,
    Func<string, string?> PreviousUser,
    Func<string, string, string> LabelOf,
    Func<string, string, string?> FullNameOf)
{
    public static QuickSwitchData Empty { get; } = new(_ => [], _ => null, _ => null, (_, user) => user, (_, _) => null);
}

public enum QuickSwitchState { Ready, Problem, Loading, DistroStopped, BenchStarting, BenchStopped, NoDeveloperSite }

/// <summary>
/// The bench and sites the panel works on, or the reason it has none. <paramref name="Detail"/> is the line for a state that
/// needs words of its own: the snapshot's problem text, or which bench is starting.
/// </summary>
public sealed record QuickSwitchTarget(
    QuickSwitchState State, BenchStatus? Bench, IReadOnlyList<string> Sites, string? StartSite, string? Detail = null)
{
    public string? Message => State switch
    {
        QuickSwitchState.Problem => Detail,
        QuickSwitchState.Loading => QuickSwitchModel.LoadingText,
        QuickSwitchState.DistroStopped => QuickSwitchModel.DistroStoppedText,
        QuickSwitchState.BenchStarting => Detail ?? QuickSwitchModel.BenchStartingText,
        QuickSwitchState.BenchStopped => QuickSwitchModel.BenchStoppedText,
        QuickSwitchState.NoDeveloperSite => QuickSwitchModel.NoDeveloperSiteText,
        _ => null,
    };
}

/// <summary>
/// The Quick Switch panel as data: rows (Switch Back, the pins, Administrator), the filter, the selection, the
/// digits, site cycling and the search that adds "Other users". No window and no clock, so the rules are testable.
/// Same rules as the macOS panel.
/// </summary>
public sealed class QuickSwitchModel
{
    public const int SearchDebounceMs = 250;
    public const int SearchLimit = 8;
    public const int MinSearchChars = 2;
    public const string OtherUsersHeader = "Other users";
    public const string NoMatchText = "No users match";
    public const string CurrentMark = "✓";
    public const string CurrentHint = "current";
    public const string NoDeveloperSiteText = "No default site in developer mode. Open as works on a bench's default site only.";
    public const string BenchStoppedText = "Start the bench first (open BenchBar from the tray).";
    public const string BenchStartingText = "The bench is starting.";
    public const string LoadingText = "Loading…";
    public const string DistroStoppedText = DistroGuard.StoppedMessage;

    private readonly IReadOnlyList<string> _sites;
    private readonly QuickSwitchData _data;
    private List<QuickRow> _primary = [];
    private List<QuickRow> _others = [];
    private int _siteIndex;
    private bool _fired;

    public QuickSwitchModel(IReadOnlyList<string> sites, string? startSite, QuickSwitchData data)
    {
        if (sites.Count == 0) throw new ArgumentException("Quick Switch needs at least one site.", nameof(sites));
        _sites = sites;
        _data = data;
        _siteIndex = Math.Max(0, sites.ToList().IndexOf(startSite ?? ""));
        BuildPrimary();
    }

    public string Site => _sites[_siteIndex];

    public string Title => $"Open {Site} as…";

    public string Query { get; private set; } = "";

    public int Selected { get; private set; }

    /// <summary>True from the first Enter or digit until <see cref="Rearm"/>: a second one produces nothing.</summary>
    public bool Busy => _fired;

    public bool ShouldSearch => Query.Trim().Length >= MinSearchChars;

    public string SearchText => Query.Trim();

    /// <summary>The rows matching the query, then the searched "Other users".</summary>
    public IReadOnlyList<QuickRow> Rows => [.. FilteredPrimary(), .. _others];

    /// <summary>How many of <see cref="Rows"/> come before the "Other users" header.</summary>
    public int PrimaryCount => FilteredPrimary().Count;

    public QuickRow? SelectedRow
    {
        get
        {
            var rows = Rows;
            return Selected >= 0 && Selected < rows.Count ? rows[Selected] : null;
        }
    }

    /// <summary>
    /// Replaces the query: the first match is selected again and the searched users are dropped (they matched the old text).
    /// The default selection (no query) is row 0, which is Switch Back when it exists, else the first pin,
    /// so Ctrl+Alt+U then Enter toggles between the last two users.
    /// </summary>
    public void SetQuery(string text)
    {
        if (_fired) return;
        Query = text ?? "";
        _others = [];
        Selected = 0;
    }

    /// <summary>Adds the site-wide search results under "Other users": at most <see cref="SearchLimit"/>, none already shown above.</summary>
    public void SetOthers(IEnumerable<SiteUser> users)
    {
        var shown = FilteredPrimary().Select(r => r.User).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _others =
        [
            .. users
                .Where(u => !shown.Contains(u.Name))
                .Take(SearchLimit)
                .Select(u => new QuickRow(
                    QuickRowKind.Other,
                    u.Name,
                    u.FullName.Length > 0 ? $"{u.FullName} — {u.Name}" : u.Name,
                    IsCurrentUser(u.Name),
                    u.FullName.Length > 0 ? u.FullName : u.Name,
                    u.FullName.Length > 0 ? u.FullName : null)),
        ];
        Selected = Math.Clamp(Selected, 0, Math.Max(0, Rows.Count - 1));
    }

    /// <summary>Moves the selection, wrapping around at both ends.</summary>
    public void Move(int delta)
    {
        if (_fired) return;
        var count = Rows.Count;
        if (count == 0) return;
        Selected = ((Selected + delta) % count + count) % count;
    }

    /// <summary>Moves to the next developer-mode site; false when there is only one. The query stays, the rows are rebuilt.</summary>
    public bool NextSite()
    {
        if (_fired || _sites.Count < 2) return false;
        _siteIndex = (_siteIndex + 1) % _sites.Count;
        _others = [];
        Selected = 0;
        BuildPrimary();
        return true;
    }

    /// <summary>The switch for the selected row, or null when there is none or one is already under way.</summary>
    public QuickSwitchChoice? Confirm() => Fire(SelectedRow);

    /// <summary>True while a digit key picks a row: the trimmed query is empty. A digit typed into a real query is just text.</summary>
    public bool DigitsActive => Query.Trim().Length == 0;

    /// <summary>The switch for a digit key, only while <see cref="DigitsActive"/>.</summary>
    public QuickSwitchChoice? ConfirmDigit(int digit) => DigitsActive ? Fire(RowForDigit(digit)) : null;

    /// <summary>After a failed switch: Enter works again.</summary>
    public void Rearm() => _fired = false;

    /// <summary>
    /// Keys 1 to 9 are the pins in order, key 0 is Administrator, so a digit never moves when Switch Back appears.
    /// Switch Back and the searched users have no digit. Null when no row has that digit.
    /// </summary>
    public QuickRow? RowForDigit(int digit)
    {
        var rows = Rows;
        for (var i = 0; i < rows.Count; i++)
        {
            if (DigitLabel(i) == digit.ToString(System.Globalization.CultureInfo.InvariantCulture)) return rows[i];
        }
        return null;
    }

    /// <summary>The key shown beside row <paramref name="index"/> of <see cref="Rows"/>: the pin's place "1".."9", "0" for Administrator, else null.</summary>
    public string? DigitLabel(int index)
    {
        var rows = Rows;
        if (index < 0 || index >= rows.Count) return null;
        switch (rows[index].Kind)
        {
            case QuickRowKind.Administrator:
                return "0";
            case QuickRowKind.Pin:
                var place = rows.Take(index).Count(r => r.Kind == QuickRowKind.Pin) + 1;
                return place <= 9 ? place.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The bench and sites for the panel, or the one reason it has none. A problem line of the snapshot (shim missing,
    /// WSL silent) and a stopped distro come first; the snapshot that is still <see cref="TraySnapshot.Empty"/> is "Loading…"
    /// (nothing was polled yet). Only a bench's default site in developer mode is offered (<see cref="BenchStatus.OpenAsSites"/>).
    /// The bench of the last switch when it has one, else the first running bench with one, else the first with one
    /// (which is then stopped or starting). The start site is the last one switched on, else the first.
    /// </summary>
    public static QuickSwitchTarget Choose(TraySnapshot snapshot, (string BenchPath, string Site)? last)
    {
        if (snapshot.Problem is { Length: > 0 } problem) return new QuickSwitchTarget(QuickSwitchState.Problem, null, [], null, problem);
        if (snapshot.DistroStopped) return new QuickSwitchTarget(QuickSwitchState.DistroStopped, null, [], null);
        if (ReferenceEquals(snapshot, TraySnapshot.Empty)) return new QuickSwitchTarget(QuickSwitchState.Loading, null, [], null);

        var benches = snapshot.Benches;
        var candidates = benches.Where(b => b.OpenAsSites.Count > 0).ToList();
        if (candidates.Count == 0) return NoCandidate(benches);

        var bench = candidates.FirstOrDefault(b => last is { } l && b.Path == l.BenchPath)
                    ?? candidates.FirstOrDefault(b => b.State == BenchState.Running)
                    ?? candidates[0];
        var sites = bench.OpenAsSites;
        var start = last is { } l2 && bench.Path == l2.BenchPath && sites.Any(s => s.Name == l2.Site) ? l2.Site : sites[0].Name;
        var names = sites.Select(s => s.Name).ToList();
        return bench.State switch
        {
            BenchState.Running => new QuickSwitchTarget(QuickSwitchState.Ready, bench, names, start),
            BenchState.Starting => new QuickSwitchTarget(QuickSwitchState.BenchStarting, bench, names, start, $"{bench.Name} is starting."),
            _ => new QuickSwitchTarget(QuickSwitchState.BenchStopped, bench, names, start),
        };
    }

    private static QuickSwitchTarget NoCandidate(IReadOnlyList<BenchStatus> benches)
    {
        if (benches.Any(b => b.State == BenchState.Running)) return new QuickSwitchTarget(QuickSwitchState.NoDeveloperSite, null, [], null);
        if (benches.FirstOrDefault(b => b.State == BenchState.Starting) is { } starting)
            return new QuickSwitchTarget(QuickSwitchState.BenchStarting, null, [], null, $"{starting.Name} is starting.");
        return new QuickSwitchTarget(
            benches.Count > 0 ? QuickSwitchState.BenchStopped : QuickSwitchState.NoDeveloperSite, null, [], null);
    }

    private QuickSwitchChoice? Fire(QuickRow? row)
    {
        if (row is null || _fired) return null;
        _fired = true;
        return new QuickSwitchChoice(Site, row.User);
    }

    private List<QuickRow> FilteredPrimary()
    {
        var text = Query.Trim();
        if (text.Length == 0) return _primary;
        return
        [
            .. _primary.Where(r =>
                r.Label.Contains(text, StringComparison.OrdinalIgnoreCase)
                || r.User.Contains(text, StringComparison.OrdinalIgnoreCase)
                || (r.FullName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)),
        ];
    }

    private bool IsCurrentUser(string user) =>
        string.Equals(user, _data.CurrentUser(Site), StringComparison.OrdinalIgnoreCase);

    private void BuildPrimary()
    {
        var site = Site;
        var items = OpenAsMenuModel.Items(_data.Pins(site), _data.PreviousUser(site), user => _data.LabelOf(site, user));
        _primary =
        [
            .. items
                .Where(i => i.Kind is OpenAsItemKind.SwitchBack or OpenAsItemKind.Pin or OpenAsItemKind.Administrator)
                .Select(i => new QuickRow(
                    i.Kind switch
                    {
                        OpenAsItemKind.SwitchBack => QuickRowKind.SwitchBack,
                        OpenAsItemKind.Pin => QuickRowKind.Pin,
                        _ => QuickRowKind.Administrator,
                    },
                    i.User!,
                    i.Header,
                    IsCurrentUser(i.User!),
                    _data.LabelOf(site, i.User!),
                    _data.FullNameOf(site, i.User!))),
        ];
    }
}
