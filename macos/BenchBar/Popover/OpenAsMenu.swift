import SwiftUI

/// What the "Open as…" menu lists, as plain values so the order, the labels
/// and the shortcut digits are tested without a view.
nonisolated enum OpenAsMenuModel {
    enum Item: Equatable, Identifiable {
        case switchBack(user: String, title: String)
        case divider(Int)
        case pin(user: String, title: String, fullName: String?, shortcut: OpenAsShortcut)
        case administrator
        /// Shown while nobody is pinned: says how to pin.
        case pinHint
        case findUser
        case keepPage

        var id: String {
            switch self {
            case .switchBack: "switch-back"
            case .divider(let n): "divider-\(n)"
            case .pin(let user, _, _, _): "pin-\(user)"
            case .administrator: "administrator"
            case .pinHint: "pin-hint"
            case .findUser: "find-user"
            case .keepPage: "keep-page"
            }
        }
    }

    static let noPinsHint = "Pin people from Find User… to switch to them here"

    /// Why the control is or is not usable. Open as works on the bench's
    /// default site only: `bench serve` runs with that one site, so port 8000
    /// serves nothing else and a session made in another site's database
    /// would land the browser logged out.
    enum Availability: Equatable {
        case available
        /// "Set Up…" is the row's action, not this control.
        case needsHosts
        case notDefaultSite
        case cliTooOld
        case developerModeOff
        case benchStopped
    }

    static func availability(_ row: SiteRow, benchRunning: Bool) -> Availability {
        if row.needsHosts { return .needsHosts }
        // the CLI says which site bench serve answers as; BenchBar's default can lag a `bench use` run by hand
        if row.developerMode, let openAs = row.openAs {
            if !openAs { return .notDefaultSite }
        } else if !row.isDefault {
            return .notDefaultSite
        }
        if !row.developerModeKnown { return .cliTooOld }
        if !row.developerMode { return .developerModeOff }
        return benchRunning ? .available : .benchStopped
    }

    static func isAvailable(_ row: SiteRow, benchRunning: Bool) -> Bool {
        availability(row, benchRunning: benchRunning) == .available
    }

    static let notDefaultSiteHelp = "Open as works on the default site; port 8000 serves only it"

    /// The tooltip of the disabled control, nil when it is usable or not shown.
    static func disabledHelp(_ availability: Availability, site: String, bench: String) -> String? {
        switch availability {
        case .available, .needsHosts: nil
        case .notDefaultSite: notDefaultSiteHelp
        case .cliTooOld: "Update the benchbar CLI to use Open as"
        case .developerModeOff: "Turn on developer_mode for \(site) to use Open as"
        case .benchStopped: "Start \(bench) first"
        }
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
        items.append(.administrator)
        if pinned.isEmpty { items.append(.pinHint) }
        items += [.divider(2), .findUser, .keepPage]
        return items
    }
}

/// The "Open as…" control of a site row: switch to a pinned user,
/// Administrator or the one before, find any user, and the Keep the page I'm
/// on choice. Disabled, with the reason as its tooltip, while the site cannot do it.
struct OpenAsMenu: View {
    let store: BenchStore
    let bench: BenchModel
    let row: SiteRow
    /// The popover's ⌘0 to ⌘9 and ⇧⌘O act on the default site only.
    var shortcuts = false
    var compact = false
    let findUser: () -> Void

    var body: some View {
        let availability = OpenAsMenuModel.availability(row, benchRunning: bench.state == .running)
        switch availability {
        case .available:
            menu
        case .needsHosts, .notDefaultSite:
            EmptyView()
        case .cliTooOld, .developerModeOff, .benchStopped:
            Button { } label: { Label("Open as…", systemImage: Self.icon) }
                .controlSize(compact ? .small : .regular)
                .disabled(true)
                .help(OpenAsMenuModel.disabledHelp(availability, site: row.name, bench: bench.name) ?? "")
        }
    }

    private static let icon = "person.crop.circle"

    private var openAs: OpenAsStore { store.openAs }
    private var switching: Bool { openAs.isSwitching(bench: bench.path, site: row.name) }

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
                case .pinHint:
                    Text(OpenAsMenuModel.noPinsHint)
                case .findUser:
                    Button("Find User…", action: findUser)
                case .keepPage:
                    Toggle("Keep the page I'm on", isOn: Binding(get: { store.settings.keepPage },
                                                                 set: { store.settings.keepPage = $0 }))
                }
            }
        } label: {
            HStack(spacing: 4) {
                if switching {
                    ProgressView().controlSize(.small)
                } else {
                    Image(systemName: Self.icon)
                }
                Text("Open as…")
            }
        }
        .menuStyle(.button)
        .menuIndicator(.hidden)
        .controlSize(compact ? .small : .regular)
        .fixedSize()
        .disabled(switching)
        .help("Open \(row.name) as another user")
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
                case .divider, .pinHint, .findUser, .keepPage: EmptyView()
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

/// The one-time note about Keep the page I'm on (permission, Firefox), with a way to dismiss it.
struct OpenAsNoticeLine: View {
    let store: BenchStore

    var body: some View {
        if let note = store.openAs.notice {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Label(note, systemImage: "info.circle")
                    .font(.caption)
                    .foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 4)
                Button("Got It") { store.openAs.dismissNotice() }
                    .controlSize(.small)
            }
        }
    }
}
