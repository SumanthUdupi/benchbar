using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BenchBar.Tray.Core;

namespace BenchBar.Tray;

/// <summary>What the Quick Switch panel does. Each member is one call into the app, so the window works alone in tests.</summary>
internal sealed class QuickSwitchActions
{
    /// <summary><c>users --search</c> on a site, at most <see cref="QuickSwitchModel.SearchLimit"/> rows, or the error line.</summary>
    public Func<string, string, CancellationToken, Task<(UserList? Users, string? Error)>> Find { get; set; } =
        (_, _, _) => Task.FromResult<(UserList?, string?)>((new UserList("", [], false), null));

    /// <summary>Opens the site as the user; the error line, or null when it opened.</summary>
    public Func<string, string, Task<string?>> Switch { get; set; } = (_, _) => Task.FromResult<string?>(null);
}

/// <summary>
/// The Ctrl+Alt+U panel: a borderless topmost list on the monitor under the cursor. Keys: Up/Down, Enter, Esc, Tab (next
/// site), 1-9 and 0 while the box is empty; typing filters, 2+ characters also search the site's users. Losing focus
/// closes it. All rules are in <see cref="QuickSwitchModel"/>; this class draws them and routes keys.
/// </summary>
internal sealed class QuickSwitchWindow : Window
{
    private static readonly Brush ErrorBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)));
    private static readonly Brush SelectionBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0x3B, 0x82, 0xF6)));
    private static readonly Brush EdgeBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)));

    private const double PanelWidth = 460;
    private const double PanelMaxHeight = 560;
    private const double RowHeight = 34;
    private const double ChromeHeight = 110;

    private readonly QuickSwitchTarget _target;
    private readonly QuickSwitchModel? _model;
    private readonly QuickSwitchActions _actions;
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(16, 12, 16, 4) };
    private readonly TextBox _search = new() { Margin = new Thickness(12, 0, 12, 6), Padding = new Thickness(6, 4, 6, 4) };
    private readonly StackPanel _list = new();
    private readonly TextBlock _message = new() { Margin = new Thickness(16, 6, 16, 10), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly DispatcherTimer _debounce = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(QuickSwitchModel.SearchDebounceMs),
    };
    private readonly List<string> _rowTexts = [];
    private CancellationTokenSource? _searchCts;
    private string? _error;
    private bool _busy;
    private bool _searching;
    private bool _closed;

    public QuickSwitchWindow(QuickSwitchTarget target, QuickSwitchData data, QuickSwitchActions actions, ResolvedTheme theme)
    {
        _target = target;
        _actions = actions;
        if (target.State == QuickSwitchState.Ready) _model = new QuickSwitchModel(target.Sites, target.StartSite, data);

        Title = "BenchBar Quick Switch";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = PanelWidth;
        MaxHeight = PanelMaxHeight;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ThemeMode = theme.FlyoutLight ? ThemeMode.Light : ThemeMode.Dark;

        System.Windows.Automation.AutomationProperties.SetName(_search, "Search users");
        _search.TextChanged += (_, _) => OnTextChanged();
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = SearchAsync();
        };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) =>
        {
            if (!_busy) Close();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _debounce.Stop();
            _searchCts?.Cancel();
        };
        Loaded += (_, _) =>
        {
            Activate();
            _search.Focus();
        };

        var root = new DockPanel();
        DockPanel.SetDock(_title, Dock.Top);
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(_message, Dock.Bottom);
        root.Children.Add(_title);
        root.Children.Add(_search);
        root.Children.Add(_message);
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _list,
            Margin = new Thickness(6, 0, 6, 0),
        });
        Content = new Border { BorderBrush = EdgeBrush, BorderThickness = new Thickness(1), Child = root };
        Render();
    }

    /// <summary>The text of every selectable row now shown, in order (the "Other users" header is not a row).</summary>
    internal IReadOnlyList<string> RowTexts => _rowTexts;

    internal int SelectedIndex => _model?.Selected ?? -1;

    internal string TitleText => _title.Text;

    internal string MessageText => _message.Text;

    internal bool MessageIsError { get; private set; }

    internal bool SearchBoxVisible => _search.Visibility == Visibility.Visible;

    /// <summary>Shows the panel centred on the monitor under the cursor and takes the keyboard.</summary>
    public void ShowCentered()
    {
        var point = NativeMethods.GetCursorPos(out var cursor) ? cursor : default;
        var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        var known = monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info)
                    && info.Work.Right > info.Work.Left && info.Work.Bottom > info.Work.Top;
        if (known)
        {
            uint dpi = 96;
            if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0) dpi = dpiX;
            var scale = dpi / 96.0;
            var heightDip = Math.Min(PanelMaxHeight, ChromeHeight + RowHeight * Math.Max(1, _rowTexts.Count));
            var workWidth = info.Work.Right - info.Work.Left;
            var workHeight = info.Work.Bottom - info.Work.Top;
            Left = (info.Work.Left + (workWidth - PanelWidth * scale) / 2) * 96.0 / dpi;
            Top = (info.Work.Top + (workHeight - heightDip * scale) / 2) * 96.0 / dpi;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        Show();
        Activate();
    }

    /// <summary>Runs the search for the text in the box now; a newer search cancels the one still running.</summary>
    internal async Task SearchAsync()
    {
        if (_model is null || !_model.ShouldSearch) return;
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var ct = cts.Token;
        var (site, query) = (_model.Site, _model.SearchText);
        _searching = true;
        Render();
        try
        {
            var (users, error) = await _actions.Find(site, query, ct);
            if (ct.IsCancellationRequested || _closed) return;
            _searching = false;
            if (users is null)
            {
                _error = error ?? "The users could not be listed.";
            }
            else
            {
                _model.SetOthers(users.Users);
            }
            Render();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || _closed) return;
            _searching = false;
            _error = ex.Message;
            Render();
        }
    }

    /// <summary>Types into the search box, as the keyboard would.</summary>
    internal void SetSearchText(string text) => _search.Text = text;

    /// <summary>The key handling, callable without a keyboard. Returns true when the key was used.</summary>
    internal bool HandleKey(Key key, bool shift)
    {
        if (key == Key.Escape)
        {
            Close();
            return true;
        }
        if (_model is null) return false;
        if (_busy) return true;

        switch (key)
        {
            case Key.Down:
            case Key.Up:
                _model.Move(key == Key.Down ? 1 : -1);
                Render();
                return true;
            case Key.Tab:
                if (_model.NextSite())
                {
                    CancelSearch();
                    Render();
                    if (_model.ShouldSearch) _debounce.Start();
                }
                return true;
            case Key.Enter: // Key.Return is the same value
                _ = ConfirmAsync(_model.Confirm());
                return true;
        }
        if (!shift && _search.Text.Length == 0 && DigitOf(key) is { } digit)
        {
            _ = ConfirmAsync(_model.ConfirmDigit(digit));
            return true;
        }
        return false;
    }

    internal static int? DigitOf(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => key - Key.D0,
        >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
        _ => null,
    };

    private void OnKey(object sender, KeyEventArgs e)
    {
        // Alt is usually still down right after Ctrl+Alt+U, which WPF reports as Key.System
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (HandleKey(key, (Keyboard.Modifiers & ModifierKeys.Shift) != 0)) e.Handled = true;
    }

    private async Task ConfirmAsync(QuickSwitchChoice? choice)
    {
        if (choice is null || _model is null) return;
        _busy = true;
        _search.IsReadOnly = true;
        _error = null;
        Render();
        string? error;
        try
        {
            error = await _actions.Switch(choice.Site, choice.User);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        if (_closed) return;
        _busy = false;
        _search.IsReadOnly = false;
        if (error is null)
        {
            Close();
            return;
        }
        _model.Rearm();
        _error = error;
        if (!IsActive)
        {
            Close();
            return;
        }
        Render();
    }

    private void OnTextChanged()
    {
        if (_model is null) return;
        CancelSearch();
        _error = null;
        _model.SetQuery(_search.Text);
        Render();
        if (_model.ShouldSearch) _debounce.Start();
    }

    private void CancelSearch()
    {
        _debounce.Stop();
        _searchCts?.Cancel();
        _searching = false;
    }

    private void Render()
    {
        _list.Children.Clear();
        _rowTexts.Clear();

        if (_model is null)
        {
            _title.Text = "Quick Switch";
            _search.Visibility = Visibility.Collapsed;
            SetMessage(_target.Message ?? "", error: false);
            return;
        }

        _title.Text = _model.Title;
        var rows = _model.Rows;
        var primary = _model.PrimaryCount;
        var showDigits = _model.Query.Length == 0;
        FrameworkElement? selected = null;
        for (var i = 0; i < rows.Count; i++)
        {
            if (i == primary) _list.Children.Add(Header(QuickSwitchModel.OtherUsersHeader));
            var row = RowElement(rows[i], showDigits ? QuickSwitchModel.DigitLabel(i) : null, i == _model.Selected);
            if (i == _model.Selected) selected = row;
            _rowTexts.Add(rows[i].Text);
            _list.Children.Add(row);
        }
        if (rows.Count == 0 && _model.Query.Trim().Length > 0 && !_searching)
            _list.Children.Add(new TextBlock { Text = QuickSwitchModel.NoMatchText, Opacity = 0.7, Margin = new Thickness(12, 10, 12, 8) });
        selected?.BringIntoView();

        if (_error is not null) SetMessage(_error, error: true);
        else if (_searching) SetMessage("Searching…", error: false);
        else SetMessage("", error: false);
    }

    private TextBlock Header(string text) =>
        new() { Text = text, FontSize = 12, Opacity = 0.6, Margin = new Thickness(10, 8, 10, 2) };

    private Border RowElement(QuickRow row, string? digit, bool selected)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var key = new TextBlock { Text = digit ?? "", Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Text = row.Text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var detail = selected && _busy
            ? "Switching…"
            : row.IsCurrent ? $"{QuickSwitchModel.CurrentMark} {QuickSwitchModel.CurrentHint}" : "";
        var hint = new TextBlock { Text = detail, FontSize = 12, Opacity = 0.7, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(hint, 2);
        grid.Children.Add(key);
        grid.Children.Add(text);
        grid.Children.Add(hint);

        return new Border
        {
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(4),
            Background = selected ? SelectionBrush : Brushes.Transparent,
            Child = grid,
        };
    }

    private void SetMessage(string text, bool error)
    {
        _message.Text = text;
        MessageIsError = error;
        if (error) _message.Foreground = ErrorBrush;
        else _message.ClearValue(TextBlock.ForegroundProperty);
        _message.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Brush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
