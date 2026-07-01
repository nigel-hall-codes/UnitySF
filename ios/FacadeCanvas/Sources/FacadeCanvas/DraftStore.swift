import Foundation

/// Local snapshot cache + pending-write outbox for the iPad authoring client (D3 design).
///
/// Architecture: server is the source of truth; the draft store holds a last-known-good
/// snapshot for offline browsing and an outbox of writes that failed or haven't been
/// attempted yet. Sync semantics are LWW (last-write-wins) — safe because the server
/// keys every resource by a stable id (osm_id, signId, etc.) and all writes are idempotent
/// upserts.
///
/// Storage is file-based JSON (no Core Data dependency) so the package stays unit-testable
/// without an iOS simulator. Each snapshot is written atomically via a temp-file rename.
public actor DraftStore {

    // MARK: - Types

    public enum SyncStatus: Equatable, Sendable {
        case synced
        case pending(count: Int)
        case syncing
        case error(String)
    }

    /// A pending canvas write that failed or hasn't been flushed yet.
    public struct PendingWrite: Codable, Sendable {
        public var id: String       // "\(osm_id)/\(facade)"
        public var canvas: FacadeCanvas
        public var enqueuedAt: Date
        public var attempts: Int

        public init(canvas: FacadeCanvas, enqueuedAt: Date = Date()) {
            self.id = "\(canvas.osm_id)/\(canvas.facade)"
            self.canvas = canvas
            self.enqueuedAt = enqueuedAt
            self.attempts = 0
        }
    }

    // MARK: - State

    private(set) var buildings: [BuildingFacts] = []
    private(set) var outbox: [PendingWrite] = []
    private(set) var syncStatus: SyncStatus = .synced

    private let storeURL: URL
    private let outboxURL: URL

    // MARK: - Init

    /// - Parameter directory: directory for snapshot files. Defaults to the app's Caches folder.
    public init(directory: URL? = nil) {
        let dir = directory ?? FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("FacadeCanvasDraftStore", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        storeURL = dir.appendingPathComponent("buildings.json")
        outboxURL = dir.appendingPathComponent("outbox.json")
        // Load persisted state synchronously at init (small files, acceptable).
        if let data = try? Data(contentsOf: storeURL),
           let decoded = try? JSONDecoder().decode([BuildingFacts].self, from: data) {
            buildings = decoded
        }
        if let data = try? Data(contentsOf: outboxURL),
           let decoded = try? JSONDecoder().decode([PendingWrite].self, from: data) {
            outbox = decoded
        }
        syncStatus = outbox.isEmpty ? .synced : .pending(count: outbox.count)
    }

    // MARK: - Snapshot

    /// Replace the local building snapshot with a fresh server page. Called after a
    /// successful GET /buildings.
    public func updateBuildings(_ buildings: [BuildingFacts]) {
        self.buildings = buildings
        persist(buildings, to: storeURL)
    }

    // MARK: - Outbox

    /// Enqueue a canvas save for later retry. Called when `ServerClient.saveCanvas` throws.
    public func enqueue(_ canvas: FacadeCanvas) {
        let write = PendingWrite(canvas: canvas)
        // Deduplicate by id (same osm_id/facade overwrites previous pending write — LWW).
        outbox.removeAll { $0.id == write.id }
        outbox.append(write)
        persistOutbox()
        syncStatus = .pending(count: outbox.count)
    }

    /// Attempt to flush the outbox via the provided client. Idempotent — safe to call
    /// on reconnect or app foreground. Does not throw: individual failures are re-queued.
    public func flush(using client: ServerClient) async {
        guard !outbox.isEmpty else { return }
        syncStatus = .syncing
        var remaining: [PendingWrite] = []
        for var write in outbox {
            do {
                _ = try await client.saveCanvas(write.canvas)
            } catch {
                write.attempts += 1
                remaining.append(write)
            }
        }
        outbox = remaining
        persistOutbox()
        syncStatus = outbox.isEmpty ? .synced : .pending(count: outbox.count)
    }

    /// Remove a successfully saved canvas from the outbox (called by the view after an
    /// online save completes without error so no re-queueing is needed).
    public func dequeue(osmId: Int, facade: String) {
        let key = "\(osmId)/\(facade)"
        outbox.removeAll { $0.id == key }
        persistOutbox()
        syncStatus = outbox.isEmpty ? .synced : .pending(count: outbox.count)
    }

    // MARK: - Persistence (private)

    private func persist<T: Encodable>(_ value: T, to url: URL) {
        guard let data = try? JSONEncoder().encode(value) else { return }
        let tmp = url.deletingLastPathComponent().appendingPathComponent(UUID().uuidString)
        try? data.write(to: tmp, options: .atomic)
        try? FileManager.default.replaceItemAt(url, withItemAt: tmp)
    }

    private func persistOutbox() {
        persist(outbox, to: outboxURL)
    }
}
