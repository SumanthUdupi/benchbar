import Foundation
import Testing
@testable import BenchBar

@Suite("Quick Switch")
struct QuickSwitchTests {
    let bench = "/Users/you/frappe-bench"

    func row(_ name: String, developerMode: Bool = true, isDefault: Bool = false, needsHosts: Bool = false) -> SiteRow {
        SiteRow(name: name, isDefault: isDefault, url: "http://\(name):8000", needsHosts: needsHosts, developerMode: developerMode)
    }

    var devBench: QuickSwitchBench {
        QuickSwitchBench(path: bench, running: true, rows: [row("macdev", isDefault: true), row("prod.example", developerMode: false), row("second")])
    }

    func pin(_ user: String, _ label: String? = nil, _ fullName: String? = nil) -> PinnedUser {
        PinnedUser(user: user, label: label, fullName: fullName)
    }

    func model(pins: [PinnedUser] = [], recents: [RecentUser] = [], benches: [QuickSwitchBench]? = nil,
               last: String? = nil, site: String = "macdev") -> QuickSwitchModel {
        let key = OpenAsStore.siteKey(bench: bench, site: site)
        return QuickSwitchModel(benches: benches ?? [devBench], selectedPath: bench, lastSwitchedKey: last,
                                pins: [key: pins], recents: [key: recents])
    }

    let ne = PinnedUser(user: "ne@x.com", label: "NE Pune", fullName: "Priya Nair")
    let tech = PinnedUser(user: "tech@x.com", label: nil, fullName: "Ravi Kumar")

    func recents(_ users: String...) -> [RecentUser] { users.map { RecentUser(user: $0, fullName: nil) } }

    @Test func rowsAreSwitchBackThenPinsThenAdministrator() {
        let m = model(pins: [ne, tech], recents: recents("tech@x.com", "ne@x.com"))
        #expect(m.rows.map(\.kind) == [.switchBack, .pin, .pin, .administrator])
        #expect(m.rows.map(\.user) == ["ne@x.com", "ne@x.com", "tech@x.com", "Administrator"])
        #expect(m.rows[0].title == "Switch Back to NE Pune")
    }

    @Test func defaultSelectionIsSwitchBackElseTheFirstPin() {
        let withBack = model(pins: [ne, tech], recents: recents("tech@x.com", "ne@x.com"))
        #expect(withBack.selectable[withBack.selection].kind == .switchBack)
        let pinsOnly = model(pins: [ne, tech])
        #expect(pinsOnly.selectable[pinsOnly.selection].user == "ne@x.com")
        let nothing = model()
        #expect(nothing.selectable[nothing.selection].kind == .administrator)
    }

    @Test func upAndDownWrapAround() {
        var m = model(pins: [ne, tech])
        #expect(m.selectable.count == 3)
        _ = m.handle(.up)
        #expect(m.selection == 2)
        _ = m.handle(.down)
        #expect(m.selection == 0)
        _ = m.handle(.down)
        _ = m.handle(.down)
        _ = m.handle(.down)
        #expect(m.selection == 0)
    }

    @Test func digitsSwitchAdministratorAndThePinsAtOnce() {
        var m = model(pins: [ne, tech])
        #expect(m.handle(.digit(2)) == .switchTo(QuickSwitchRequest(user: "tech@x.com", fullName: "Ravi Kumar", target: m.target!)))
        var admin = model(pins: [ne])
        guard case .switchTo(let request) = admin.handle(.digit(0)) else { Issue.record("0 did not switch"); return }
        #expect(request.user == "Administrator")
        var none = model(pins: [ne])
        #expect(none.handle(.digit(5)) == .none)
        #expect(!none.isSwitching)
    }

    @Test func digitsAreTextOnceSomethingIsTyped() {
        var m = model(pins: [ne])
        m.setQuery("a")
        #expect(m.key(keyCode: 18, characters: "1", hasModifier: false) == nil)
        #expect(m.handle(.digit(1)) == .none)
        m.setQuery("")
        #expect(m.key(keyCode: 18, characters: "1", hasModifier: false) == .digit(1))
    }

    @Test func keyCodesMapToKeys() {
        let m = model()
        #expect(m.key(keyCode: 126, characters: nil, hasModifier: false) == .up)
        #expect(m.key(keyCode: 125, characters: nil, hasModifier: false) == .down)
        #expect(m.key(keyCode: 36, characters: "\r", hasModifier: false) == .confirm)
        #expect(m.key(keyCode: 53, characters: nil, hasModifier: false) == .escape)
        #expect(m.key(keyCode: 48, characters: "\t", hasModifier: false) == .tab)
        #expect(m.key(keyCode: 0, characters: "a", hasModifier: false) == nil)
        #expect(m.key(keyCode: 18, characters: "1", hasModifier: true) == nil)
    }

    @Test func typingFiltersByLabelIdAndFullName() {
        var m = model(pins: [ne, tech])
        m.setQuery("pune")
        #expect(m.rows.map(\.user) == ["ne@x.com"])
        m.setQuery("TECH@")
        #expect(m.rows.map(\.user) == ["tech@x.com"])
        m.setQuery("ravi")
        #expect(m.rows.map(\.user) == ["tech@x.com"])
        m.setQuery("priya")
        #expect(m.rows.map(\.user) == ["ne@x.com"])
        m.setQuery("admin")
        #expect(m.rows.map(\.user) == ["Administrator"])
    }

    @Test func twoCharactersAskForASearchAfterTheDebounceAndOneDoesNot() {
        var m = model(pins: [ne])
        m.setQuery("p")
        #expect(m.searchRequest == nil)
        m.setQuery("pr")
        let request = m.searchRequest
        #expect(request?.query == "pr")
        #expect(request?.debounce == .milliseconds(250))
        #expect(request?.limit == 8)
        #expect(request?.target.site == "macdev")
        m.setQuery(" p ")
        #expect(m.searchRequest == nil)
    }

    @Test func searchResultsLandBelowTheMatchesAndStaleOnesAreDropped() {
        var m = model(pins: [ne])
        m.setQuery("ne")
        let found = [SiteUser(name: "ne@x.com", fullName: "Priya Nair", userType: "System User", roles: [], roleCount: 0, lastLogin: nil),
                     SiteUser(name: "neha@x.com", fullName: "Neha Rao", userType: "System User", roles: [], roleCount: 0, lastLogin: nil)]
        m.applySearch(query: "n", users: found)
        #expect(m.otherRows.isEmpty)
        m.applySearch(query: "ne", users: found)
        #expect(m.rows.map(\.user) == ["ne@x.com"])
        #expect(m.otherRows.map(\.user) == ["neha@x.com"])
        #expect(m.otherRows.first?.title == "Neha Rao")
        #expect(m.selectable.map(\.user) == ["ne@x.com", "neha@x.com"])
        m.setQuery("nex")
        #expect(m.otherRows.isEmpty)
    }

    @Test func tabCyclesOnlyDeveloperModeSites() {
        var m = model(pins: [ne])
        #expect(m.targets.map(\.site) == ["macdev", "second"])
        #expect(m.title == "Open macdev as…")
        _ = m.handle(.tab)
        #expect(m.title == "Open second as…")
        _ = m.handle(.tab)
        #expect(m.title == "Open macdev as…")
        var single = model(benches: [QuickSwitchBench(path: bench, running: true, rows: [row("macdev", isDefault: true)])])
        _ = single.handle(.tab)
        #expect(single.title == "Open macdev as…")
    }

    @Test func aSiteThatNeedsAHostsLineOrAStoppedBenchIsNotOffered() {
        let hosts = QuickSwitchBench(path: bench, running: true, rows: [row("macdev", needsHosts: true)])
        #expect(QuickSwitchModel.targets(benches: [hosts], selectedPath: bench).isEmpty)
        let stopped = QuickSwitchBench(path: bench, running: false, rows: [row("macdev")])
        #expect(QuickSwitchModel.targets(benches: [stopped], selectedPath: bench).isEmpty)
    }

    @Test func itOpensOnTheLastSwitchedSiteElseTheSelectedBenchsDefault() {
        let second = OpenAsStore.siteKey(bench: bench, site: "second")
        #expect(model(last: second).title == "Open second as…")
        #expect(model(last: "gone|site").title == "Open macdev as…")
        #expect(model().title == "Open macdev as…")
        let other = QuickSwitchBench(path: "/other", running: true, rows: [row("other.site", isDefault: true)])
        let m = QuickSwitchModel(benches: [other, devBench], selectedPath: bench, lastSwitchedKey: nil, pins: [:], recents: [:])
        #expect(m.title == "Open macdev as…")
    }

    @Test func theCurrentUserCarriesATickAndTheWordCurrent() {
        let m = model(pins: [ne, tech], recents: recents("tech@x.com", "ne@x.com"))
        let current = m.rows.filter(\.isCurrent)
        #expect(current.map(\.user) == ["tech@x.com"])
        #expect(current.first?.hint == "current")
        #expect(m.rows.first { $0.user == "ne@x.com" && $0.kind == .pin }?.hint == nil)
        let admin = model(pins: [ne], recents: recents("Administrator"))
        #expect(admin.rows.first { $0.kind == .administrator }?.isCurrent == true)
    }

    @Test func emptyAndStoppedStatesUseTheExactCopy() {
        let none = QuickSwitchBench(path: bench, running: true, rows: [row("macdev", developerMode: false)])
        #expect(model(benches: [none]).blockerText == "No site in developer mode — run benchbar open --help")
        let stopped = QuickSwitchBench(path: bench, running: false, rows: [row("macdev")])
        #expect(model(benches: [stopped]).blockerText == "Start the bench first (⌘U in BenchBar)")
        #expect(model().blockerText == nil)
        var blocked = model(benches: [none])
        #expect(blocked.handle(.confirm) == .none)
        #expect(blocked.handle(.escape) == .close)
    }

    @Test func noMatchesSaysSoOnceNothingIsLeftAndNothingIsPending() {
        var m = model(pins: [ne])
        m.setQuery("z")
        #expect(m.emptyResultCopy == "No users match")
        m.setQuery("zz")
        #expect(m.emptyResultCopy == nil)
        m.applySearch(query: "zz", users: [])
        #expect(m.emptyResultCopy == "No users match")
    }

    @Test func returnOnARowIsOneSwitchEvenWhenPressedTwice() {
        var m = model(pins: [ne, tech], recents: recents("tech@x.com", "ne@x.com"))
        let first = m.handle(.confirm)
        guard case .switchTo(let request) = first else { Issue.record("Return did not switch"); return }
        #expect(request.user == "ne@x.com")
        #expect(request.target.site == "macdev")
        #expect(m.isSwitching && m.switchingUser == "ne@x.com")
        #expect(m.handle(.confirm) == .none)
        #expect(m.handle(.digit(1)) == .none)
        #expect(m.handle(.down) == .none)
        #expect(m.handle(.escape) == .close)
    }

    @Test func aFailedSwitchStaysInlineAndAllowsAnotherTry() {
        var m = model(pins: [ne])
        _ = m.handle(.confirm)
        m.finishSwitch(error: "benchbar could not sign in")
        #expect(m.errorMessage == "benchbar could not sign in")
        #expect(!m.isFinished)
        guard case .switchTo = m.handle(.confirm) else { Issue.record("retry did not switch"); return }
        m.finishSwitch(error: nil)
        #expect(m.isFinished && m.errorMessage == nil)
    }
}
