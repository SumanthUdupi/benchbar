import Foundation

/// One bench as Quick Switch sees it: its sites and whether it runs.
nonisolated struct QuickSwitchBench: Equatable, Sendable {
    var path: String
    var running: Bool
    var rows: [SiteRow]
}

/// A site the panel can switch on.
nonisolated struct QuickSwitchTarget: Equatable, Sendable, Identifiable {
    var bench: String
    var site: String
    var siteURL: String

    var id: String { OpenAsStore.siteKey(bench: bench, site: site) }
}

nonisolated enum QuickSwitchKey: Equatable, Sendable {
    case up, down, confirm, escape, tab
    /// 0 is Administrator, 1 to 9 the pins.
    case digit(Int)
}

nonisolated struct QuickSwitchRequest: Equatable, Sendable {
    var user: String
    var fullName: String?
    var target: QuickSwitchTarget
}

nonisolated enum QuickSwitchAction: Equatable, Sendable {
    case none
    case close
    case switchTo(QuickSwitchRequest)
}

nonisolated struct QuickSwitchRow: Equatable, Sendable, Identifiable {
    enum Kind: Equatable, Sendable { case switchBack, pin, administrator, other }

    var kind: Kind
    var user: String
    var fullName: String?
    var title: String
    /// The user id under a title that is a label or a name, nil when the title is the id.
    var detail: String?
    var isCurrent: Bool
    /// The key that switches at once while the field is empty: 0 Administrator, 1 to 9 the pins.
    var digit: Int?
    /// What typing filters on: label, id and full name.
    var searchable: [String]

    var id: String {
        switch kind {
        case .switchBack: "switch-back"
        case .pin: "pin-\(user)"
        case .administrator: "administrator"
        case .other: "other-\(user)"
        }
    }

    /// Shown next to the ✓.
    var hint: String? { isCurrent ? "current" : nil }
}

/// Everything the Quick Switch panel decides, as plain values: the rows, the
/// filter, the selection, the keys and the one-switch-per-Return guard. The
/// panel and its view only draw this and report keys.
///
/// It holds a snapshot of the pins and recents taken when the panel opened:
/// nothing but the switch itself changes state while the panel is up.
nonisolated struct QuickSwitchModel: Equatable, Sendable {
    static let minSearchLength = 2
    static let searchDebounce: Duration = .milliseconds(250)
    static let searchLimit = 8
    static let noMatches = "No users match"
    static let searchFailedCopy = "Could not search users"

    enum Blocker: Equatable, Sendable {
        case noDeveloperSite
        case benchStopped

        var text: String {
            switch self {
            case .noDeveloperSite: "No site in developer mode — run benchbar open --help"
            case .benchStopped: "Start the bench first (⌘U in BenchBar)"
            }
        }
    }

    enum Phase: Equatable, Sendable {
        case idle
        case switching(user: String)
        case failed(String)
        case finished
    }

    struct SearchRequest: Equatable, Sendable {
        var target: QuickSwitchTarget
        var query: String
        var debounce: Duration
        var limit: Int
    }

    let targets: [QuickSwitchTarget]
    let blocker: Blocker?
    private let pinsBySite: [String: [PinnedUser]]
    private let recentsBySite: [String: [RecentUser]]

    private(set) var siteIndex = 0
    private(set) var query = ""
    private(set) var selection = 0
    private(set) var phase: Phase = .idle
    private var remote: [SiteUser] = []
    private var remoteQuery: String?
    private var remoteFailed = false

    // MARK: building

    /// The selected bench's sites first, then the others'. Only a running
    /// bench's developer_mode site that resolves is offered, as in the menu.
    static func targets(benches: [QuickSwitchBench], selectedPath: String?) -> [QuickSwitchTarget] {
        let ordered = benches.filter { $0.path == selectedPath } + benches.filter { $0.path != selectedPath }
        return ordered.flatMap { bench in
            bench.rows
                .filter { OpenAsMenuModel.isAvailable($0, benchRunning: bench.running) }
                .map { QuickSwitchTarget(bench: bench.path, site: $0.name, siteURL: $0.url) }
        }
    }

    /// Why there is nothing to switch on: a stopped bench with a developer
    /// site is fixed by starting it, anything else needs developer mode.
    static func blocker(benches: [QuickSwitchBench]) -> Blocker {
        let startable = benches.contains { bench in
            !bench.running && bench.rows.contains { $0.developerMode && !$0.needsHosts }
        }
        return startable ? .benchStopped : .noDeveloperSite
    }

    /// `lastSwitchedKey` is the site the last switch happened on; without it
    /// (or when it is not offered) the first target, the selected bench's
    /// default site, opens.
    init(benches: [QuickSwitchBench], selectedPath: String?, lastSwitchedKey: String?,
         pins: [String: [PinnedUser]], recents: [String: [RecentUser]]) {
        targets = Self.targets(benches: benches, selectedPath: selectedPath)
        blocker = targets.isEmpty ? Self.blocker(benches: benches) : nil
        pinsBySite = pins
        recentsBySite = recents
        siteIndex = targets.firstIndex { $0.id == lastSwitchedKey } ?? 0
    }

    // MARK: what the panel shows

    var target: QuickSwitchTarget? { targets.indices.contains(siteIndex) ? targets[siteIndex] : nil }

    var title: String { target.map { "Open \($0.site) as…" } ?? "Open as…" }

    var canCycleSites: Bool { targets.count > 1 }

    var trimmedQuery: String { query.trimmingCharacters(in: .whitespaces) }

    var isSwitching: Bool {
        if case .switching = phase { true } else { false }
    }

    var switchingUser: String? {
        if case .switching(let user) = phase { user } else { nil }
    }

    var errorMessage: String? {
        if case .failed(let text) = phase { text } else { nil }
    }

    var isFinished: Bool { phase == .finished }

    /// Switch Back, the pins and Administrator, filtered by what is typed.
    var rows: [QuickSwitchRow] {
        let all = baseRows
        let needle = trimmedQuery
        guard !needle.isEmpty else { return all }
        return all.filter { row in row.searchable.contains { $0.localizedCaseInsensitiveContains(needle) } }
    }

    /// The site's other users the search found, below the matches.
    var otherRows: [QuickSwitchRow] {
        guard remoteQuery == trimmedQuery, let target else { return [] }
        let pins = pinsBySite[target.id] ?? []
        let shown = Set(rows.map(\.user))
        return remote.filter { !shown.contains($0.name) }.map { user in
            let title = OpenAsMenuModel.label(for: user.name, fullName: user.fullName, pins: pins)
            return QuickSwitchRow(kind: .other, user: user.name, fullName: user.fullName, title: title,
                                  detail: title == user.name ? nil : user.name, isCurrent: user.name == currentUser,
                                  digit: nil, searchable: [])
        }
    }

    /// What ↑ ↓ ↩ move over: the matches, then the other users.
    var selectable: [QuickSwitchRow] { rows + otherRows }

    var searching: Bool { searchRequest != nil && remoteQuery != trimmedQuery }

    /// "No users match" once typing left nothing to pick and nothing is still being asked.
    var emptyResultCopy: String? {
        guard !trimmedQuery.isEmpty, selectable.isEmpty, !searching else { return nil }
        return remoteFailed && remoteQuery == trimmedQuery ? Self.searchFailedCopy : Self.noMatches
    }

    /// The one-line explanation shown instead of the list.
    var blockerText: String? { blocker?.text }

    /// Digits switch only while the field is empty; typed, they are text.
    var digitsSwitch: Bool { query.isEmpty }

    /// Set when typing reached the length that asks the site's users. The
    /// view waits `debounce`, then asks; a newer request cancels it.
    var searchRequest: SearchRequest? {
        guard let target, trimmedQuery.count >= Self.minSearchLength, !isSwitching else { return nil }
        return SearchRequest(target: target, query: trimmedQuery, debounce: Self.searchDebounce, limit: Self.searchLimit)
    }

    private var pins: [PinnedUser] { target.flatMap { pinsBySite[$0.id] } ?? [] }
    private var recents: [RecentUser] { target.flatMap { recentsBySite[$0.id] } ?? [] }
    private var currentUser: String? { recents.first?.user }

    private var baseRows: [QuickSwitchRow] {
        let pins = pins
        let current = currentUser
        let items = OpenAsMenuModel.items(pins: pins, recents: recents)
        return items.compactMap { item -> QuickSwitchRow? in
            switch item {
            case .switchBack(let user, let title):
                let name = recents.dropFirst().first?.fullName
                let label = OpenAsMenuModel.label(for: user, fullName: name, pins: pins)
                return QuickSwitchRow(kind: .switchBack, user: user, fullName: name, title: title,
                                      detail: nil, isCurrent: user == current, digit: nil,
                                      searchable: [label, user, name ?? ""])
            case .pin(let user, _, let fullName, let shortcut):
                let label = OpenAsMenuModel.label(for: user, fullName: nil, pins: pins)
                return QuickSwitchRow(kind: .pin, user: user, fullName: fullName, title: label,
                                      detail: label == user ? nil : user, isCurrent: user == current,
                                      digit: Self.digit(of: shortcut), searchable: [label, user, fullName ?? ""])
            case .administrator:
                return QuickSwitchRow(kind: .administrator, user: OpenAsStore.administrator, fullName: nil,
                                      title: OpenAsStore.administrator, detail: nil,
                                      isCurrent: current == OpenAsStore.administrator, digit: 0,
                                      searchable: [OpenAsStore.administrator])
            case .divider, .findUser, .keepPage:
                return nil
            }
        }
    }

    private static func digit(of shortcut: OpenAsShortcut) -> Int? {
        if case .openAs(let digit) = shortcut { digit } else { nil }
    }

    // MARK: input

    /// The key the panel acts on for a key press, nil when the press is
    /// typing and belongs to the text field.
    func key(keyCode: UInt16, characters: String?, hasModifier: Bool) -> QuickSwitchKey? {
        if hasModifier { return nil }
        switch keyCode {
        case 126: return .up
        case 125: return .down
        case 36, 76: return .confirm
        case 53: return .escape
        case 48: return .tab
        default:
            guard digitsSwitch, let text = characters, text.count == 1, let digit = Int(text) else { return nil }
            return .digit(digit)
        }
    }

    mutating func setQuery(_ text: String) {
        guard text != query else { return }
        query = text
        selection = 0
        if case .failed = phase { phase = .idle }
    }

    mutating func handle(_ key: QuickSwitchKey) -> QuickSwitchAction {
        if key == .escape { return .close }
        guard target != nil, !isSwitching else { return .none }
        switch key {
        case .up: move(-1)
        case .down: move(1)
        case .tab: cycleSite()
        case .confirm:
            guard selectable.indices.contains(selection) else { return .none }
            return begin(selectable[selection])
        case .digit(let digit):
            guard digitsSwitch, let row = rows.first(where: { $0.digit == digit }) else { return .none }
            return begin(row)
        case .escape: return .close
        }
        return .none
    }

    /// A search answered. An answer for text that is no longer typed is dropped.
    mutating func applySearch(query asked: String, users: [SiteUser]) {
        guard asked == trimmedQuery else { return }
        remote = users
        remoteQuery = asked
        remoteFailed = false
        clampSelection()
    }

    mutating func searchFailed(query asked: String) {
        guard asked == trimmedQuery else { return }
        remote = []
        remoteQuery = asked
        remoteFailed = true
    }

    /// The switch ended: nil closes the panel, a message stays inline and allows another try.
    mutating func finishSwitch(error: String?) {
        phase = error.map(Phase.failed) ?? .finished
    }

    private mutating func begin(_ row: QuickSwitchRow) -> QuickSwitchAction {
        guard let target else { return .none }
        phase = .switching(user: row.user)
        return .switchTo(QuickSwitchRequest(user: row.user, fullName: row.fullName, target: target))
    }

    private mutating func move(_ step: Int) {
        let count = selectable.count
        guard count > 0 else { return }
        selection = (selection + step + count) % count
    }

    private mutating func cycleSite() {
        guard canCycleSites else { return }
        siteIndex = (siteIndex + 1) % targets.count
        selection = 0
        remote = []
        remoteQuery = nil
        remoteFailed = false
        phase = .idle
    }

    private mutating func clampSelection() {
        selection = min(selection, max(selectable.count - 1, 0))
    }
}
