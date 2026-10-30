import Observation
import SwiftUI

/// The state behind one showing of the panel. The model decides; this runs
/// the two things it cannot, the switch and the user search, and tells the
/// panel when to close.
@Observable
final class QuickSwitchSession {
    typealias Switcher = (QuickSwitchRequest) async -> String?
    typealias UserSearch = (QuickSwitchTarget, String, Int) async -> [SiteUser]?

    private(set) var model: QuickSwitchModel

    @ObservationIgnored private let switcher: Switcher
    @ObservationIgnored private let userSearch: UserSearch
    @ObservationIgnored private let close: () -> Void

    init(model: QuickSwitchModel, switcher: @escaping Switcher, userSearch: @escaping UserSearch,
         close: @escaping () -> Void) {
        self.model = model
        self.switcher = switcher
        self.userSearch = userSearch
        self.close = close
    }

    var query: String {
        get { model.query }
        set { model.setQuery(newValue) }
    }

    func press(_ key: QuickSwitchKey) {
        switch model.handle(key) {
        case .none: break
        case .close: close()
        case .switchTo(let request): Task { await run(request) }
        }
    }

    /// Waits out the debounce, then asks. The view runs this under `.task(id:)`,
    /// so typing again cancels the sleep and the older answer never lands.
    func search(_ request: QuickSwitchModel.SearchRequest) async {
        do { try await Task.sleep(for: request.debounce) } catch { return }
        let users = await userSearch(request.target, request.query, request.limit)
        guard !Task.isCancelled else { return }
        if let users {
            model.applySearch(query: request.query, users: users)
        } else {
            model.searchFailed(query: request.query)
        }
    }

    private func run(_ request: QuickSwitchRequest) async {
        let error = await switcher(request)
        model.finishSwitch(error: error)
        if error == nil { close() }
    }
}

struct QuickSwitchView: View {
    static let width: CGFloat = 460
    private static let rowHeight: CGFloat = 34
    private static let sectionHeight: CGFloat = 24
    private static let listLimit: CGFloat = 320

    let session: QuickSwitchSession
    @FocusState private var typing: Bool

    private var model: QuickSwitchModel { session.model }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            if let text = model.blockerText {
                Text(text)
                    .foregroundStyle(.secondary)
                    .padding(16)
                    .frame(maxWidth: .infinity, alignment: .leading)
            } else {
                field
                Divider()
                list
                if let error = model.errorMessage {
                    Text(error)
                        .font(.callout)
                        .foregroundStyle(.red)
                        .padding(.horizontal, 16)
                        .padding(.vertical, 8)
                        .fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                Divider()
                footer
            }
        }
        .frame(width: Self.width)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(.separator))
        .shadow(color: .black.opacity(0.25), radius: 18, y: 8)
        .padding(24)
        .frame(maxHeight: .infinity, alignment: .top)
        .onAppear { typing = true }
        .task(id: model.searchRequest) {
            if let request = model.searchRequest { await session.search(request) }
        }
    }

    private var header: some View {
        HStack {
            Text(model.title).font(.headline)
            Spacer()
            if model.canCycleSites {
                Text("⇥ next site").font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
    }

    private var field: some View {
        TextField("Type to filter", text: Binding(get: { session.query }, set: { session.query = $0 }))
            .textFieldStyle(.plain)
            .focused($typing)
            .padding(.horizontal, 16)
            .padding(.bottom, 10)
            .disabled(model.isSwitching)
    }

    private var list: some View {
        let rows = model.rows
        let others = model.otherRows
        let selectedID = model.selectable.indices.contains(model.selection) ? model.selectable[model.selection].id : nil
        let height = Self.listHeight(rows: rows.count, others: others.count, hasMessage: model.emptyResultCopy != nil || model.searching)
        return ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    ForEach(rows) { row in line(row, selected: row.id == selectedID) }
                    if !others.isEmpty || model.searching && !rows.isEmpty {
                        sectionTitle("Other users")
                        ForEach(others) { row in line(row, selected: row.id == selectedID) }
                    }
                    if let copy = model.emptyResultCopy {
                        Text(copy).foregroundStyle(.secondary).padding(.horizontal, 16).frame(height: Self.rowHeight)
                    } else if model.searching && rows.isEmpty {
                        ProgressView().controlSize(.small).padding(.horizontal, 16).frame(height: Self.rowHeight)
                    }
                }
            }
            .frame(height: height)
            .onChange(of: selectedID) { _, id in
                if let id { proxy.scrollTo(id) }
            }
        }
    }

    private static func listHeight(rows: Int, others: Int, hasMessage: Bool) -> CGFloat {
        let sections = others > 0 ? sectionHeight : 0
        let message = hasMessage ? rowHeight : 0
        return min(CGFloat(rows + others) * rowHeight + sections + message, listLimit)
    }

    private func sectionTitle(_ text: String) -> some View {
        Text(text)
            .font(.caption.weight(.semibold))
            .foregroundStyle(.secondary)
            .padding(.horizontal, 16)
            .frame(height: Self.sectionHeight, alignment: .bottom)
    }

    private func line(_ row: QuickSwitchRow, selected: Bool) -> some View {
        HStack(spacing: 10) {
            Text(model.digitsSwitch ? row.digit.map { String($0) } ?? "" : "")
                .font(.system(.caption, design: .monospaced))
                .foregroundStyle(.secondary)
                .frame(width: 12)
            Text(row.title).lineLimit(1)
            if let detail = row.detail {
                Text(detail).font(.callout).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 8)
            if model.switchingUser == row.user {
                ProgressView().controlSize(.small)
            } else if let hint = row.hint {
                Text(hint).font(.caption).foregroundStyle(.secondary)
                Image(systemName: "checkmark")
            }
        }
        .padding(.horizontal, 16)
        .frame(height: Self.rowHeight)
        .background(selected ? Color.accentColor.opacity(0.22) : .clear)
        .id(row.id)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    private var footer: some View {
        Text("↑↓ select   ↩ switch   0–9 quick pick   ⎋ close")
            .font(.caption)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 16)
            .padding(.vertical, 8)
    }
}
