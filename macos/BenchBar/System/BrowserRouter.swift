import AppKit

/// What Open as needs from the browser, so tests need none.
protocol BrowserOpening {
    func open(_ url: URL)
    /// The address of the testing browser's front tab, nil when it cannot be read.
    func frontTabURL() async -> URL?
    /// Sends the front tab to `url`; false when it could not.
    func replaceFrontTab(with url: URL) async -> Bool
    /// Why "Keep the page I'm on" cannot work with the testing browser, nil when nothing is known to be wrong.
    var tabControlIssue: TabControlIssue? { get }
}

/// A reason the browser's tab cannot be read or replaced; `OpenAsStore` tells the user once.
nonisolated enum TabControlIssue: Hashable, Sendable {
    case automationDenied(browser: String)
    case firefox

    var note: String {
        switch self {
        case .automationDenied(let browser):
            "Keep the page I'm on needs permission to control \(browser): System Settings → Privacy & Security → Automation"
        case .firefox:
            "Firefox always gets a new tab: Keep the page I'm on cannot replace its current tab."
        }
    }
}

/// Picks the browser a switch opens in: the supported browser that was
/// frontmost last (the one whose cookies are being tested), else the first
/// supported browser that runs, and only then the default browser. With "Keep the page I'm on" it also
/// reads and replaces that browser's front tab through Apple Events.
final class BrowserRouter: BrowserOpening {
    nonisolated static let safari = "com.apple.Safari"
    nonisolated static let firefox = "org.mozilla.firefox"
    /// Safari, then the Chromium family and Arc (they share one tab dictionary), then Firefox.
    nonisolated static let supported = [safari, "com.google.Chrome", "company.thebrowser.Browser", "com.brave.Browser",
                                         "com.microsoft.edgemac", firefox]

    /// Apple's "not authorized to send Apple events" (errAEEventNotPermitted).
    private nonisolated static let automationDeniedError = -1743

    private(set) var testingBrowser: String?
    private var deniedBrowser: String?
    private var observer: NSObjectProtocol?

    init() {
        testingBrowser = NSWorkspace.shared.frontmostApplication?.bundleIdentifier.flatMap(Self.supportedID)
        observer = NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { [weak self] note in
            let id = (note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?.bundleIdentifier
            MainActor.assumeIsolated {
                if let id, let known = Self.supportedID(id) { self?.testingBrowser = known }
            }
        }
    }

    nonisolated static func supportedID(_ bundleID: String) -> String? {
        supported.contains(bundleID) ? bundleID : nil
    }

    func open(_ url: URL) {
        if let id = runningTestingBrowser, let app = NSWorkspace.shared.urlForApplication(withBundleIdentifier: id) {
            NSWorkspace.shared.open([url], withApplicationAt: app, configuration: NSWorkspace.OpenConfiguration())
        } else {
            NSWorkspace.shared.open(url)
        }
    }

    func frontTabURL() async -> URL? {
        guard let id = scriptableBrowser, let source = Self.readScript(bundleID: id),
              let text = run(source, bundleID: id)?.stringValue else { return nil }
        return URL(string: text)
    }

    /// On success the browser is brought to the front: BenchBar is frontmost
    /// when the switch started from its popover or window, and the new page would stay behind it.
    func replaceFrontTab(with url: URL) async -> Bool {
        guard let id = scriptableBrowser, let source = Self.replaceScript(bundleID: id, url: url.absoluteString),
              run(source, bundleID: id) != nil else { return false }
        NSRunningApplication.runningApplications(withBundleIdentifier: id).first?.activate()
        return true
    }

    var tabControlIssue: TabControlIssue? {
        if let deniedBrowser { return .automationDenied(browser: Self.displayName(deniedBrowser)) }
        return runningTestingBrowser == Self.firefox ? .firefox : nil
    }

    /// The testing browser, only while it runs: `tell application` would launch it.
    private var runningTestingBrowser: String? {
        Self.browser(testing: testingBrowser, running: Set(Self.supported.filter(isRunning)), defaultBrowser: Self.defaultBrowserID())
    }

    private func isRunning(_ bundleID: String) -> Bool {
        !NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).isEmpty
    }

    /// Firefox has no AppleScript dictionary for its tabs.
    private var scriptableBrowser: String? {
        runningTestingBrowser.flatMap { $0 == Self.firefox ? nil : $0 }
    }

    private func run(_ source: String, bundleID: String) -> NSAppleEventDescriptor? {
        var failure: NSDictionary?
        let result = NSAppleScript(source: source)?.executeAndReturnError(&failure)
        guard let failure else {
            deniedBrowser = nil
            return result
        }
        // Only a refused permission is worth telling the user; "no window open" and the like are normal.
        if (failure["NSAppleScriptErrorNumber"] as? Int) == Self.automationDeniedError { deniedBrowser = bundleID }
        return nil
    }

    // MARK: pure

    /// The browser a switch uses: the one tested last while it runs, else the
    /// first supported one that runs (in `supported` order), else nil for the default browser.
    /// The tested browser while it runs, else the default browser if it runs,
    /// else the first supported one that runs: a Safari left open in the
    /// background must not win over a Chrome the person set as default.
    nonisolated static func browser(testing: String?, running: Set<String>, defaultBrowser: String? = nil) -> String? {
        if let testing, running.contains(testing) { return testing }
        if let defaultBrowser, running.contains(defaultBrowser) { return defaultBrowser }
        return supported.first(where: running.contains)
    }

    nonisolated static func defaultBrowserID() -> String? {
        guard let probe = URL(string: "http://example.com"),
              let app = NSWorkspace.shared.urlForApplication(toOpen: probe) else { return nil }
        return Bundle(url: app)?.bundleIdentifier
    }

    nonisolated static func displayName(_ bundleID: String) -> String {
        switch bundleID {
        case safari: "Safari"
        case firefox: "Firefox"
        case "com.google.Chrome": "Google Chrome"
        case "company.thebrowser.Browser": "Arc"
        case "com.brave.Browser": "Brave"
        case "com.microsoft.edgemac": "Microsoft Edge"
        default: bundleID
        }
    }

    /// Same scheme, host and port: the tab is on the site being switched. The
    /// bench also serves its default site at 127.0.0.1 and localhost on its
    /// port, and people work there; a login cookie belongs to one host, so
    /// such a tab is the default site's too and the link has to go to it.
    nonisolated static func matches(tabURL: URL, siteURL: URL, isDefaultSite: Bool = false) -> Bool {
        guard tabURL.scheme == "http", siteURL.scheme == "http", let tabHost = tabURL.host?.lowercased(),
              (tabURL.port ?? 80) == (siteURL.port ?? 80) else { return false }
        if tabHost == siteURL.host?.lowercased() { return true }
        return isDefaultSite && (tabHost == "127.0.0.1" || tabHost == "localhost")
    }

    /// `link` at the tab's host and port, so the cookie the link sets lands where the tab is.
    nonisolated static func link(_ link: URL, onHostOf tab: URL) -> URL {
        guard var parts = URLComponents(url: link, resolvingAgainstBaseURL: false) else { return link }
        parts.host = tab.host
        parts.port = tab.port
        return parts.url ?? link
    }

    /// `/app/nst/NST-1?x=1#y` out of `http://macdev:8000/app/nst/NST-1?x=1#y`.
    nonisolated static func path(of tabURL: URL) -> String {
        guard let parts = URLComponents(url: tabURL, resolvingAgainstBaseURL: false) else { return "/" }
        let path = parts.percentEncodedPath.isEmpty ? "/" : parts.percentEncodedPath
        return path + (parts.percentEncodedQuery.map { "?" + $0 } ?? "") + (parts.percentEncodedFragment.map { "#" + $0 } ?? "")
    }

    nonisolated static func readScript(bundleID: String) -> String? {
        guard bundleID != firefox, supported.contains(bundleID) else { return nil }
        let target = bundleID == safari ? "URL of front document" : "URL of active tab of front window"
        return timeLimited("tell application id \"\(bundleID)\" to get \(target)")
    }

    nonisolated static func replaceScript(bundleID: String, url: String) -> String? {
        guard bundleID != firefox, supported.contains(bundleID) else { return nil }
        let target = bundleID == safari ? "URL of front document" : "URL of active tab of front window"
        return timeLimited("tell application id \"\(bundleID)\" to set \(target) to \"\(escapedForAppleScript(url))\"")
    }

    /// The scripts run on the main thread: a browser that does not answer would hold the popover
    /// for the two minutes an Apple Event waits by default. The consent prompt is not limited by this.
    nonisolated static func timeLimited(_ statement: String) -> String {
        "with timeout of 3 seconds\n\(statement)\nend timeout"
    }

    /// A string literal's two special characters; line breaks cannot be in one.
    nonisolated static func escapedForAppleScript(_ text: String) -> String {
        text.replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
            .replacingOccurrences(of: "\n", with: "")
            .replacingOccurrences(of: "\r", with: "")
    }
}
