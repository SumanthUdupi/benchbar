using System.Diagnostics;
using System.Windows;
using BenchBar.Tray.Core;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using H.NotifyIcon.EfficiencyMode;

namespace BenchBar.Tray;

/// <summary>
/// The tray app: no window at startup, a TaskbarIcon created in code, the shared <see cref="TrayController"/> behind
/// it, and a flyout, Logs and Doctor windows created on first use.
/// </summary>
public partial class App : Application, ITrayHost
{
    private const string MutexName = @"Local\BenchBarTray";
    private const string OpenEventName = @"Local\BenchBarTray.Open";
    private const string HelpUrl = "https://benchbar.akashmishra.com/windows/";
    private const string BrowserFailedMessage = "Could not open your browser. Check that Windows has a default browser, then try again.";
    private static readonly TimeSpan SessionCheckTimeout = TimeSpan.FromSeconds(2);

    private readonly ErrorLog _log = new();
    private readonly StartupShortcut _startup = new();
    private readonly Dictionary<string, LogsWindow> _logs = [];
    private readonly Dictionary<string, DoctorWindow> _doctors = [];
    private readonly Dictionary<string, FindUserWindow> _findUsers = [];
    private Mutex? _mutex;
    private EventWaitHandle? _openEvent;
    private RegisteredWaitHandle? _openWait;
    private TaskbarIcon? _icon;
    private TrayController? _controller;
    private TrayConfig _config = TrayConfig.Load();
    private HarnessLink? _link;
    private FlyoutWindow? _flyout;
    private TrayMenu? _menu;
    private IBenchSource? _source;
    private IDistroProbe? _probe;
    private HttpClient? _openAsHttp;
    private OpenAsService? _openAsService;
    private HotKey? _hotKey;
    private bool _quickSwitchTaken;
    private QuickSwitchWindow? _quickSwitch;
    private (string BenchPath, string Site)? _lastQuickSwitch;
    private static readonly TimeSpan FlyoutKeepAlive = TimeSpan.FromSeconds(30);
    private readonly System.Windows.Threading.DispatcherTimer _releaseFlyout = new(System.Windows.Threading.DispatcherPriority.Background) { Interval = FlyoutKeepAlive };
    private long _flyoutHiddenAt;
    private int _awake;
    private bool _quitting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Software rendering: the flyout is plain 2D, and a Direct3D device (with the GPU driver behind it)
        // stayed in memory after the first open, 62.6 MB private instead of 17 MB at rest.
        if (Environment.GetEnvironmentVariable("BENCHBAR_TRAY_GPU") != "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        DispatcherUnhandledException += (_, args) =>
        {
            _log.Write("ui", args.Exception);
            args.Handled = true; // keep running
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) _log.Write("domain", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.Write("task", args.Exception);
            args.SetObserved();
        };

        if (!TakeSingleInstance())
        {
            Shutdown();
            return;
        }

        // Created first: `hello` is queued until the pipe is up, so nothing sent below is lost.
        _link = HarnessLink.FromEnvironment("BenchBarTray");
        if (_link is not null) _link.Command += cmd => Dispatcher.BeginInvoke(() => OnHarnessCommand(cmd));

        _config = TrayConfig.Load();
        ApplyThemeMode();
        _source = BenchSourceFactory.FromEnvironment();
        var options = new TrayOptions
        {
            Runners = RunnerSet.Load(),
            RunnerId = _config.RunnerId,
            Appearance = _config.Appearance,
            Notifications = _config.Notifications,
            DistroProbe = _probe = BenchSourceFactory.DistroProbeFromEnvironment(),
            Distro = () => DistroName.Resolve(_config.Distro),
        };
        // The controller and the first frame exist before NIM_ADD, so "icon-added" means a runner is visible.
        _controller = new TrayController(_source, new DispatcherFrameClock(), this, TimeProvider.System, options);
        _icon = new TaskbarIcon { ToolTipText = "BenchBar", NoLeftClickDelay = true };
        _icon.TrayLeftMouseUp += (_, _) => ToggleFlyout();
        // The menu is built on the first right click: built at startup it loaded WPF's render stack
        // (wpfgfx, d3d9, the GPU driver) and doubled private bytes at rest.
        _icon.TrayRightMouseDown += (_, _) => EnsureMenu();
        _icon.TrayKeyboardContextMenu += (_, _) => EnsureMenu();
        _icon.PreviewTrayContextMenuOpen += (_, _) => EnsureMenu();
        _icon.TrayBalloonTipClicked += (_, _) => ShowFlyout();

        // NIM_ADD succeeded when the core TrayIcon raises Created; IsCreated is the fallback.
        var added = false;
        void IconAdded()
        {
            if (added) return;
            added = true;
            _link?.Send($"icon-added qpc={HarnessLink.Qpc()}");
            LogonLog.IconAdded("BenchBarTray");
        }
        _icon.TrayIcon.Created += (_, _) => IconAdded();
        _icon.TrayIcon.UpdateIcon(_controller.Icons.Get(RunnerPose.Unknown, 0));
        _icon.ForceCreate(); // puts the process in efficiency mode; the windows leave it while open
        if (_icon.IsCreated) IconAdded();

        _releaseFlyout.Tick += (_, _) => ReleaseFlyout();
        _controller.Start();
        ApplyQuickSwitch();
    }

    // --- single instance ---

    private bool TakeSingleInstance()
    {
        _mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // ask the running one to open its flyout; quietly nothing if it cannot be reached
            try
            {
                using var existing = EventWaitHandle.OpenExisting(OpenEventName);
                existing.Set();
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException) { }
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        _openEvent = new EventWaitHandle(false, EventResetMode.AutoReset, OpenEventName);
        _openWait = ThreadPool.RegisterWaitForSingleObject(_openEvent, (_, _) => Dispatcher.BeginInvoke(ShowFlyout), null, Timeout.Infinite, executeOnlyOnce: false);
        return true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _openWait?.Unregister(null);
        _openEvent?.Dispose();
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }

    // --- ITrayHost ---

    public void SetIcon(IntPtr hicon) => _icon?.TrayIcon.UpdateIcon(hicon);

    public void SetTooltip(string text)
    {
        if (_icon is not null) _icon.ToolTipText = text;
    }

    public void Post(Action action) => Dispatcher.BeginInvoke(action);

    public void BenchesChanged(TraySnapshot snapshot, string upText) => _flyout?.View.SetSnapshot(snapshot, upText);

    public void ShowToast(Toast toast)
    {
        try
        {
            _icon?.TrayIcon.ShowNotification(toast.Title, toast.Body, NotificationIcon.Warning);
        }
        catch (Exception ex)
        {
            _log.Write("toast", ex);
        }
    }

    // --- efficiency mode ---

    /// <summary>The icon alone runs in efficiency mode; any open window of ours leaves it until the last one closes.</summary>
    private void Awake(bool open)
    {
        _awake = Math.Max(0, _awake + (open ? 1 : -1));
        if (open && _awake != 1) return;
        if (!open && _awake != 0) return;
        try { EfficiencyModeUtilities.SetEfficiencyMode(!open); }
        catch (Exception ex) { _log.Write("efficiency", ex); }
    }

    // --- flyout ---

    private void ShowFlyout()
    {
        if (_quitting || _controller is null) return;
        if (_flyout is null)
        {
            var view = new FlyoutView(BuildFlyoutActions());
            _flyout = new FlyoutWindow(_controller, view, _link);
            _flyout.Opened += () => Awake(true);
            _flyout.Hidden += () =>
            {
                _flyoutHiddenAt = Environment.TickCount64;
                Awake(false);
                _releaseFlyout.Stop();
                _releaseFlyout.Start();
            };
            view.SetSnapshot(_controller.Snapshot, TrayController.UpText(_controller.Snapshot, _controller.Benches.Select(b => b.State).ToList()));
        }
        _flyout.ShowAtTray();
    }

    /// <summary>
    /// A flyout hidden for <see cref="FlyoutKeepAlive"/> is closed and its memory handed back, so the tray
    /// returns to its at rest size; a click within that time reopens the warm window.
    /// </summary>
    private void ReleaseFlyout()
    {
        _releaseFlyout.Stop();
        if (_flyout is null || _flyout.IsVisible) return;
        _flyout.Close();
        _flyout = null;
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    /// <summary>
    /// A click on the tray icon deactivates the open flyout first (it hides itself), so
    /// the click that follows within a moment means "close", not "open again".
    /// </summary>
    private void ToggleFlyout()
    {
        if (_flyout is { IsVisible: true }) { _flyout.HideFlyout(); return; }
        if (Environment.TickCount64 - _flyoutHiddenAt < 300) return;
        ShowFlyout();
    }

    private void OnHarnessCommand(string command)
    {
        switch (command)
        {
            case "open-flyout": ShowFlyout(); break;
            case "hide-flyout": _flyout?.HideFlyout(); break;
            case "quit": Quit(); break;
        }
    }

    private FlyoutActions BuildFlyoutActions() => new()
    {
        StartStop = bench => _controller!.StartStopAsync(bench),
        Restart = bench => _controller!.RestartAsync(bench),
        OpenSite = bench => _controller!.OpenSite(bench),
        ShowLogs = ShowLogs,
        ShowDoctor = ShowDoctor,
        CopyPath = bench => CopyText(bench.Path),
        OpenVsCode = OpenVsCode,
        VsCodeAvailable = () => VsCode.Find() is not null,
        StartDistro = () => _controller!.StartDistro(),
        CopyText = CopyText,
        OpenUrl = url => OpenUrl(url),
        OpenAsItems = OpenAsItemsFor,
        OpenAsLabel = (bench, site) => OpenAsSvc?.CurrentLabel(bench.Path, site, _config.PinsFor(bench.Path, site)),
        OpenAs = (bench, site, user) => SwitchUserAsync(bench, site, user, CancellationToken.None),
        FindUser = ShowFindUser,
    };

    // --- Open as ---

    /// <summary>Created on first use. Cookies are off: the session check sends its own <c>Cookie</c> header and must not keep what the site sets.</summary>
    private OpenAsService? OpenAsSvc
    {
        get
        {
            if (_openAsService is null && _source is not null)
            {
                var http = _openAsHttp = new HttpClient(new HttpClientHandler { UseCookies = false, UseProxy = false });
                _openAsService = new OpenAsService(
                    _source,
                    (baseUrl, sid, ct) => OpenAsService.CheckSessionAsync(http, baseUrl, sid, SessionCheckTimeout, ct),
                    TimeProvider.System);
            }
            return _openAsService;
        }
    }

    private IReadOnlyList<OpenAsItem> OpenAsItemsFor(BenchStatus bench, string site)
    {
        var pins = _config.PinsFor(bench.Path, site);
        var service = OpenAsSvc;
        return OpenAsMenuModel.Items(
            pins,
            service?.PreviousUser(bench.Path, site),
            user => service?.LabelFor(bench.Path, site, user, pins) ?? user);
    }

    /// <summary>
    /// The line to show instead of calling the shim, or null when the distro runs. Asked of WSL now, not read from the
    /// snapshot (up to 5 minutes old, and WSL idles a distro out): the shim would boot a stopped one.
    /// </summary>
    private async Task<string?> DistroProblemAsync(CancellationToken ct)
    {
        if (_probe is null) return _controller?.Snapshot.DistroStopped == true ? DistroGuard.StoppedMessage : null;
        return await DistroGuard.CheckAsync(_probe, DistroName.Resolve(_config.Distro), ct);
    }

    /// <summary>
    /// Opens the site in the browser as <paramref name="user"/>. The error line, or null. Cancelling <paramref name="ct"/>
    /// throws and opens nothing. The user counts as "current" only once the browser launch succeeded.
    /// </summary>
    private async Task<string?> SwitchUserAsync(BenchStatus bench, string site, string user, CancellationToken ct)
    {
        if (OpenAsSvc is not { } service) return "BenchBar is not ready.";
        if (await DistroProblemAsync(ct) is { } distroProblem) return distroProblem;
        var (url, error) = await service.SwitchAsync(bench, site, user, null, ct);
        if (url is null) return error ?? "The site could not be opened.";
        ct.ThrowIfCancellationRequested();
        // ShellExecute with the URL as one string: it holds `&`, which `cmd /c start` would split on
        if (!OpenUrl(url.AbsoluteUri)) return BrowserFailedMessage;
        service.MarkOpened(bench.Path, site, user);
        // a switch from Find User leaves a warm flyout showing the previous "as {label}" until the next poll
        if (_controller is { } controller)
            _flyout?.View.SetSnapshot(controller.Snapshot, TrayController.UpText(controller.Snapshot, controller.Benches.Select(b => b.State).ToList()));
        return null;
    }

    private async Task<(UserList? Users, string? Error)> FindUsersAsync(
        BenchStatus bench, string site, string? text, CancellationToken ct, int limit = OpenAsService.UserListLimit)
    {
        if (OpenAsSvc is not { } service) return (null, "BenchBar is not ready.");
        if (await DistroProblemAsync(ct) is { } distroProblem) return (null, distroProblem);
        return await service.FindUsersAsync(bench, site, text, ct, limit);
    }

    private void ShowFindUser(BenchStatus bench, string site)
    {
        var key = TrayConfig.PinKey(bench.Path, site);
        if (_findUsers.TryGetValue(key, out var existing))
        {
            existing.Activate();
            return;
        }
        var actions = new FindUserActions
        {
            Find = (text, ct) => FindUsersAsync(bench, site, text, ct),
            Open = user => SwitchUserAsync(bench, site, user, CancellationToken.None),
            Pins = () => _config.PinsFor(bench.Path, site),
            Pin = (user, label) =>
                !_config.TryPin(bench.Path, site, user, label) ? PinOutcome.NoRoom
                : SaveConfig() ? PinOutcome.Saved
                : PinOutcome.NotSaved,
            Unpin = user =>
            {
                _config.Unpin(bench.Path, site, user);
                return SaveConfig();
            },
        };
        var window = new FindUserWindow(site, actions, Theme);
        _findUsers[key] = window;
        window.Closed += (_, _) =>
        {
            _findUsers.Remove(key);
            Awake(false);
        };
        Awake(true);
        window.Show();
    }

    // --- Quick Switch ---

    /// <summary>
    /// Registers Ctrl+Alt+U when the setting is on and drops it when off. A key another app owns leaves the feature inactive
    /// with a note in the menu (the item stays checked); one click turns the setting off, the next turning it on tries again.
    /// </summary>
    private void ApplyQuickSwitch()
    {
        if (_quitting) return;
        if (!_config.QuickSwitch)
        {
            _hotKey?.Dispose();
            _hotKey = null;
            _quickSwitchTaken = false;
            _quickSwitch?.Close();
            return;
        }
        if (_hotKey is { IsRegistered: true }) return;
        _hotKey?.Dispose();
        var key = new HotKey(() => Dispatcher.BeginInvoke(ShowQuickSwitch));
        try
        {
            _quickSwitchTaken = !key.TryRegister();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.Write("quick-switch", ex);
            _quickSwitchTaken = true;
        }
        if (_quickSwitchTaken)
        {
            key.Dispose();
            _hotKey = null;
            return;
        }
        _hotKey = key;
    }

    private void ShowQuickSwitch()
    {
        if (_quitting || _controller is null) return;
        if (_quickSwitch is not null)
        {
            _quickSwitch.Close();
            return;
        }
        var target = QuickSwitchModel.Choose(_controller.Snapshot, _lastQuickSwitch);
        var service = OpenAsSvc;
        var data = QuickSwitchData.Empty;
        QuickSwitchActions actions = new();
        if (target.Bench is { } bench && service is not null)
        {
            data = new QuickSwitchData(
                site => _config.PinsFor(bench.Path, site),
                site => service.CurrentUser(bench.Path, site),
                site => service.PreviousUser(bench.Path, site),
                (site, user) => service.LabelFor(bench.Path, site, user, _config.PinsFor(bench.Path, site)),
                (site, user) => service.FullNameOf(bench.Path, site, user));
            actions = new QuickSwitchActions
            {
                Find = (site, text, ct) => FindUsersAsync(bench, site, text, ct, QuickSwitchModel.SearchLimit),
                Switch = async (site, user, ct) =>
                {
                    var error = await SwitchUserAsync(bench, site, user, ct);
                    if (error is null) _lastQuickSwitch = (bench.Path, site);
                    return error;
                },
                ReportFailure = message => ShowToast(new Toast("Open as failed", message, bench.Path, AlertKind.None)),
            };
        }
        else if (target.State == QuickSwitchState.Ready)
        {
            target = new QuickSwitchTarget(QuickSwitchState.BenchStopped, null, [], null);
        }

        var window = new QuickSwitchWindow(target, data, actions, Theme);
        _quickSwitch = window;
        window.Closed += (_, _) =>
        {
            _quickSwitch = null;
            Awake(false);
        };
        Awake(true);
        window.ShowCentered();
    }

    private VsCodeOutcome OpenVsCode(BenchStatus bench)
    {
        var code = VsCode.Find();
        if (code is null) return VsCodeOutcome.NotFound;
        var distro = DistroName.Resolve(_config.Distro) ?? _controller?.Snapshot.Distro;
        if (distro is null) return VsCodeOutcome.NotFound;
        if (!VsCode.CanLaunch(distro, bench.Path)) return VsCodeOutcome.BadPath;
        try
        {
            Process.Start(VsCode.StartInfo(code, distro, bench.Path))?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.Write("vscode", ex);
            return VsCodeOutcome.NotFound;
        }
        return VsCodeOutcome.Launched;
    }

    // --- Logs and Doctor ---

    private ResolvedTheme Theme => ThemeResolver.Resolve(_config.Appearance);

    private void ShowLogs(BenchStatus bench)
    {
        if (_source is null) return;
        if (_logs.TryGetValue(bench.Path, out var existing))
        {
            existing.Activate();
            _ = existing.LoadAsync();
            return;
        }
        var window = new LogsWindow(_source, bench, Theme, CopyText);
        _logs[bench.Path] = window;
        window.Closed += (_, _) =>
        {
            _logs.Remove(bench.Path);
            Awake(false);
        };
        Awake(true);
        window.Show();
    }

    private void ShowDoctor(BenchStatus bench)
    {
        if (_source is null) return;
        if (_doctors.TryGetValue(bench.Path, out var existing))
        {
            existing.Activate();
            _ = existing.RunAsync();
            return;
        }
        var window = new DoctorWindow(_source, bench, Theme, CopyText);
        _doctors[bench.Path] = window;
        window.Closed += (_, _) =>
        {
            _doctors.Remove(bench.Path);
            Awake(false);
        };
        Awake(true);
        window.Show();
    }

    // --- context menu ---

    private void EnsureMenu()
    {
        if (_menu is not null || _icon is null || _quitting) return;
        _menu = new TrayMenu(BuildMenuModel());
        _icon.ContextMenu = _menu.Menu;
    }

    private TrayMenuModel BuildMenuModel() => new()
    {
        LogonEnabled = _startup.IsEnabled,
        SetLogon = on =>
        {
            try { if (on) _startup.Enable(); else _startup.Disable(); }
            catch (Exception ex) { _log.Write("logon", ex); }
        },
        Appearance = () => _config.Appearance,
        SetAppearance = mode =>
        {
            _config.Appearance = mode;
            SaveConfig();
            _controller?.SetAppearance(mode);
            ApplyThemeMode();
        },
        LoadRunners = () =>
        {
            var set = RunnerSet.Load();
            _controller?.ReloadRunners(set);
            return set;
        },
        RunnerId = () => _config.RunnerId,
        SelectRunner = id =>
        {
            _config.RunnerId = id;
            SaveConfig();
            _controller?.SetRunner(id);
        },
        OpenRunnersFolder = OpenRunnersFolder,
        Notifications = () => _config.Notifications,
        SetNotifications = on =>
        {
            _config.Notifications = on;
            SaveConfig();
            if (_controller is not null) _controller.Notifications = on;
        },
        QuickSwitch = () => _config.QuickSwitch,
        QuickSwitchTaken = () => _quickSwitchTaken,
        SetQuickSwitch = on =>
        {
            _config.QuickSwitch = on;
            SaveConfig();
            ApplyQuickSwitch();
        },
        Theme = () => Theme,
        CheckForUpdates = () => _ = CheckForUpdatesAsync(),
        Help = () => OpenUrl(HelpUrl),
        ReportBug = () => _ = ReportBugAsync(),
        Quit = Quit,
    };

    private void ApplyThemeMode() =>
        ThemeMode = _config.Appearance switch
        {
            AppearanceMode.Light => ThemeMode.Light,
            AppearanceMode.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };

    private bool SaveConfig()
    {
        try
        {
            _config.Save();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write("config", ex);
            return false;
        }
    }

    private void OpenRunnersFolder()
    {
        try
        {
            Directory.CreateDirectory(RunnerSet.DefaultFolder);
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            info.ArgumentList.Add(RunnerSet.DefaultFolder);
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _log.Write("runners-folder", ex);
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var latest = await UpdateCheck.FetchLatestAsync(CancellationToken.None);
            var current = UpdateCheck.CurrentVersion();
            if (latest is { } release && UpdateCheck.IsNewer(release.Tag, current))
            {
                var answer = MessageBox.Show($"BenchBar {release.Tag.TrimStart('v')} is out. You have {current.Split('+')[0]}.\n\nOpen the release page?",
                    "BenchBar", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (answer == MessageBoxResult.Yes) OpenUrl(release.Url);
            }
            else
            {
                MessageBox.Show("You have the latest version of BenchBar.", "BenchBar", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            MessageBox.Show("Could not check for updates: " + ex.Message, "BenchBar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ReportBugAsync()
    {
        if (_controller is null || _source is null) return;
        var snapshot = _controller.Snapshot;
        if (snapshot.DistroStopped)
        {
            MessageBox.Show($"{snapshot.Distro} is stopped. Start it from the BenchBar window first; the report is written inside it.",
                "BenchBar", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var zip = await _source.ReportAsync(snapshot.Benches.FirstOrDefault()?.Path, CancellationToken.None);
            var distro = DistroName.Resolve(_config.Distro) ?? snapshot.Distro;
            if (distro is null)
            {
                MessageBox.Show("The report was written inside the distro:\n" + zip, "BenchBar");
                return;
            }
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            info.ArgumentList.Add("/select," + TextFormats.WslToWindows(distro, zip));
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex) when (ex is SourceProblemException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            MessageBox.Show(ex is OperationCanceledException ? BenchPoller.WslSilentMessage : ex.Message, "BenchBar report", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // --- small helpers ---

    private void CopyText(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException ex) { _log.Write("clipboard", ex); }
    }

    /// <summary>False when Windows could not launch the URL (no default browser).</summary>
    private static bool OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _controller?.Dispose();
        _hotKey?.Dispose(); // UnregisterHotKey
        _hotKey = null;
        _quickSwitch?.Close();
        _flyout?.Close();
        foreach (var w in _logs.Values.ToList()) w.Close();
        foreach (var w in _doctors.Values.ToList()) w.Close();
        foreach (var w in _findUsers.Values.ToList()) w.Close();
        _openAsHttp?.Dispose();
        _icon?.Dispose(); // NIM_DELETE
        _link?.Dispose(); // sends bye
        Shutdown();
    }
}
