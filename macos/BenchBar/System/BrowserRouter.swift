import AppKit

/// What Open as needs from the browser, so tests need none.
protocol BrowserOpening {
    func open(_ url: URL)
    /// The address of the testing browser's front tab, nil when it cannot be read.
    func frontTabURL() async -> URL?
    /// Sends the front tab to `url`; false when it could not.
    func replaceFrontTab(with url: URL) async -> Bool
}

/// Picks the browser a switch opens in: the supported browser that was
/// frontmost last (the one whose cookies are being tested), not the default
/// browser, which may be another one. With "Keep the page I'm on" it also
/// reads and replaces that browser's front tab through Apple Events.
final class BrowserRouter: BrowserOpening {
    nonisolated static let safari = "com.apple.Safari"
    nonisolated static let firefox = "org.mozilla.firefox"
    /// Safari, then the Chromium family and Arc (they share one tab dictionary), then Firefox.
    nonisolated static let supported = [safari, "com.google.Chrome", "company.thebrowser.Browser", "com.brave.Browser",
                                         "com.microsoft.edgemac", firefox]

    private(set) var testingBrowser: String?
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
              let text = run(source)?.stringValue else { return nil }
        return URL(string: text)
    }

    func replaceFrontTab(with url: URL) async -> Bool {
        guard let id = scriptableBrowser, let source = Self.replaceScript(bundleID: id, url: url.absoluteString) else { return false }
        return run(source) != nil
    }

    /// The testing browser, only while it runs: `tell application` would launch it.
    private var runningTestingBrowser: String? {
        guard let id = testingBrowser,
              !NSRunningApplication.runningApplications(withBundleIdentifier: id).isEmpty else { return nil }
        return id
    }

    /// Firefox has no AppleScript dictionary for its tabs.
    private var scriptableBrowser: String? {
        runningTestingBrowser.flatMap { $0 == Self.firefox ? nil : $0 }
    }

    private func run(_ source: String) -> NSAppleEventDescriptor? {
        var failure: NSDictionary?
        let result = NSAppleScript(source: source)?.executeAndReturnError(&failure)
        return failure == nil ? result : nil
    }

    // MARK: pure

    /// Same scheme, host and port: the tab is on the site being switched.
    nonisolated static func matches(tabURL: URL, siteURL: URL) -> Bool {
        guard tabURL.scheme == "http", siteURL.scheme == "http",
              let tabHost = tabURL.host?.lowercased(), tabHost == siteURL.host?.lowercased() else { return false }
        return (tabURL.port ?? 80) == (siteURL.port ?? 80)
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
