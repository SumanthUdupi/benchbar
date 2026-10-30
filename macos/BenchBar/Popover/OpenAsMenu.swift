import SwiftUI

/// What the "Open as…" menu lists, as plain values so the order, the labels
/// and the shortcut digits are tested without a view.
nonisolated enum OpenAsMenuModel {
    enum Item: Equatable, Identifiable {
        case switchBack(user: String, title: String)
        case divider(Int)
        case pin(user: String, title: String, fullName: String?, shortcut: OpenAsShortcut)
        case administrator
        case findUser
        case keepPage

        var id: String {
            switch self {
            case .switchBack: "switch-back"
            case .divider(let n): "divider-\(n)"
            case .pin(let user, _, _, _): "pin-\(user)"
            case .administrator: "administrator"
            case .findUser: "find-user"
            case .keepPage: "keep-page"
            }
        }
    }

    /// The menu is for a developer_mode site whose name resolves, on a
    /// bench that runs: the session lives in the bench's Redis.
    static func isAvailable(_ row: SiteRow, benchRunning: Bool) -> Bool {
        row.developerMode && !row.needsHosts && benchRunning
    }

    /// The pin's label, else the user's full name, else the user id.
    static func label(for user: String, fullName: String?, pins: [PinnedUser]) -> String {
        let pin = pins.first { $0.user == user }
        for candidate in [pin?.label, fullName, pin?.fullName] {
            if let text = candidate?.trimmingCharacters(in: .whitespaces), !text.isEmpty { return text }
        }
        return user
    }

    /// `recents` is newest first: the first is who the browser is now, the
    /// second is where Switch Back goes.
    static func items(pins: [PinnedUser], recents: [RecentUser]) -> [Item] {
        var items: [Item] = []
        if let previous = recents.dropFirst().first {
            let label = label(for: previous.user, fullName: previous.fullName, pins: pins)
            items += [.switchBack(user: previous.user, title: "Switch Back to \(label)"), .divider(1)]
        }
        let pinned = pins.filter { $0.user != OpenAsStore.administrator }.prefix(OpenAsStore.maxPins)
        for (index, pin) in pinned.enumerated() {
            let label = label(for: pin.user, fullName: nil, pins: pins)
            items.append(.pin(user: pin.user, title: label == pin.user ? pin.user : "\(label) — \(pin.user)",
                              fullName: pin.fullName, shortcut: .openAs(index + 1)))
        }
        items += [.administrator, .divider(2), .findUser, .keepPage]
        return items
    }
}

/// The person button of a site row: switch to a pinned user, Administrator or
/// the one before, find any user, and the Keep the page I'm on choice.
struct OpenAsMenu: View {
    let store: BenchStore
    let bench: BenchModel
    let row: SiteRow
    /// The popover's ⌘0 to ⌘9 and ⇧⌘O act on the default site only.
    var shortcuts = false
    let findUser: () -> Void

    var body: some View {
        if OpenAsMenuModel.isAvailable(row, benchRunning: bench.state == .running) {
            menu
        }
    }

    private var openAs: OpenAsStore { store.openAs }
    private var switching: Bool { openAs.switching.contains(OpenAsStore.siteKey(bench: bench.path, site: row.name)) }

    private var items: [OpenAsMenuModel.Item] {
        OpenAsMenuModel.items(pins: openAs.pins(bench: bench.path, site: row.name),
                              recents: openAs.recents(bench: bench.path, site: row.name))
    }

    private var menu: some View {
        Menu {
            ForEach(items) { item in
                switch item {
                case .switchBack(let user, let title):
                    Button(title) { go(user) }.shortcut(.switchBack, if: shortcuts)
                case .divider:
                    Divider()
                case .pin(let user, let title, let fullName, let shortcut):
                    Button(title) { go(user, fullName: fullName) }.shortcut(shortcut, if: shortcuts)
                case .administrator:
                    Button(OpenAsStore.administrator) { go(OpenAsStore.administrator) }.shortcut(.openAs(0), if: shortcuts)
                case .findUser:
                    Button("Find User…", action: findUser)
                case .keepPage:
                    Toggle("Keep the page I'm on", isOn: Binding(get: { store.settings.keepPage },
                                                                 set: { store.settings.keepPage = $0 }))
                }
            }
        } label: {
            if switching {
                ProgressView().controlSize(.small)
            } else {
                Image(systemName: "person.crop.circle")
            }
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .disabled(switching)
        .accessibilityLabel("Open as…")
        .help("Open as…")
        .background { if shortcuts { shortcutButtons } }
    }

    /// The same shortcuts as invisible buttons, so they act while the menu is closed.
    private var shortcutButtons: some View {
        ZStack {
            ForEach(items) { item in
                switch item {
                case .switchBack(let user, _): Button("") { go(user) }.shortcut(.switchBack)
                case .pin(let user, _, let fullName, let shortcut): Button("") { go(user, fullName: fullName) }.shortcut(shortcut)
                case .administrator: Button("") { go(OpenAsStore.administrator) }.shortcut(.openAs(0))
                case .divider, .findUser, .keepPage: EmptyView()
                }
            }
        }
        .opacity(0)
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    private func go(_ user: String, fullName: String? = nil) {
        guard !switching else { return }
        Task { await store.switchUser(user, fullName: fullName, on: bench, row: row) }
    }
}

private extension View {
    @ViewBuilder func shortcut(_ shortcut: OpenAsShortcut, if enabled: Bool) -> some View {
        if enabled { self.shortcut(shortcut) } else { self }
    }
}
