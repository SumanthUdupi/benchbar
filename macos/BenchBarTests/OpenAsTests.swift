import Foundation
import Testing
@testable import BenchBar

/// Calls the store's CLI makes, answered from a table.
nonisolated final class FakeOpenAsCLI: OpenAsCLI, @unchecked Sendable {
    struct Open: Equatable {
        var site: String
        var user: String
        var path: String?
    }

    private let lock = NSLock()
    private var _opens: [Open] = []
    var failure: CLIError?
    var sid = "freshsid0001"
    var fullName: String? = "Priya Nair"
    var userType = "System User"

    var opens: [Open] { lock.withLock { _opens } }

    func openAs(bench: String, site: String, user: String, path: String?) async throws(CLIError) -> OpenAsResult {
        lock.withLock { _opens.append(Open(site: site, user: user, path: path)) }
        if let failure { throw failure }
        return OpenAsResult(site: site, user: user, fullName: fullName, userType: userType, path: path ?? "/app",
                            url: "http://\(site):8000/login?redirect-to=%2Fapp&sid=\(sid)&t=1")
    }

    func users(bench: String, site: String, search: String?, limit: Int) async throws(CLIError) -> UserList {
        UserList(site: site, users: [], truncated: false)
    }
}

final class FakeBrowser: BrowserOpening {
    var opened: [URL] = []
    var replaced: [URL] = []
    var tab: URL?
    var replaceSucceeds = true

    func open(_ url: URL) { opened.append(url) }
    func frontTabURL() async -> URL? { tab }
    func replaceFrontTab(with url: URL) async -> Bool {
        replaced.append(url)
        return replaceSucceeds
    }
}

@Suite("Open as")
struct OpenAsTests {
    let bench = "/Users/you/frappe-bench"
    let site = "macdev"
    let siteURL = "http://macdev:8000"

    func scratchDefaults() throws -> UserDefaults {
        try #require(UserDefaults(suiteName: "benchbar-open-as-\(UUID().uuidString)"))
    }

    func makeStore(defaults: UserDefaults? = nil, browser: FakeBrowser = FakeBrowser(), keepPage: Bool = false,
               check: @escaping @Sendable (URL, String) async -> String? = { _, _ in nil }) throws -> OpenAsStore {
        OpenAsStore(defaults: try defaults ?? scratchDefaults(), keepPage: { keepPage }, browser: browser, checkSession: check)
    }

    func switchTo(_ user: String, on store: OpenAsStore, cli: FakeOpenAsCLI, path: String? = nil) async -> String? {
        await store.switchTo(user: user, bench: bench, site: site, siteURL: siteURL, path: path, client: cli)
    }

    // MARK: models

    @Test func openAsResultDecodesTheCLIDocument() throws {
        let result = try BenchJSON.decode(OpenAsResult.self, from: Fixture.data("open-as"))
        #expect(result.user == "priya@example.com" && result.fullName == "Priya Nair" && result.userType == "System User")
        #expect(result.path == "/app")
        #expect(OpenAsLink.sessionID(in: try #require(result.url)) == "fixturesid0123456789")
    }

    @Test func theLinkIsAbsentWithoutPrintURL() throws {
        let json = #"{"schema_version":1,"site":"macdev","user":"a@x.com","full_name":"A","user_type":"System User","path":"/app","opened":true}"#
        #expect(try BenchJSON.decode(OpenAsResult.self, from: Data(json.utf8)).url == nil)
    }

    @Test func theUserListDecodesWithMissingNamesAndLogins() throws {
        let list = try BenchJSON.decode(UserList.self, from: Fixture.data("open-as-users"))
        #expect(list.truncated)
        #expect(list.users.map(\.name) == ["Administrator", "priya@example.com", "customer@example.com"])
        #expect(list.users[1].roles.count == 5 && list.users[1].roleCount == 9)
        #expect(list.users[2].fullName == nil && list.users[2].lastLogin == nil && list.users[2].userType == "Website User")
    }

    @Test func aSiteWithoutDeveloperModeDecodesToNilAndAHiddenRow() throws {
        let old = #"{"name":"macdev","default":true,"hosts_entry":true,"ping_code":200}"#
        let info = try JSONDecoder().decode(SiteInfo.self, from: Data(old.utf8))
        #expect(info.developerMode == nil)
        #expect(SiteRow.make(sites: [info], defaultSite: "macdev", port: 8000).map(\.developerMode) == [false])
        var on = info
        on.developerMode = true
        #expect(SiteRow.make(sites: [on], defaultSite: "macdev", port: 8000).map(\.developerMode) == [true])
        let sent = #"{"name":"macdev","default":true,"hosts_entry":true,"ping_code":null,"developer_mode":true}"#
        #expect(try JSONDecoder().decode(SiteInfo.self, from: Data(sent.utf8)).developerMode == true)
    }

    // MARK: CLI client

    @Test func openAsPassesTheUserThePathAndPrintURL() async throws {
        let runner = FakeRunner { _ in .ok(try! Fixture.string("open-as")) }
        let client = CLIClient(executable: URL(fileURLWithPath: "/bin/benchbar"), runner: runner)
        let result = try await client.openAs(bench: bench, site: site, user: "a+b@x.com", path: "/app/nst?status=Open#x")
        #expect(result.user == "priya@example.com")
        #expect(runner.calls.first?.arguments == ["open", "--bench-dir", bench, "--site", site, "--as", "a+b@x.com",
                                                  "--path", "/app/nst?status=Open#x", "--print-url", "--json"])
        _ = try await client.openAs(bench: bench, site: site, user: "a@x.com", path: nil)
        #expect(runner.calls.last?.arguments == ["open", "--bench-dir", bench, "--site", site, "--as", "a@x.com", "--print-url", "--json"])
    }

    @Test func usersPassesTheSearchAsOneArgument() async throws {
        let runner = FakeRunner { _ in .ok(try! Fixture.string("open-as-users")) }
        let client = CLIClient(executable: URL(fileURLWithPath: "/bin/benchbar"), runner: runner)
        _ = try await client.users(bench: bench, site: site, search: "o'brien x", limit: 50)
        #expect(runner.calls.first?.arguments == ["users", "--bench-dir", bench, "--site", site, "--search", "o'brien x", "--limit", "50", "--json"])
        _ = try await client.users(bench: bench, site: site, search: "", limit: 50)
        #expect(runner.calls.last?.arguments == ["users", "--bench-dir", bench, "--site", site, "--limit", "50", "--json"])
    }

    @Test func aMalformedAnswerDoesNotKeepTheLinkInItsError() async {
        let runner = FakeRunner { _ in .ok("{\"schema_version\":1,\"url\":\"http://macdev:8000/login?sid=secretsid1&t=1\"") }
        let client = CLIClient(executable: URL(fileURLWithPath: "/bin/benchbar"), runner: runner)
        do {
            _ = try await client.openAs(bench: bench, site: site, user: "a@x.com", path: nil)
            Issue.record("expected a decoding failure")
        } catch {
            #expect(!String(describing: error).contains("secretsid1"))
            #expect(error.recoverySuggestion?.contains("secretsid1") != true)
        }
    }

    @Test func aRefusalIsShownWithoutAnySessionID() async {
        let runner = FakeRunner { _ in CommandOutput(exitCode: 1, stdout: "http://macdev:8000/login?sid=secretsid1&t=1", stderr: "[FAIL] Frappe refused the login: sid=secretsid1") }
        let client = CLIClient(executable: URL(fileURLWithPath: "/bin/benchbar"), runner: runner)
        do {
            _ = try await client.openAs(bench: bench, site: site, user: "a@x.com", path: nil)
            Issue.record("expected a refusal")
        } catch {
            #expect(!error.localizedDescription.contains("secretsid1"))
        }
    }

    // MARK: links

    @Test func theRedirectIsEncodedOnceAndAnOldSessionIDIsRemoved() throws {
        let url = try #require(OpenAsLink.url(base: siteURL, path: "/app/nst?status=Open&sid=old#x", sid: "SID1", millis: 42))
        #expect(url.absoluteString == "http://macdev:8000/login?redirect-to=%2Fapp%2Fnst%3Fstatus%3DOpen%23x&sid=SID1&t=42")
        #expect(OpenAsLink.removingSession(from: "/app/x?sid=old") == "/app/x")
        #expect(OpenAsLink.removingSession(from: "/app/x?a=1&sid=old&b=2") == "/app/x?a=1&b=2")
        #expect(OpenAsLink.removingSession(from: "/app/todo") == "/app/todo")
        let plus = try #require(OpenAsLink.url(base: siteURL, path: "/app/user/a+b@x.com", sid: "S", millis: 1))
        #expect(plus.absoluteString.contains("redirect-to=%2Fapp%2Fuser%2Fa%2Bb%40x.com&"))
    }

    @Test func scrubHidesEverySessionIDInAMessage() {
        #expect(OpenAsLink.scrub("bad link http://h/login?redirect-to=%2Fapp&sid=abc123&t=9 end sid=zzz") == "bad link http://h/login?redirect-to=%2Fapp&sid=…&t=9 end sid=…")
    }

    // MARK: switching

    @Test func eachSwitchGetsItsOwnTimestamp() async throws {
        let browser = FakeBrowser()
        let cli = FakeOpenAsCLI()
        let store = try makeStore(browser: browser, check: { _, sid in sid == "freshsid0001" ? "priya@example.com" : nil })
        _ = await switchTo("priya@example.com", on: store, cli: cli)
        _ = await switchTo("priya@example.com", on: store, cli: cli)
        _ = await switchTo("priya@example.com", on: store, cli: cli)
        #expect(cli.opens.count == 1)
        let stamps = browser.opened.dropFirst().compactMap { URLComponents(url: $0, resolvingAgainstBaseURL: false)?.queryItems?.first { $0.name == "t" }?.value }
        #expect(stamps.count == 2 && Set(stamps).count == 2)
        #expect(browser.opened.dropFirst().allSatisfy { $0.absoluteString.contains("redirect-to=%2Fapp&sid=freshsid0001&t=") })
    }

    @Test func aDeadCachedSessionAsksTheCLIForANewOne() async throws {
        let cli = FakeOpenAsCLI()
        let browser = FakeBrowser()
        let store = try makeStore(browser: browser, check: { _, _ in "Guest" })
        #expect(await switchTo("priya@example.com", on: store, cli: cli) == nil)
        #expect(await switchTo("priya@example.com", on: store, cli: cli) == nil)
        #expect(cli.opens.count == 2)
        #expect(browser.opened.count == 2)
    }

    @Test func aLiveCachedSessionIsReusedWithoutTheCLI() async throws {
        let cli = FakeOpenAsCLI()
        let store = try makeStore(check: { _, _ in "priya@example.com" })
        _ = await switchTo("priya@example.com", on: store, cli: cli)
        #expect(await switchTo("priya@example.com", on: store, cli: cli) == nil)
        #expect(cli.opens.count == 1)
    }

    @Test func aSessionOfAnotherUserIsNotReused() async throws {
        let cli = FakeOpenAsCLI()
        let store = try makeStore(check: { _, _ in "someone.else@example.com" })
        _ = await switchTo("priya@example.com", on: store, cli: cli)
        _ = await switchTo("priya@example.com", on: store, cli: cli)
        #expect(cli.opens.count == 2)
    }

    @Test func aWebsiteUsersCachedSessionLandsOnTheSiteRoot() async throws {
        let cli = FakeOpenAsCLI()
        cli.userType = "Website User"
        let browser = FakeBrowser()
        let store = try makeStore(browser: browser, check: { _, _ in "customer@example.com" })
        _ = await switchTo("customer@example.com", on: store, cli: cli)
        _ = await switchTo("customer@example.com", on: store, cli: cli)
        #expect(browser.opened.last?.absoluteString.contains("redirect-to=%2F&sid=") == true)
    }

    @Test func aRefusalComesBackAsTextAndOpensNothing() async throws {
        let cli = FakeOpenAsCLI()
        cli.failure = .failed(command: "open", exitCode: 1, message: "macdev is not in developer mode, so benchbar will not sign in as another user.")
        let browser = FakeBrowser()
        let store = try makeStore(browser: browser)
        let error = await switchTo("a@x.com", on: store, cli: cli)
        #expect(error == "macdev is not in developer mode, so benchbar will not sign in as another user.")
        #expect(browser.opened.isEmpty)
        #expect(store.recents(bench: bench, site: site).isEmpty)
        #expect(store.switching.isEmpty)
    }

    @Test func theSessionIDIsNotInUserDefaults() async throws {
        let defaults = try scratchDefaults()
        let cli = FakeOpenAsCLI()
        let store = try makeStore(defaults: defaults)
        _ = await switchTo("priya@example.com", on: store, cli: cli)
        store.pin("priya@example.com", fullName: "Priya Nair", bench: bench, site: site)
        #expect(!String(describing: defaults.dictionaryRepresentation()).contains(cli.sid))
    }

    // MARK: pins and recents

    @Test func pinsRoundTripThroughUserDefaultsAndTheTenthIsRefused() throws {
        let defaults = try scratchDefaults()
        let first = try makeStore(defaults: defaults)
        for number in 1...9 { #expect(first.pin("user\(number)@x.com", fullName: nil, bench: bench, site: site)) }
        #expect(!first.pin("user10@x.com", fullName: nil, bench: bench, site: site))
        first.setLabel("  NE ", for: "user2@x.com", bench: bench, site: site)
        let second = try makeStore(defaults: defaults)
        let pins = second.pins(bench: bench, site: site)
        #expect(pins.count == 9 && pins[1] == PinnedUser(user: "user2@x.com", label: "NE"))
        #expect(second.pins(bench: bench, site: "other").isEmpty)
        second.unpin("user2@x.com", bench: bench, site: site)
        #expect(!second.isPinned("user2@x.com", bench: bench, site: site))
    }

    @Test func administratorCannotBePinnedAndAnEmptyLabelClears() throws {
        let store = try makeStore()
        #expect(!store.pin(OpenAsStore.administrator, fullName: nil, bench: bench, site: site))
        store.pin("a@x.com", fullName: nil, bench: bench, site: site)
        store.setLabel("CS", for: "a@x.com", bench: bench, site: site)
        store.setLabel("  ", for: "a@x.com", bench: bench, site: site)
        #expect(store.pins(bench: bench, site: site).first?.label == nil)
    }

    @Test func recentsKeepTwoNewestFirstAndSwitchBackIsThePreviousOne() async throws {
        let cli = FakeOpenAsCLI()
        let store = try makeStore()
        _ = await switchTo("a@x.com", on: store, cli: cli)
        _ = await switchTo("b@x.com", on: store, cli: cli)
        _ = await switchTo("c@x.com", on: store, cli: cli)
        let recents = store.recents(bench: bench, site: site)
        #expect(recents.map(\.user) == ["c@x.com", "b@x.com"])
        guard case .switchBack(let user, _)? = OpenAsMenuModel.items(pins: [], recents: recents).first else {
            Issue.record("expected Switch Back first")
            return
        }
        #expect(user == "b@x.com")
        _ = await switchTo(user, on: store, cli: cli)
        #expect(store.recents(bench: bench, site: site).map(\.user) == ["b@x.com", "c@x.com"])
        _ = await switchTo("b@x.com", on: store, cli: cli)
        #expect(store.recents(bench: bench, site: site).map(\.user) == ["b@x.com", "c@x.com"])
    }

    @Test func theRowSubtitleNamesTheCurrentUserByLabelThenFullNameThenID() async throws {
        let cli = FakeOpenAsCLI()
        let store = try makeStore()
        #expect(store.currentLabel(bench: bench, site: site) == nil)
        _ = await switchTo("priya@example.com", on: store, cli: cli)
        #expect(store.currentLabel(bench: bench, site: site) == "Priya Nair")
        store.pin("priya@example.com", fullName: "Priya Nair", bench: bench, site: site)
        store.setLabel("NE", for: "priya@example.com", bench: bench, site: site)
        #expect(store.currentLabel(bench: bench, site: site) == "NE")
        cli.fullName = nil
        _ = await switchTo("raw@example.com", on: store, cli: cli)
        #expect(store.currentLabel(bench: bench, site: site) == "raw@example.com")
        #expect(store.currentLabel(bench: bench, site: "other") == nil)
    }

    // MARK: the menu

    @Test func theMenuIsInTheDocumentedOrderWithDigitsFromThePinIndexes() {
        let pins = [PinnedUser(user: "rahul@x.com", label: "CS", fullName: "Rahul Sharma"),
                    PinnedUser(user: "priya@x.com", label: nil, fullName: "Priya Nair"),
                    PinnedUser(user: "arun@x.com", label: nil, fullName: nil)]
        let recents = [RecentUser(user: "arun@x.com", fullName: nil), RecentUser(user: "priya@x.com", fullName: "Priya Nair")]
        #expect(OpenAsMenuModel.items(pins: pins, recents: recents) == [
            .switchBack(user: "priya@x.com", title: "Switch Back to Priya Nair"),
            .divider(1),
            .pin(user: "rahul@x.com", title: "CS — rahul@x.com", fullName: "Rahul Sharma", shortcut: .openAs(1)),
            .pin(user: "priya@x.com", title: "Priya Nair — priya@x.com", fullName: "Priya Nair", shortcut: .openAs(2)),
            .pin(user: "arun@x.com", title: "arun@x.com", fullName: nil, shortcut: .openAs(3)),
            .administrator,
            .divider(2),
            .findUser,
            .keepPage,
        ])
    }

    @Test func switchBackAndItsDividerAreHiddenWithoutAPreviousUser() {
        #expect(OpenAsMenuModel.items(pins: [], recents: []) == [.administrator, .divider(2), .findUser, .keepPage])
        let one = [RecentUser(user: "a@x.com", fullName: nil)]
        #expect(OpenAsMenuModel.items(pins: [], recents: one).first == .administrator)
    }

    @Test func labelsFallBackFromPinLabelToFullNameToUserID() {
        let pins = [PinnedUser(user: "a@x.com", label: "NE"), PinnedUser(user: "b@x.com", label: " ")]
        #expect(OpenAsMenuModel.label(for: "a@x.com", fullName: "Anita", pins: pins) == "NE")
        #expect(OpenAsMenuModel.label(for: "b@x.com", fullName: "Bala", pins: pins) == "Bala")
        #expect(OpenAsMenuModel.label(for: "b@x.com", fullName: nil, pins: pins) == "b@x.com")
        #expect(OpenAsMenuModel.label(for: "c@x.com", fullName: "", pins: []) == "c@x.com")
    }

    @Test func aPinnedAdministratorIsNotListedTwice() {
        let items = OpenAsMenuModel.items(pins: [PinnedUser(user: OpenAsStore.administrator, label: nil)], recents: [])
        #expect(items.filter { $0 == .administrator }.count == 1)
        #expect(!items.contains { if case .pin = $0 { true } else { false } })
    }

    @Test func theMenuShowsOnlyForARunningDeveloperModeSiteWithAHostsLine() {
        let ready = SiteRow(name: "macdev", isDefault: true, url: siteURL, needsHosts: false, developerMode: true)
        #expect(OpenAsMenuModel.isAvailable(ready, benchRunning: true))
        #expect(!OpenAsMenuModel.isAvailable(ready, benchRunning: false))
        var production = ready
        production.developerMode = false
        #expect(!OpenAsMenuModel.isAvailable(production, benchRunning: true))
        var unresolved = ready
        unresolved.needsHosts = true
        #expect(!OpenAsMenuModel.isAvailable(unresolved, benchRunning: true))
    }

    @Test func theShortcutsAreCommandDigitsAndShiftCommandO() {
        #expect(OpenAsShortcut.openAs(0).display == "⌘0" && OpenAsShortcut.openAs(7).display == "⌘7")
        #expect(OpenAsShortcut.switchBack.display == "⌘⇧O")
        #expect(OpenAsShortcut.switchBack.key == "o" && OpenAsShortcut.switchBack.usesShift && !OpenAsShortcut.openAs(1).usesShift)
        #expect(BenchShortcut.openSite.display == "⌘O")
    }

    // MARK: the browser

    @Test func keepThePageOnPassesTheTabsPathAndSendsTheTabToTheNewUser() async throws {
        let browser = FakeBrowser()
        browser.tab = URL(string: "http://macdev:8000/app/nst/NST-1?x=1#y")
        let cli = FakeOpenAsCLI()
        let store = try makeStore(browser: browser, keepPage: true)
        #expect(await switchTo("priya@example.com", on: store, cli: cli) == nil)
        #expect(cli.opens.first?.path == "/app/nst/NST-1?x=1#y")
        #expect(browser.replaced.count == 1 && browser.opened.isEmpty)
    }

    @Test func aTabThatCannotBeReplacedFallsBackToOpeningTheLink() async throws {
        let browser = FakeBrowser()
        browser.tab = URL(string: "http://macdev:8000/app/nst")
        browser.replaceSucceeds = false
        let store = try makeStore(browser: browser, keepPage: true)
        _ = await switchTo("priya@example.com", on: store, cli: FakeOpenAsCLI())
        #expect(browser.replaced.count == 1 && browser.opened.count == 1)
    }

    @Test func aTabOnAnotherSiteOrWithTheSettingOffOpensANewTab() async throws {
        let other = FakeBrowser()
        other.tab = URL(string: "http://other:8000/app/x")
        let cli = FakeOpenAsCLI()
        _ = await switchTo("a@x.com", on: try makeStore(browser: other, keepPage: true), cli: cli)
        #expect(cli.opens.last?.path == nil && other.replaced.isEmpty && other.opened.count == 1)

        let off = FakeBrowser()
        off.tab = URL(string: "http://macdev:8000/app/x")
        _ = await switchTo("a@x.com", on: try makeStore(browser: off, keepPage: false), cli: cli)
        #expect(cli.opens.last?.path == nil && off.replaced.isEmpty && off.opened.count == 1)
    }

    @Test func aTabIsOnTheSiteOnlyWithTheSameSchemeHostAndPort() throws {
        let site = try #require(URL(string: siteURL))
        func matches(_ text: String) -> Bool { BrowserRouter.matches(tabURL: URL(string: text)!, siteURL: site) }
        #expect(matches("http://macdev:8000/app/nst"))
        #expect(matches("http://MacDev:8000/"))
        #expect(!matches("http://macdev:8001/app"))
        #expect(!matches("http://other:8000/app"))
        #expect(!matches("https://macdev:8000/app"))
        #expect(!matches("http://macdev/app"))
    }

    @Test func theTabsPathKeepsItsQueryAndFragment() throws {
        #expect(BrowserRouter.path(of: try #require(URL(string: "http://macdev:8000/app/nst/NST-1?x=1#y"))) == "/app/nst/NST-1?x=1#y")
        #expect(BrowserRouter.path(of: try #require(URL(string: "http://macdev:8000"))) == "/")
        #expect(BrowserRouter.path(of: try #require(URL(string: "http://macdev:8000/app/a%20b?q=%26"))) == "/app/a%20b?q=%26")
    }

    @Test func theScriptsUseTheBundleIDAndEscapeTheAddress() {
        #expect(BrowserRouter.readScript(bundleID: BrowserRouter.safari) == #"tell application id "com.apple.Safari" to get URL of front document"#)
        #expect(BrowserRouter.readScript(bundleID: "com.google.Chrome") == #"tell application id "com.google.Chrome" to get URL of active tab of front window"#)
        #expect(BrowserRouter.readScript(bundleID: BrowserRouter.firefox) == nil)
        #expect(BrowserRouter.readScript(bundleID: "com.example.Unknown") == nil)
        #expect(BrowserRouter.replaceScript(bundleID: BrowserRouter.safari, url: "http://macdev:8000/app")
                == #"tell application id "com.apple.Safari" to set URL of front document to "http://macdev:8000/app""#)
        let script = BrowserRouter.replaceScript(bundleID: "com.brave.Browser", url: "http://h/a\"b\\c\nd") ?? ""
        #expect(script.hasSuffix(#"to "http://h/a\"b\\cd""#))
        #expect(!script.contains("\n"))
    }

    @Test func onlyTheSupportedBrowsersAreTracked() {
        for id in ["com.apple.Safari", "com.google.Chrome", "company.thebrowser.Browser", "com.brave.Browser", "com.microsoft.edgemac", "org.mozilla.firefox"] {
            #expect(BrowserRouter.supportedID(id) == id)
        }
        #expect(BrowserRouter.supportedID("com.apple.finder") == nil)
    }

    // MARK: the session check

    @Test func theSessionCheckReadsTheUserFromTheAnswer() {
        let ok = Data("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n{\"message\":\"priya@example.com\"}".utf8)
        #expect(SessionProbe.user(inResponse: ok) == "priya@example.com")
        let chunked = Data("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n1e\r\n{\"message\":\"Guest\"}\r\n0\r\n\r\n".utf8)
        #expect(SessionProbe.user(inResponse: chunked) == "Guest")
        #expect(SessionProbe.user(inResponse: Data("HTTP/1.1 403 FORBIDDEN\r\n\r\n{\"message\":\"x\"}".utf8)) == nil)
        #expect(SessionProbe.user(inResponse: Data()) == nil)
        #expect(SessionProbe.user(inResponse: Data("HTTP/1.1 200 OK\r\n\r\nnot json".utf8)) == nil)
    }

    @Test func theSessionCheckSendsTheCookieToTheSiteAndRefusesAnOddID() throws {
        let request = try #require(SessionProbe.request(site: "macdev", sessionID: "abc123"))
        #expect(request.hasPrefix("GET /api/method/frappe.auth.get_logged_user HTTP/1.1\r\nHost: macdev\r\nCookie: sid=abc123\r\n"))
        #expect(SessionProbe.request(site: "macdev", sessionID: "abc\r\nX-Evil: 1") == nil)
        #expect(SessionProbe.request(site: "macdev", sessionID: "") == nil)
        #expect(SessionProbe.request(site: "mac dev", sessionID: "abc") == nil)
    }
}
