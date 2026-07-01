import Foundation

/// The ONLY component that talks off-device (design #276 boundary): a thin async wrapper over the
/// Home PC Server's §5 endpoints the canvas needs. Never calls an AI provider or Unity directly.
public actor ServerClient {
    private let baseURL: URL
    private let session: URLSession
    private let encoder = JSONEncoder()
    private let decoder = JSONDecoder()

    public init(baseURL: URL, session: URLSession = .shared) {
        self.baseURL = baseURL
        self.session = session
    }

    public enum ServerError: Error, Equatable {
        case http(Int)          // non-2xx status
        case emptyBody
    }

    // POST /canvas — upsert the layered canvas document; returns the stored form.
    public func saveCanvas(_ canvas: FacadeCanvas) async throws -> FacadeCanvas {
        try await post("canvas", body: canvas)
    }

    // GET /canvas/{osm_id}/{facade} — load a facade's canvas, or nil on 404.
    public func loadCanvas(osmId: Int, facade: String) async throws -> FacadeCanvas? {
        // appendingPathComponent percent-encodes as needed; don't double-encode.
        let path = "canvas/\(osmId)/\(facade)"
        do {
            return try await get(path) as FacadeCanvas
        } catch ServerError.http(404) {
            return nil
        }
    }

    // GET /canvas/{osm_id} — every facade canvas authored for a building.
    public func listCanvases(osmId: Int) async throws -> [FacadeCanvas] {
        try await get("canvas/\(osmId)")
    }

    // POST /ai/signs/generate — server-mediated; returns a reusable sign asset record.
    public func generateSign(_ request: SignRequest) async throws -> SignDef {
        try await post("ai/signs/generate", body: request)
    }

    // --- transport ---------------------------------------------------------

    private func get<T: Decodable>(_ path: String) async throws -> T {
        var req = URLRequest(url: baseURL.appendingPathComponent(path))
        req.httpMethod = "GET"
        return try await send(req)
    }

    private func post<B: Encodable, T: Decodable>(_ path: String, body: B) async throws -> T {
        var req = URLRequest(url: baseURL.appendingPathComponent(path))
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = try encoder.encode(body)
        return try await send(req)
    }

    private func send<T: Decodable>(_ req: URLRequest) async throws -> T {
        let (data, response) = try await session.data(for: req)
        guard let http = response as? HTTPURLResponse else { throw ServerError.emptyBody }
        guard (200..<300).contains(http.statusCode) else { throw ServerError.http(http.statusCode) }
        return try decoder.decode(T.self, from: data)
    }
}
