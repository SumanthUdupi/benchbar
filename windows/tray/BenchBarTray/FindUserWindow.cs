using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BenchBar.Tray.Core;

namespace BenchBar.Tray;

/// <summary>What the Find User window does. Each member is one call into the app, so the window works alone in tests.</summary>
internal sealed class FindUserActions
{
    /// <summary><c>users --search</c>: the users matching the text (null or empty is everyone, up to the limit), or the error line.</summary>
    public Func<string?, CancellationToken, Task<(UserList? Users, string? Error)>> Find { get; set; } =
        (_, _) => Task.FromResult<(UserList?, string?)>((new UserList("", [], false), null));

    /// <summary>Opens the site as this user; the error line, or null when it opened.</summary>
    public Func<string, Task<string?>> Open { get; set; } = _ => Task.FromResult<string?>(null);

    public Func<IReadOnlyList<PinnedUser>> Pins { get; set; } = () => [];

    /// <summary>Pins the user with the label (or relabels a pinned one).</summary>
    public Func<string, string?, PinOutcome> Pin { get; set; } = (_, _) => PinOutcome.Saved;

    /// <summary>Unpins the user; false when config.json could not be written.</summary>
    public Func<string, bool> Unpin { get; set; } = _ => true;
}

/// <summary>What pinning did: kept and written, refused (the site has 9 other pins), or kept in memory only because config.json could not be written.</summary>
internal enum PinOutcome { Saved, NoRoom, NotSaved }

/// <summary>
/// Search every enabled user of one site, open the site as one of them, and pin the ones used often. The search
/// waits <see cref="DebounceMs"/> after the last keystroke so typing does not start a CLI call per letter.
/// </summary>
internal sealed class FindUserWindow : ToolWindow
{
    public const int DebounceMs = 250;
    public const string EmptyText = "No users match";
    public const string TruncatedText = "Showing the first 50; refine the search";
    public const string PinLimitText = "A site can pin at most 9 users.";
    public const string PinSaveFailedText = "Could not save your pins to config.json; they will be gone after a restart.";
    public const string UnpinSaveFailedText = "Could not save your pins to config.json; this user will be back after a restart.";
    public const string LabelHint = "Label, e.g. NE";

    private static readonly Brush ErrorBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)));

    private readonly FindUserActions _actions;
    private readonly TextBox _search = new() { Margin = new Thickness(16, 14, 16, 8), Padding = new Thickness(6, 4, 6, 4) };
    private readonly StackPanel _list = new();
    private readonly TextBlock _footer = new() { Margin = new Thickness(16, 6, 16, 10), TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
    private readonly DispatcherTimer _debounce = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
    private bool _closed;

    public FindUserWindow(string site, FindUserActions actions, ResolvedTheme theme)
        : base($"Open {site} as…", 560, 520, theme)
    {
        _actions = actions;
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search users");
        _search.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = SearchAsync();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _debounce.Stop();
        };
        Loaded += (_, _) =>
        {
            _search.Focus();
            _ = SearchAsync();
        };

        var root = new DockPanel();
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(_footer, Dock.Bottom);
        root.Children.Add(_search);
        root.Children.Add(_footer);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list, Margin = new Thickness(8, 0, 8, 0) });
        Content = root;
    }

    /// <summary>How many user rows the list shows now.</summary>
    internal int RowCount { get; private set; }

    /// <summary>True when the list shows "No users match" instead of rows.</summary>
    internal bool ShowsEmptyState { get; private set; }

    internal string FooterText => _footer.Text;

    internal bool FooterIsError { get; private set; }

    /// <summary>Runs the search for the text in the box now; a newer search cancels the one still running.</summary>
    internal async Task SearchAsync()
    {
        var ct = BeginLoad();
        SetFooter("Loading...", error: false);
        try
        {
            var (users, error) = await _actions.Find(_search.Text, ct);
            if (ct.IsCancellationRequested) return;
            if (users is null)
            {
                ShowRows([]);
                SetFooter(error ?? "The users could not be listed.", error: true);
                return;
            }
            ShowRows(users.Users);
            SetFooter(users.Truncated ? TruncatedText : "", error: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowRows([]);
            SetFooter(ex.Message, error: true);
        }
    }

    private void ShowRows(IReadOnlyList<SiteUser> users)
    {
        _list.Children.Clear();
        RowCount = users.Count;
        ShowsEmptyState = users.Count == 0;
        if (users.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = EmptyText, Opacity = 0.7, Margin = new Thickness(12, 16, 12, 8) });
            return;
        }
        var pins = _actions.Pins();
        foreach (var user in users) _list.Children.Add(UserRow(user, pins.FirstOrDefault(p => TrayConfig.SameUser(p.User, user.Name))));
    }

    private void SetFooter(string text, bool error)
    {
        _footer.Text = text;
        if (error) _footer.Foreground = ErrorBrush;
        else _footer.ClearValue(TextBlock.ForegroundProperty); // back to the theme's text colour
        _footer.Opacity = error ? 1 : 0.7;
        FooterIsError = error;
    }

    private Border UserRow(SiteUser user, PinnedUser? pinned)
    {
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = user.FullName.Length > 0 ? user.FullName : user.Name,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        info.Children.Add(new TextBlock { Text = $"{user.Name} · {user.UserType}", FontSize = 12, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
        if (user.Roles.Count > 0)
        {
            var more = user.RoleCount > user.Roles.Count ? $" +{user.RoleCount - user.Roles.Count} more" : "";
            info.Children.Add(new TextBlock { Text = string.Join(", ", user.Roles) + more, FontSize = 11, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis });
        }

        var labelBox = new TextBox { Text = pinned?.Label ?? "", MinWidth = 120, VerticalContentAlignment = VerticalAlignment.Center };
        var hint = new TextBlock
        {
            Text = LabelHint,
            Opacity = 0.5,
            IsHitTestVisible = false,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = labelBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        var labelHost = new Grid { Visibility = pinned is null ? Visibility.Collapsed : Visibility.Visible, Margin = new Thickness(8, 0, 0, 0) };
        labelHost.Children.Add(labelBox);
        labelHost.Children.Add(hint);
        labelBox.TextChanged += (_, _) => hint.Visibility = labelBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var pin = new ToggleButton { Content = pinned is null ? "Pin" : "Pinned", IsChecked = pinned is not null, MinWidth = 64, Margin = new Thickness(8, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(pin, $"Pin {user.Name}");
        pin.Click += (_, _) =>
        {
            if (pin.IsChecked == true)
            {
                var outcome = _actions.Pin(user.Name, labelBox.Text);
                if (outcome == PinOutcome.NoRoom)
                {
                    pin.IsChecked = false;
                    SetFooter(PinLimitText, error: true);
                    return;
                }
                if (outcome == PinOutcome.NotSaved) SetFooter(PinSaveFailedText, error: true);
                pin.Content = "Pinned";
                labelHost.Visibility = Visibility.Visible;
                labelBox.Focus();
            }
            else
            {
                if (!_actions.Unpin(user.Name)) SetFooter(UnpinSaveFailedText, error: true);
                pin.Content = "Pin";
                labelHost.Visibility = Visibility.Collapsed;
            }
        };
        void CommitLabel()
        {
            if (pin.IsChecked == true && _actions.Pin(user.Name, labelBox.Text) == PinOutcome.NotSaved)
                SetFooter(PinSaveFailedText, error: true);
        }
        labelBox.LostKeyboardFocus += (_, _) => CommitLabel();
        labelBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitLabel();
            e.Handled = true;
        };

        var open = new Button { Content = "Open", MinWidth = 64, Margin = new Thickness(8, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(open, $"Open as {user.Name}");
        open.Click += async (_, _) =>
        {
            open.IsEnabled = false;
            string? error;
            try { error = await _actions.Open(user.Name); }
            catch (Exception ex) { error = ex.Message; }
            if (_closed) return;
            if (error is null)
            {
                Close();
                return;
            }
            SetFooter(error, error: true);
            open.IsEnabled = true;
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(labelHost, 1);
        Grid.SetColumn(pin, 2);
        Grid.SetColumn(open, 3);
        grid.Children.Add(info);
        grid.Children.Add(labelHost);
        grid.Children.Add(pin);
        grid.Children.Add(open);

        return new Border { Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 2), Child = grid };
    }

    private static Brush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
